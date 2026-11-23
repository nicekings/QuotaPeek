using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace NhipPC
{
internal static partial class Checks
{
    static void DesktopOptions()
    {
        DesktopAccountFields();
        DesktopUsageWindow();
        const string week = "ai/codex/codex/10080", credit = "ai/codex/codex/credits",
                     session = "ai/claude/five_hour";
        Store.Write(Store.PathFor("preferences"), @"{""Widget"":true,""TrayKey"":""ai/codex/codex/10080"",
            ""Enabled"":{""ram"":false},""Corner"":""tl""}");
        var p = Store.Load();
        Check(p.ShowPc && p.ShowAi && p.Widget && p.WidgetStyle == "system" && p.Corner == "tl" &&
                  p.TrayItems == null && !p.On("ram"),
              "Old preferences retain widget placement, groups and individual choices");

        var sources = SampleAccounts();
        var codex = sources["codex"].Snapshot;
        Func<string, ProviderState> state = id => sources[id];
        var hardware =
            new HardwareFrame { Cpu = 8, Ram = 35, Receive = 45056, Send = 2560, Adapter = "Ethernet" };
        hardware.Volumes.Add(new Volume { Name = "C:", Total = 1000, Free = 650 });
        hardware.Volumes.Add(new Volume { Name = "D:", Total = 1000, Free = 400 });
        var choices = DisplayData.Choices(hardware, state);
        Check(DisplayData.VisibleTray(p, choices).Single().Key == week,
              "The original single tray preference selects the same reported period");
        p.ShowPc = false;
        p.Enabled[credit] = false;
        p.Enabled[session] = false;
        var compact = DisplayData.Rows(p, choices, state, true);
        Check(compact.Count == 2 && compact.All(row => row.Label.Contains("Tuần")) &&
                  compact.All(row => row.Kind == RowKind.Quota),
              "AI-only widget filters hardware, credit and a single 5-hour period");
        var detailed = DisplayData.Rows(p, choices, state, false);
        Check(!detailed.Any(row => row.Label == "Máy tính") &&
                  detailed.Count(row => row.Kind == RowKind.Quota) == 2 &&
                  detailed.Any(row => row.ActionId == "gemini") && detailed.Any(row => row.ActionId == "grok"),
              "Overview respects the same filters and offers source-specific actions when usage is missing");
        var codexRow = compact.Single(row => row.Label == "Codex · Tuần");
        var claudeRow = compact.Single(row => row.Label == "Claude · Tuần");
        Check(codexRow.Value == "Còn lại 7%" && codexRow.Percent == 7 && codex.Windows[0].Used == 93 &&
                  claudeRow.Value == "Còn lại 59%" && claudeRow.Percent == 59,
              "Every service shows its remaining quota while the stored reading keeps the used value");
        Check(codexRow.Level == 1 && claudeRow.Level == 0,
              "Twenty percent or less remaining is flagged for a warning colour");
        var detailedWeeks = detailed.Where(row => row.Kind == RowKind.Quota && row.Label == "Tuần").ToList();
        Check(detailedWeeks.Count == 2 && detailedWeeks[0].Value == "Còn lại 7%" &&
                  detailedWeeks[1].Value == "Còn lại 59%" &&
                  detailed.Single(row => row.Kind == RowKind.Section && row.Label == "Hạn mức AI").Value == "Phần còn lại",
              "The overview uses one direction for every service");
        p.Enabled[credit] = true;
        p.TrayItems = new List<string> { "ram", credit, week, session, "ai/codex/codex/300", week };
        var pinned = DisplayData.VisibleTray(p, choices);
        Check(pinned.Select(c => c.Key).SequenceEqual(new[] { credit, week }) && pinned[0].Number == "42" &&
                  pinned[0].Value == "42 credit" && pinned[0].Tip.Contains("42 credit") && pinned[0].Percent == null,
              "Multiple tray choices preserve order and units while excluding duplicates, hidden and " +
              "unreported periods");
        Check(pinned[1].Number == "7" && pinned[1].Percent == 7 && pinned[1].Name.Contains("còn lại") &&
                  pinned[1].Tip.Contains("Còn lại 7%"),
              "Taskbar icon, picker and tooltip identify the remaining Codex allowance");
        sources["codex"].Phase = Phase.Stale;
        var staleChoices = DisplayData.Choices(hardware, state);
        var stale = staleChoices.Single(c => c.Key == week);
        Check(stale.Stale && stale.Tip.Contains("Lần cuối") && stale.Number == "7" &&
                  DisplayData.Rows(p, staleChoices, state, true).Single(row => row.Label == "Codex · Tuần").Value == "~ Còn lại 7%",
              "Cached quota is labeled with the last read in the tray");
        foreach (int used in new[] { 0, 100 })
        {
            codex.Windows[0].Used = used;
            var edge = DisplayData.Choices(hardware, state).Single(c => c.Key == week);
            Check(edge.Value == "Còn lại " + (100 - used) + "%" && edge.Percent == 100 - used &&
                      edge.Number == (100 - used).ToString() && edge.Level == (used == 100 ? 2 : 0),
                  "Codex shows the true remaining balance at " + used + "% used");
        }
        sources["codex"].Phase = Phase.Error;
        var unknown = DisplayData.Choices(hardware, state).Single(c => c.Key == week);
        Check(unknown.Number == "—" && unknown.Percent == null && unknown.Tip.Contains("chưa xác nhận lại"),
              "Numbers that can no longer be confirmed are hidden on the taskbar but kept in the tooltip");
        sources["codex"].Phase = Phase.Stale;
        codex.Windows[0].Reset = Fmt.Now - 1;
        var expiredChoices = DisplayData.Choices(hardware, state);
        var expired = expiredChoices.Single(c => c.Key == week);
        Check(expired.Value == "Chờ cập nhật" && expired.Number == "—" && expired.Percent == null &&
                  DisplayData.Rows(p, expiredChoices, state, true).Single(row => row.Label == "Codex · Tuần").Value == "Chờ cập nhật",
              "An expired Codex week waits for a new sample rather than showing a replenished quota");
        codex.Windows.RemoveAt(0);
        Check(!DisplayData.VisibleTray(p, DisplayData.Choices(hardware, state)).Any(c => c.Key == week),
              "A retired plan window disappears from the taskbar on the next reading");
        p.ShowPc = true;
        p.ShowAi = false;
        p.Enabled["disk/D:"] = false;
        p.Enabled["net/tx"] = false;
        compact = DisplayData.Rows(p, choices, state, true);
        Check(compact.Select(r => r.Label).SequenceEqual(new[] { "CPU", "Ổ C:", "Tải xuống" }),
              "PC-only mode selects drives and one network direction independently");
        p.TrayItems.Clear();
        p.TrayOnly = true;
        p.WidgetStyle = "glass";
        p.GlassOpacity = 51;
        Store.Save(p);
        var loaded = Store.Load();
        Check(loaded.TrayOnly && loaded.TrayItems.Count == 0 && loaded.GlassOpacity == 51 &&
                  loaded.WidgetStyle == "glass" && loaded.Checked(credit) && !loaded.Checked(session) &&
                  !loaded.On(week),
              "Explicitly empty tray, appearance and nested choices survive restart");
        Check(DisplayData.VisibleTray(loaded, choices).Count == 0,
              "An empty tray selection never automatically pins another reading");
        loaded.ShowPc = false;
        Check(DisplayData.Rows(loaded, choices, state, true).Single().ActionId == "content",
              "Both groups can be hidden and the empty view opens content settings");
        using (var font = SystemFonts.MessageBoxFont)
            Check(Theme.Font(13, false).FontFamily.Name == font.FontFamily.Name,
                  "Widget and numeric labels use the Windows message font");
        WidgetAlpha();
    }

    static void DesktopAccountFields()
    {
        Func<bool, string[], Reading> read = (bounded, metadata) =>
        {
            var r = UsagePanel.Parse("gemini", "Gemini Chat Desktop", new[] { "Weekly limits", "14% used" });
            DesktopUsage.Identify(r, bounded, metadata);
            return r;
        };
        var fields = new[] { "Email: viewer@example.com", "Current plan: Pro" };
        var bound = read(true, fields);
        Check(AccountScope.From(bound) != null && bound.Plan == "Pro" &&
              bound.AccountKey == AccountScope.Key("gemini", "email", "viewer@example.com") &&
              bound.Windows.Single().Used == 14, "A labeled desktop panel binds quota and plan to its own account");
        Check(AccountScope.From(read(false, fields)) == null &&
              AccountScope.From(read(true, new[] { "viewer@example.com", "Pro" })) == null &&
              AccountScope.From(read(true, new[] { "Contact support: email help@example.com", "Plan: Pro" })) == null,
              "An unbounded window, arbitrary addresses and unlabeled tiers cannot verify desktop ownership");
        Check(AccountScope.From(read(true, fields.Concat(new[] { "Account: another@example.com" }).ToArray())) == null &&
              AccountScope.From(read(true, fields.Concat(new[] { "Plan: Max" }).ToArray())) == null,
              "Conflicting email and plan fields block a desktop fallback");
        var work = new[] { "Tài khoản: viewer@example.com", "Gói hiện tại: Team" };
        Check(AccountScope.From(read(true, work)) == null &&
              AccountScope.From(read(true, fields.Concat(new[] { "Workspace: Marketing" }).ToArray())) == null,
              "A managed desktop panel must expose the workspace identifier");
        var team = read(true, work.Concat(new[] { "Organization ID: org-123" }).ToArray());
        var another = read(true, work.Concat(new[] { "Organization ID: org-987" }).ToArray());
        Check(team.AccountKey == AccountScope.Key("gemini", "email", "viewer@example.com", "org-123") &&
              !AccountScope.From(team).Matches(another) && !AccountScope.From(team).Matches(bound),
              "Workspace membership and plan are part of the desktop account match");
    }

    sealed class UsageFixture : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        public UsageFixture(string title, params string[] rows)
        {
            Text = title;
            ShowInTaskbar = false;
            Opacity = 0;
            Size = new Size(330, 210);
            var panel = new GroupBox { Text = "Usage", AccessibleName = "Usage", Width = 295, Height = 120 };
            if (rows.Length == 0)
                rows = new[] { "Email: viewer@example.com", "Current plan: Pro", "Weekly limits · 14% used" };
            for (int i = 0; i < rows.Length; ++i)
                panel.Controls.Add(new Label { Text = rows[i], Top = 20 + i * 24, Left = 8, Width = 275 });
            Controls.Add(panel);
            Controls.Add(new Label { Text = "Email: another@example.com", Top = 126, Width = 275 });
        }
    }

    static void DesktopUsageWindow()
    {
        using (var settings = new UsageFixture("Settings"))
        using (var conversation = new UsageFixture("Conversation"))
        using (var missingAccount = new UsageFixture("Settings", "Weekly limits · 27% used"))
        using (var loading = new UsageFixture("Settings", "Loading…"))
        {
            settings.Show();
            conversation.Show();
            missingAccount.Show();
            loading.Show();
            var settingsId = settings.Handle;
            var conversationId = conversation.Handle;
            var missingAccountId = missingAccount.Handle;
            var loadingId = loading.Handle;
            Reading bound = null, other = null, noAccount = null, notReady = null, closed = null;
            Exception error = null;
            var reader = new Thread(delegate() {
                try
                {
                    bound = DesktopUsage.ReadWindows("gemini", new[] { settingsId });
                    other = DesktopUsage.ReadWindows("gemini", new[] { conversationId });
                    noAccount = DesktopUsage.ReadWindows("gemini", new[] { missingAccountId });
                    notReady = DesktopUsage.ReadWindows("gemini", new[] { loadingId });
                    closed = DesktopUsage.ReadWindows("gemini", new IntPtr[0]);
                }
                catch (Exception e) { error = e; }
            });
            reader.IsBackground = true;
            reader.SetApartmentState(ApartmentState.MTA);
            reader.Start();
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (reader.IsAlive && timeout.ElapsedMilliseconds < 10000)
            {
                Application.DoEvents();
                reader.Join(10);
            }
            Check(!reader.IsAlive && error == null, "Windows UI Automation completed the scoped panel reads");
            Check(bound.Valid("gemini") && bound.AccountKey == AccountScope.Key("gemini", "email", "viewer@example.com") &&
                  bound.Windows.Single().Used == 14, "The Gemini Usage control excludes an adjacent account in Settings");
            Check(!other.Valid("gemini") && other.AccountKey == null && other.Notice.Contains("Chưa thấy bảng hạn mức") &&
                  other.Notice.Contains("Settings → Usage Limits"),
                  "A conversation named Usage leads to navigation help and never provides an account");
            Check(!noAccount.Valid("gemini") && noAccount.Windows.Count == 0 && noAccount.Notice.Contains("Đã thấy số hạn mức"),
                  "An open quota without an identity returns no numbers");
            Check(!notReady.Valid("gemini") && notReady.Notice.Contains("đang chờ bảng"),
                  "A visible empty Usage panel is reported as waiting for quota values");
            Check(!closed.Valid("gemini") && closed.Notice == DesktopUsage.NoWindowNotice("gemini"),
                  "A closed or minimized Gemini window prompts the user to open or restore it");
        }
    }

    static Dictionary<string, ProviderState> SampleAccounts()
    {
        var sources = Catalog.Providers.ToDictionary(id => id, id => new ProviderState());
        var codex = Reading.Start("codex", "Codex App Server");
        codex.Add(new Quota { Key = "codex/10080", Label = "Tuần", Used = 93 });
        codex.Add(new Quota { Key = "codex/credits", Label = "Số dư credit", Balance = "42 credit" });
        var claude = Reading.Start("claude", "Claude Code");
        claude.Add(new Quota { Key = "five_hour", Label = "5 giờ", Used = 18 });
        claude.Add(new Quota { Key = "seven_day", Label = "Tuần", Used = 41 });
        sources["codex"] = new ProviderState { Phase = Phase.Live, Snapshot = codex };
        sources["claude"] = new ProviderState { Phase = Phase.Live, Snapshot = claude };
        return sources;
    }
    static List<ViewRow> SampleRows()
    {
        var sources = SampleAccounts();
        var prefs = new Preferences { ShowPc = false };
        prefs.Enabled["ai/codex/codex/credits"] = false;
        Func<string, ProviderState> state = id => sources[id];
        return DisplayData.Rows(prefs, DisplayData.Choices(new HardwareFrame(), state), state, true);
    }
    static void WidgetAlpha()
    {
        var rows = SampleRows();
        var size = new Size(252, WidgetPainter.Height(rows));
        Check(WidgetPainter.TextInks(rows, 1, 0, size.Height, (right, y) => right ? Color.Black : Color.White)
                  == new String('1', rows.Count), "Text ink adapts independently on the bright and dark sides of a window");
        using (var solid = WidgetPainter.Render(rows, "system", 51, 1, size, 0))
        using (var glass = WidgetPainter.Render(rows, "glass", 51, 1, size, 0))
        using (var text = WidgetPainter.Render(rows, "text", 51, 1, size, 0))
        {
            int x = size.Width / 2, y = size.Height / 2;
            Check(solid.GetPixel(x, y).A == 255, "System widget has an opaque readable panel");
            if (SystemInformation.HighContrast)
            {
                Check(glass.GetPixel(x, y).A == 255 && text.GetPixel(x, y).A == 255,
                      "Windows high contrast overrides transparency with the system panel");
                return;
            }
            Check(Math.Abs(glass.GetPixel(x, y).A - 130) <= 1 && text.GetPixel(x, y).A == 0,
                  "Glass uses actual per-pixel alpha and text-only clears the backdrop");
            bool legible = false;
            for (int px = 210; px < size.Width && !legible; px++)
                for (int py = 9; py < 32; py++)
                    if (glass.GetPixel(px, py).A >= 250 && text.GetPixel(px, py).A >= 250)
                    {
                        legible = true;
                        break;
                    }
            Check(legible, "Foreground numbers retain full opacity independently of the glass slider");
        }
    }

    static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    static void NativeControls()
    {
        var left = new PinChoice { Key = "cpu", Tip = "CPU · 8%", Number = "8" };
        var right = new PinChoice { Key = "ram", Tip = "RAM · 35%", Number = "35" };
        Check(TrayIndicators.Identity(left.Key) != TrayIndicators.Identity(right.Key) &&
                  TrayIndicators.Identity(right.Key) == TrayIndicators.Identity("ram"),
              "Each tray metric has its own stable Windows identity");
        using (var menu = new ContextMenuStrip())
        using (var tray = new TrayIndicators(delegate {}, menu))
        {
            tray.Update(new List<PinChoice> { left, right });
            Check(tray.RegisteredCount == 2, "Windows registered both numeric notification icons together");
            right.Number = "36";
            tray.Update(new List<PinChoice> { right });
            Check(tray.RegisteredCount == 1, "Removing one tray metric keeps the other registered");
            tray.Update(new List<PinChoice>());
            Check(tray.RegisteredCount == 1, "With no numeric readings Windows retains one launcher");
        }

        var p = new Preferences { ShowPc = true, ShowAi = false, Widget = true, TrayOnly = true,
                                  TrayItems = new List<string>() };
        foreach (string id in Catalog.Providers)
            p.Enabled[id] = false;
        Store.Save(p);
        using (var activation = new EventWaitHandle(false, EventResetMode.AutoReset))
        using (var app = new MonitorApp(activation, true))
        {
            try
            {
                Application.DoEvents();
                Check(!Application.OpenForms.Cast<Form>().Any(form => form.Visible),
                      "Tray-only startup suppresses the overview and a previously pinned widget");
                app.OpenDashboard();
                Application.DoEvents();
                var flyout = Application.OpenForms.OfType<Dashboard>().Single();
                Check(flyout.Visible && !flyout.ShowInTaskbar && Screen.FromControl(flyout).WorkingArea.Contains(flyout.Bounds),
                      "Tray-only activation opens a flyout within the monitor work area");
                var body = flyout.Controls.OfType<ReadoutView>().Single();
                var footer = flyout.Controls.OfType<Button>().Single();
                Check(!footer.Visible && body.AccessibilityObject.GetChild(0).Name.StartsWith("Máy tính"),
                      "A PC-only flyout shows detailed metrics directly");
                app.Prefs.ShowAi = true;
                app.Prefs.Enabled["claude"] = true;
                flyout.UpdateValues();
                Check(footer.Visible && body.AccessibilityObject.GetChild(0).Name.StartsWith("Hạn mức AI"),
                      "Mixed content puts AI first with a compact hardware footer");
                footer.PerformClick();
                Check(body.AccessibilityObject.GetChild(0).Name.StartsWith("Máy tính"),
                      "Expanding the footer exposes the selected PC details");
                // Transfer focus the same way as opening settings from a service link.
                app.OpenTarget("claude");
                Application.DoEvents();
                Check(!flyout.Visible, "Activating settings dismisses the flyout");
                Application.OpenForms.OfType<SettingsWindow>().Single().Close();
                app.Prefs.ShowAi = false;
                app.Prefs.Enabled["claude"] = false;
                var monitor = new Rectangle(-1920, 0, 1920, 1080);
                var usable = new Rectangle(-1920, 48, 1920, 1032);
                var at = Dashboard.AnchorPosition(monitor, usable, new Size(390, 500), 8, new Point(-1900, 20));
                Check(at.X == usable.Left && at.Y == usable.Top + 8,
                      "A tray on top of a secondary monitor anchors and clamps the flyout");
                var rows = SampleRows();
                using (var window = new FloatingWidget(app))
                using (var pixels = WidgetPainter.Render(rows, "glass", 51, 1, new Size(252, WidgetPainter.Height(rows)), 0))
                    Check(LayeredSurface.Present(window.Handle, Point.Empty, pixels) && !window.Visible,
                          "Windows accepted the per-pixel alpha surface on a real hidden widget handle");
                app.Prefs.Widget = false;
                using (var settings = new SettingsWindow(app))
                {
                    var pages = Enumerable.Range(0, 5).Select(i => settings.Controls.Find("section" + i, true).Single()).ToArray();
                    var navigation = Descendants(settings).OfType<SettingsNavItem>().ToArray();
                    Check(navigation.Length == 5, "Each settings section has a named navigation item");
                    settings.SelectTarget("about");
                    var website = Descendants(pages[4]).OfType<LinkLabel>().Single();
                    Check(navigation[4].Checked && navigation[4].Text == "Giới thiệu" && website.TabStop &&
                          website.Text == "https://premiumvns.com" && Object.Equals(website.Links[0].LinkData, website.Text),
                          "About provides a keyboard-accessible link to the requested website");
                    settings.SelectTarget("content");
                    var pc = Descendants(pages[0]).OfType<CheckBox>().Single(box => box.Text == "Máy tính");
                    var ai =
                        Descendants(pages[0]).OfType<CheckBox>().Single(box => box.Text == "Hạn mức AI");
                    ai.Checked = true;
                    pc.Checked = false;
                    settings.UpdateValues();
                    Check(!app.Prefs.ShowPc && app.Prefs.ShowAi &&
                              !Descendants(pages[0]).OfType<CheckedListBox>()
                                   .First(box => box.AccessibleName == "Chỉ số máy tính")
                                   .Enabled,
                          "Content master switches apply immediately and reflect the AI-only state");
                    pc.Checked = true;
                    settings.SelectTarget("tray");
                    var trayPage = (Panel)pages[1];
                    var layout = Descendants(trayPage);
                    var trayMode = layout.OfType<ComboBox>().Single(box => box.AccessibleName == "Kiểu biểu tượng AI");
                    var ringSize = layout.OfType<ComboBox>().Single(box => box.AccessibleName == "Kích thước vòng");
                    settings.Show();
                    var fullSize = settings.ClientSize;
                    settings.ClientSize = new Size(Ui.D(Theme.Scale(settings), 410), fullSize.Height);
                    var selector = settings.Controls.OfType<ComboBox>().Single();
                    selector.SelectedIndex = 4;
                    Check(selector.Visible && pages[4].Visible && navigation[4].Checked,
                          "Compact navigation selects the same About section");
                    settings.ClientSize = fullSize;
                    navigation[1].PerformClick();
                    Check(pages[1].Visible && navigation[1].Checked, "The sidebar opens the tray settings page");
                    trayMode.SelectedIndex = 2;
                    settings.UpdateValues();
                    Application.DoEvents();
                    var hint = layout.OfType<Label>().Single(label => label.Text.StartsWith("Vòng ngoài và số giữa"));
                    Check(ringSize.Visible && ringSize.Text == "Lớn" && !trayPage.HorizontalScroll.Visible &&
                          hint.Height >= hint.GetPreferredSize(new Size(hint.Width, 0)).Height,
                          "Ring settings fit the tab width and wrap the complete explanation");
                    Check(ringSize.Items.Cast<string>().SequenceEqual(new[] { "Vừa", "Lớn" }),
                          "The ring size picker offers only medium and large");
                    ringSize.SelectedIndex = 0;
                    Check(!Store.Load().LargeTrayRing, "Choosing medium persists the ring size");
                    trayMode.SelectedIndex = 0;
                    settings.UpdateValues();
                    Check(!ringSize.Visible, "Ring size is shown only for the ring style");
                    trayMode.SelectedIndex = 2;
                    settings.UpdateValues();
                    Check(ringSize.Text == "Vừa", "Switching back to rings restores the selected size");
                    ringSize.SelectedIndex = 1;
                    Check(Store.Load().GetRingSize() == RingSize.Large && ringSize.Text == "Lớn",
                          "Choosing large persists the full-size ring");
                    settings.Hide();
                    var list = layout.OfType<CheckedListBox>().Single();
                    var ram =
                        Enumerable.Range(0, list.Items.Count).Single(i => list.Items[i].ToString() == "RAM");
                    list.SetItemChecked(ram, true);
                    Application.DoEvents();
                    Check(Store.Load().TrayItems.SequenceEqual(new[] { "ram" }),
                          "Native multi-select taskbar control saves the exact chosen metric");
                    app.Prefs.Enabled["claude"] = true;
                    app.Prefs.ShowAi = true;
                    app.Hub["claude"].Phase = Phase.Live;
                    app.Hub["claude"].Snapshot = SampleAccounts()["claude"].Snapshot;
                    app.Prefs.TrayItems = new List<string> { "ai/claude/five_hour", "ai/claude/seven_day" };
                    app.Apply();
                    settings.UpdateValues();
                    var center = layout.OfType<ComboBox>().Single(box => box.AccessibleName == "Số giữa");
                    var outer = layout.OfType<ComboBox>().Single(box => box.AccessibleName == "Vòng ngoài");
                    center.SelectedIndex = 2;
                    outer.SelectedIndex = 1;
                    var savedFields = Store.Load().RingFieldsByProvider["claude"];
                    Check(savedFields.Center == "ai/claude/seven_day" && savedFields.Outer == "ai/claude/five_hour",
                          "The native pickers persist independent, swapped assignments for Claude");
                    app.Prefs.Enabled["claude"] = false;
                    app.Prefs.TrayItems = new List<string> { "ram" };
                    app.Apply();
                    var only = layout.OfType<CheckBox>().Single();
                    only.Checked = false;
                    only.Checked = true;
                    Check(Store.Load().TrayOnly &&
                              !Application.OpenForms.Cast<Form>().Any(form => form.Visible),
                          "Tray-only control persists without opening a dashboard");
                    settings.SelectTarget("style");
                    var picker = Descendants(pages[2]).OfType<ComboBox>().Single(
                        box => box.AccessibleName == "Kiểu hiển thị");
                    foreach (var style in new[] { "tiles", "meters", "text" })
                    {
                        picker.SelectedIndex = Array.IndexOf(WidgetPainter.Styles, style);
                        Check(Store.Load().WidgetStyle == style, "The widget picker saves and reloads " + style);
                        app.Prefs.Widget = true;
                        app.Prefs.TrayOnly = false;
                        app.Apply();
                        var widget = Application.OpenForms.OfType<FloatingWidget>().Single();
                        var live = app.Rows(true);
                        Check(widget.Visible && widget.Width == Ui.D(Theme.Scale(widget), WidgetPainter.Width(live, style)),
                              "The desktop widget uses the selected layout for " + style);
                    }
                    var textColor = Descendants(pages[2]).OfType<ComboBox>().Single(box => box.AccessibleName == "Màu chữ widget");
                    textColor.SelectedIndex = 2;
                    Check(Store.Load().WidgetTextColor == "dark", "Text-only foreground choice survives reload");
                    app.Prefs.Widget = false;
                    app.Apply();
                    picker.SelectedIndex = Array.IndexOf(WidgetPainter.Styles, "glass");
                    var slider = Descendants(pages[2]).OfType<TrackBar>().Single();
                    slider.Value = 45;
                    Check(Store.Load().WidgetStyle == "glass" && Store.Load().GlassOpacity == 45,
                          "Native style and glass controls persist their settings");
                    settings.SelectTarget("claude");
                    var source = Descendants(pages[3]).OfType<ComboBox>().Single();
                    Check(
                        navigation[3].Checked && source.SelectedItem.ToString() == "Claude" &&
                            Descendants(pages[3]).OfType<Label>().Any(label => label.Text.Contains("/usage")) &&
                            Descendants(pages[3]).OfType<CheckBox>().Count() == 1,
                        "Opening Claude shows the background /usage card with its single status line option");
                    settings.SelectTarget("gemini");
                    Check(source.SelectedItem.ToString() == "Gemini Chat" &&
                              Descendants(pages[3]).OfType<Label>().Any(
                                  label => label.Text.Contains("Usage Limits")) &&
                              !Descendants(pages[3]).OfType<CheckBox>().Any(),
                          "Gemini Chat explains its open-panel requirement and lists only supported " +
                          "connection actions");
                }
                app.OpenTarget("style");
                foreach (var code in new[] { "en", "vi" })
                {
                    var dialog = Application.OpenForms.OfType<SettingsWindow>().Single();
                    var picker = Descendants(dialog).OfType<ComboBox>().Single(box => box.Items.Contains("English"));
                    picker.SelectedIndex = code == "en" ? 1 : 0;
                    typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic).Invoke(picker, new object[] { EventArgs.Empty });
                    Application.DoEvents();
                    dialog = Application.OpenForms.OfType<SettingsWindow>().Single();
                    Check(Store.Load().Language == code && dialog.Text == (code == "en" ? "QuotaPeek settings" : "Cài đặt QuotaPeek"),
                          "Changing the language updates settings immediately and persists " + code);
                    Check(app.Menu.Items[0].Text == (code == "en" ? "Open dashboard" : "Mở bảng số liệu") &&
                          SampleRows().Any(row => row.Label == (code == "en" ? "Claude · Weekly" : "Claude · Tuần")),
                          "Tray menu and cached quota labels follow the selected language " + code);
                }
            }
            finally
            {
                app.ExitThread();
            }
        }
    }
}
}
