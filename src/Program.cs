using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NhipPC
{
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && (args[0] == "--status-line" || args[0] == "--tap"))
            return StatusLine.Receive(args[1], args[0] == "--tap");
        if (args.Length == 2 && (args[0] == "--read-window" || args[0] == "--read-cli"))
        {
            string id = args[1];
            bool cli = args[0] == "--read-cli";
            // Gemini Chat is read from its open window; the other polled services from their CLI.
            if (!Catalog.Providers.Contains(id) || id == "codex" || cli == (id == "gemini"))
                return 2;
            if (cli && !WorkerJob.ContainCurrent())
                return 1;
            Reading result = null;
            var worker = new Thread(delegate() {
                try
                {
                    result = cli ? CliUsage.Read(id) : DesktopUsage.Read(id);
                }
                catch (Exception)
                {
                }
            });
            worker.SetApartmentState(ApartmentState.MTA);
            worker.IsBackground = true;
            worker.Start();
            if (!worker.Join(cli ? 45000 : 6000) || result == null)
                return 1;
            using (var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)))
                writer.Write(Json.Encode(result));
            return 0;
        }
        try
        {
            Directory.CreateDirectory(Store.Root);
            string scope = Path.GetFullPath(Store.Root).ToUpperInvariant().GetHashCode().ToString("X8");
            bool primary;
            using (var m = new Mutex(true, @"Local\NhipPC.Main." + scope, out primary)) using (
                var wake =
                    new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\NhipPC.Show." + scope))
            {
                if (!primary)
                {
                    wake.Set();
                    return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new MonitorApp(wake, !args.Contains("--tray"))) Application.Run(app);
                m.ReleaseMutex();
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(UiText.Pick("Không thể mở QuotaPeek.\n", "Could not open QuotaPeek.\n") + ex.Message, "QuotaPeek", MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed class MonitorApp : ApplicationContext
{
    public Preferences Prefs { get; private set; }
    public UsageHub Hub { get; private set; }
    public HardwareFrame Hardware { get; private set; }
    public ContextMenuStrip Menu { get; private set; }
    public List<PinChoice> Choices { get; private set; }
    readonly Control dispatcher = new Control();
    readonly HardwareSampler sampler = new HardwareSampler();
    readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 1000 };
    Dashboard dashboard;
    readonly FloatingWidget widget;
    readonly TrayIndicators tray;
    readonly RegisteredWaitHandle wake;
    SettingsWindow settings;
    bool sampling, closing, hadSensors;
    public MonitorApp(EventWaitHandle activation, bool show)
    {
        var h = dispatcher.Handle;
        Prefs = Store.Load();
        UiText.SetLanguage(Prefs.Language);
        Theme.Refresh();
        Hardware = new HardwareFrame();
        foreach (string id in new[] { "claude", "antigravity" })
            try { StatusLine.Repair(id); }
            catch (Exception) { /* A locked CLI settings file is retried on the next start. */ }
        Hub = new UsageHub(Prefs, Post);
        Choices = DisplayData.Choices(Hardware, id => Hub[id], Prefs.ResetClock);
        dashboard = new Dashboard(this);
        widget = new FloatingWidget(this);
        Menu = new ContextMenuStrip { ShowImageMargin = false, Font = SystemFonts.MenuFont };
        BuildMenu();
        Menu.Opening += delegate {
            Menu.Items["widget"].Text = Prefs.Widget && !Prefs.TrayOnly
                ? UiText.Pick("Ẩn widget", "Hide widget") : UiText.Pick("Ghim widget", "Pin widget");
        };
        tray = new TrayIndicators(point => dashboard.Present(point), Menu);
        wake = ThreadPool.RegisterWaitForSingleObject(activation, delegate { Post(OpenDashboard); }, null, -1,
                                                      false);
        Hub.Changed += UpdateViews;
        clock.Tick += delegate
        {
            Tick();
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.UserPreferenceChanged += UserChanged;
        clock.Start();
        Post(delegate {
            Tick();
            SyncWidget();
            if (show && !Prefs.TrayOnly)
                OpenDashboard();
        });
    }
    void BuildMenu()
    {
        while (Menu.Items.Count > 0) Menu.Items[0].Dispose();
        Menu.Items.Add(UiText.Pick("Mở bảng số liệu", "Open dashboard"), null, delegate { OpenDashboard(); });
        Menu.Items.Add(UiText.Pick("Chọn chỉ số cạnh đồng hồ…", "Choose taskbar metrics…"), null, delegate { OpenTarget("tray"); });
        Menu.Items.Add(UiText.Pick("Cài đặt…", "Settings…"), null, delegate { OpenSettings(); });
        var pin = Menu.Items.Add(UiText.Pick("Ghim widget", "Pin widget"), null, delegate { ToggleWidget(); });
        pin.Name = "widget";
        Menu.Items.Add(new ToolStripSeparator());
        Menu.Items.Add(UiText.Pick("Giới thiệu QuotaPeek", "About QuotaPeek"), null, delegate { OpenTarget("about"); });
        Menu.Items.Add(UiText.Pick("Thoát QuotaPeek", "Quit QuotaPeek"), null, delegate { ExitThread(); });
    }
    public void SetLanguage(string language)
    {
        if (Prefs.Language == language) return;
        Prefs.Language = language;
        UiText.SetLanguage(language);
        SavePosition();
        BuildMenu();
        dashboard.Dispose();
        dashboard = new Dashboard(this);
        widget.InvalidateStyle();
        if (settings != null && !settings.IsDisposed)
        {
            var bounds = settings.WindowState == FormWindowState.Normal ? settings.Bounds : settings.RestoreBounds;
            var state = settings.WindowState;
            settings.Dispose();
            settings = new SettingsWindow(this);
            settings.StartPosition = FormStartPosition.Manual;
            settings.Bounds = bounds;
            settings.WindowState = state;
            OpenTarget("style");
        }
        UpdateViews();
    }
    public List<ViewRow> Rows(bool compact)
    {
        return DisplayData.Rows(Prefs, Choices, id => Hub[id], compact);
    }
    void Post(Action action)
    {
        if (closing || dispatcher.IsDisposed)
            return;
        try
        {
            dispatcher.BeginInvoke(new Action(delegate {
                if (!closing)
                    action();
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }
    void Tick()
    {
        Hub.Tick();
        bool sensors = Catalog.Sensors.Any(Prefs.On);
        if (sampling || (!sensors && !hadSensors))
        {
            UpdateViews();
            return;
        }
        sampling = true;
        hadSensors = sensors;
        var p =
            new Preferences { ShowPc = Prefs.ShowPc, Enabled = new Dictionary<string, bool>(Prefs.Enabled),
                              NetworkId = Prefs.NetworkId };
        Task.Run(delegate {
            HardwareFrame frame = null;
            try
            {
                lock (sampler) frame = sampler.Sample(p);
            }
            catch (Exception)
            {
            }
            Post(delegate {
                sampling = false;
                if (frame != null)
                    Hardware = frame;
                UpdateViews();
            });
        });
    }
    void UpdateViews()
    {
        if (closing)
            return;
        Choices = DisplayData.Choices(Hardware, id => Hub[id], Prefs.ResetClock);
        if (widget.Visible)
            widget.UpdateValues();
        if (dashboard.Visible)
            dashboard.UpdateValues();
        if (settings != null && !settings.IsDisposed && settings.Visible)
            settings.UpdateValues();
        tray.Update(DisplayData.TrayIcons(Prefs, Choices));
    }
    // Ask Windows to show QuotaPeek's icons next to the clock instead of under "^".
    public int PromoteTray()
    {
        int count = TrayIndicators.Promote();
        tray.Reregister();
        tray.Update(DisplayData.TrayIcons(Prefs, Choices));
        return count;
    }
    public void ToggleWidget()
    {
        bool enable = Prefs.TrayOnly || !Prefs.Widget;
        if (enable)
            Prefs.TrayOnly = false;
        Prefs.Widget = enable;
        Apply();
    }
    public void SetTrayOnly(bool on)
    {
        Prefs.TrayOnly = on;
        if (on)
            dashboard.Hide();
        Apply();
    }
    public void SavePosition()
    {
        try
        {
            Store.Save(Prefs);
        }
        catch (IOException ex)
        {
            MessageBox.Show(ex.Message, "QuotaPeek");
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(ex.Message, "QuotaPeek");
        }
    }
    void SyncWidget()
    {
        if (!Prefs.Widget || Prefs.TrayOnly)
        {
            widget.Hide();
            return;
        }
        widget.Place();
        if (!widget.Visible)
            widget.Show();
    }
    public void Apply()
    {
        SavePosition();
        Hub.Apply(Prefs);
        SyncWidget();
        UpdateViews();
    }
    public Point PanelLocation(Point preferred, Size size, Rectangle area, int gap)
    {
        var at = Ui.Clamp(preferred, size, area);
        if (!widget.Visible || !new Rectangle(at, size).IntersectsWith(widget.Bounds))
            return at;
        var b = widget.Bounds;
        foreach (var pos in new[] { new Point(b.Left - size.Width - gap, at.Y),
                                    new Point(b.Right + gap, at.Y),
                                    new Point(at.X, b.Top - size.Height - gap),
                                    new Point(at.X, b.Bottom + gap) })
        {
            var pt = Ui.Clamp(pos, size, area);
            if (!new Rectangle(pt, size).IntersectsWith(b))
                return pt;
        }
        return at;
    }
    public void OpenDashboard()
    {
        dashboard.Present();
    }
    public void OpenSettings()
    {
        OpenTarget("content");
    }
    public void OpenTarget(string id)
    {
        if (settings == null || settings.IsDisposed)
            settings = new SettingsWindow(this);
        settings.SelectTarget(id);
        settings.TopMost = true;
        Ui.ShowInteractive(settings);
    }
    public void OpenLink(string link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(ex.Message, "QuotaPeek");
        }
    }
    void DisplayChanged(object sender, EventArgs e)
    {
        Post(delegate {
            widget.InvalidateStyle();
            UpdateViews();
        });
    }
    void UserChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Post(delegate {
            Theme.Refresh();
            widget.InvalidateStyle();
            if (settings != null && !settings.IsDisposed)
                settings.SystemThemeChanged();
            UpdateViews();
        });
    }
    void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Post(delegate {
                Hub.Refresh(null, userInitiated: false);
                Tick();
            });
    }
    protected override void ExitThreadCore()
    {
        if (closing)
            return;
        closing = true;
        clock.Stop();
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.UserPreferenceChanged -= UserChanged;
        wake.Unregister(null);
        Hub.Dispose();
        tray.Dispose();
        if (settings != null)
            settings.Dispose();
        widget.Dispose();
        dashboard.Dispose();
        Menu.Dispose();
        clock.Dispose();
        lock (sampler) sampler.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }
}
}
