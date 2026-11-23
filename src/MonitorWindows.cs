using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NhipPC
{
internal static class Ui
{
    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    public static void ShowInteractive(Form window)
    {
        if (window.WindowState == FormWindowState.Minimized)
            window.WindowState = FormWindowState.Normal;
        if (!window.Visible)
            window.Show();
        // A hidden startup hint can leave WinForms Visible true while the HWND is still hidden.
        if (!IsWindowVisible(window.Handle))
            ShowWindow(window.Handle, 5); // SW_SHOW
        window.BringToFront();
        window.Activate();
    }
    public static int D(float scale, float value)
    {
        return (int)Math.Round(scale * value);
    }
    public static void Place(Control c, float s, int x, int y, int w, int h)
    {
        c.SetBounds(D(s, x), D(s, y), D(s, w), D(s, h));
    }
    public static Point Clamp(Point p, Size size, Rectangle area)
    {
        return new Point(Math.Max(area.Left, Math.Min(area.Right - size.Width, p.X)),
                         Math.Max(area.Top, Math.Min(area.Bottom - size.Height, p.Y)));
    }
    public static Button Button(string label, string name, Action action)
    {
        var b = new Button { Text = label, AccessibleName = name, Cursor = Cursors.Hand,
                             UseVisualStyleBackColor = true };
        b.Click += delegate
        {
            action();
        };
        return b;
    }
}

internal sealed class ReadoutView : ScrollableControl
{
    readonly Action<string> activate;
    List<ViewRow> rows = new List<ViewRow>();
    int focus = -1;
    public int DesiredHeight { get; private set; }
    public ReadoutView(Action<string> activate)
    {
        this.activate = activate;
        DoubleBuffered = true;
        ResizeRedraw = true;
        AutoScroll = true;
        TabStop = true;
        SetStyle(ControlStyles.Selectable, true);
        AccessibleName = UiText.Pick("Bảng số liệu và kết nối", "Usage and connections");
        Cursor = Cursors.Default;
    }
    // Logical row boundaries are shared by painting, keyboard navigation and hit testing.
    int[] tops = { 6 };
    static int ValueHeight(ViewRow row)
    {
        if (row.Kind == RowKind.Section) return 37;
        if (row.Kind == RowKind.Provider) return 30;
        return row.Kind == RowKind.Quota && row.Percent.HasValue ? 34 : 26;
    }
    public void SetRows(List<ViewRow> next, int logicalWidth)
    {
        string action = focus >= 0 && focus < rows.Count ? rows[focus].ActionId : null;
        rows = next;
        BackColor = Theme.Base;
        float scale = Theme.Scale(this);
        tops = new int[rows.Count + 1];
        tops[0] = 6;
        using (var g = CreateGraphics())
        {
            // Reserve scrollbar space so notes still fit when the flyout reaches its height cap.
            int noteWidth = Math.Max(1, logicalWidth - 52 - (int)Math.Ceiling(SystemInformation.VerticalScrollBarWidth / scale));
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                int noteHeight = String.IsNullOrEmpty(r.Note) ? 0 :
                    (int)Math.Ceiling(g.MeasureString(r.Note, Theme.Font(12, false), noteWidth).Height) + 2;
                int gap = i == 0 || r.Kind != RowKind.Provider ? 0 : 6;
                tops[i + 1] = tops[i] + gap + ValueHeight(r) + noteHeight + 5;
            }
        }
        DesiredHeight = Ui.D(scale, tops[rows.Count] + 8);
        if (AutoScrollMinSize.Height != DesiredHeight)
            AutoScrollMinSize = new Size(0, DesiredHeight);
        focus = action == null ? -1 : rows.FindIndex(r => r.ActionId == action);
        if (Focused && focus < 0) focus = rows.FindIndex(r => r.ActionId != null);
        Invalidate();
    }
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (focus < 0)
            focus = rows.FindIndex(r => r.ActionId != null);
        Invalidate();
    }
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }
    int Hit(int y)
    {
        float pos = (y - AutoScrollPosition.Y) / Theme.Scale(this);
        for (int i = 0; i < rows.Count; i++)
            if (pos >= tops[i] && pos < tops[i + 1])
                return i;
        return -1;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Hit(e.Y);
        Cursor = i >= 0 && rows[i].ActionId != null ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        int i = Hit(e.Y);
        if (e.Button == MouseButtons.Left && i >= 0 && rows[i].ActionId != null)
            activate(rows[i].ActionId);
    }
    protected override bool IsInputKey(Keys key)
    {
        return key == Keys.Up || key == Keys.Down || base.IsInputKey(key);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && focus >= 0)
        {
            activate(rows[focus].ActionId);
            e.Handled = true;
        }
        if (e.KeyCode != Keys.Down && e.KeyCode != Keys.Up)
            return;
        var links = Enumerable.Range(0, rows.Count).Where(i => rows[i].ActionId != null).ToList();
        if (links.Count == 0)
            return;
        int index = links.IndexOf(focus);
        focus = links[(index < 0 ? (e.KeyCode == Keys.Up ? links.Count - 1 : 0)
                                 : (index + (e.KeyCode == Keys.Up ? -1 : 1) + links.Count) % links.Count)];
        int y = Ui.D(Theme.Scale(this), tops[focus]);
        if (y < -AutoScrollPosition.Y ||
            y + Ui.D(Theme.Scale(this), tops[focus + 1] - tops[focus]) > Height - AutoScrollPosition.Y)
            AutoScrollPosition = new Point(0, y);
        Invalidate();
        e.Handled = true;
    }
    protected override AccessibleObject CreateAccessibilityInstance()
    {
        return new RowList(this);
    }
    sealed class RowList : ControlAccessibleObject
    {
        readonly ReadoutView owner;
        public RowList(ReadoutView v) : base(v)
        {
            owner = v;
        }
        public override AccessibleRole Role
        {
            get {
                return AccessibleRole.List;
            }
        }
        public override int GetChildCount()
        {
            return owner.rows.Count;
        }
        public override AccessibleObject GetChild(int i)
        {
            return i < 0 || i >= owner.rows.Count ? null : new Item(this, owner.rows[i], owner.activate);
        }
    }
    sealed class Item : AccessibleObject
    {
        readonly AccessibleObject parent;
        readonly ViewRow row;
        readonly Action<string> open;
        public Item(AccessibleObject p, ViewRow r, Action<string> a)
        {
            parent = p;
            row = r;
            open = a;
        }
        public override AccessibleObject Parent
        {
            get {
                return parent;
            }
        }
        public override AccessibleRole Role
        {
            get {
                return row.ActionId == null ? AccessibleRole.StaticText : AccessibleRole.Link;
            }
        }
        public override string Name
        {
            get {
                return String.Join(
                    " · ", new[] { row.Label, row.Value, row.Note }.Where(v => !String.IsNullOrEmpty(v)));
            }
            set {
            }
        }
        public override string DefaultAction
        {
            get {
                return row.ActionId == null ? null : UiText.Pick("Mở", "Open");
            }
        }
        public override void DoDefaultAction()
        {
            if (row.ActionId != null)
                open(row.ActionId);
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        float scale = Theme.Scale(this);
        g.TranslateTransform(0, AutoScrollPosition.Y);
        g.ScaleTransform(scale, scale);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = ClientSize.Width / scale;
        Color service = Theme.Accent;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            bool provider = r.Kind == RowKind.Provider, section = r.Kind == RowKind.Section;
            float y = tops[i] + (provider && i > 0 ? 6 : 0);
            int head = ValueHeight(r);
            if (provider)
            {
                int end = i + 1;
                while (end < rows.Count && rows[end].Kind == RowKind.Quota) end++;
                Frame(g, 10, y, w - 20, tops[end] - y);
                service = SystemInformation.HighContrast ? Theme.Text : TrayIndicators.Accent(r.ProviderId) ?? Theme.Accent;
                using (var brush = new SolidBrush(service)) g.FillEllipse(brush, 23, y + 13, 7, 7);
            }
            float x = section ? 16 : provider ? 37 : 23;
            float right = w - (section ? 16 : 23);
            int font = section || provider ? 12 : 14;
            float valueWidth = Math.Min(w * .55f, g.MeasureString(r.Value ?? "", Theme.Font(font, !section && !provider)).Width + 5);
            float lineHeight = r.Kind == RowKind.Quota ? 25 : head;
            Theme.Words(g, r.Label, x, y, right - x - valueWidth - 6, lineHeight,
                        section ? 19 : 14, Theme.Text, section || provider, false);
            Theme.Words(g, r.Value, right - valueWidth, y, valueWidth, lineHeight, font,
                        r.ActionId != null ? Theme.Accent : section || provider ? Theme.Muted : Theme.ForLevel(r.Level, Theme.Text),
                        !section && !provider, true);
            if (r.Kind == RowKind.Quota && r.Percent.HasValue)
            {
                float width = Math.Max(0, right - x);
                using (var track = new SolidBrush(Theme.Edge)) g.FillRectangle(track, x, y + 25, width, 4);
                using (var fill = new SolidBrush(Theme.ForLevel(r.Level, service)))
                    g.FillRectangle(fill, x, y + 25, (float)(width * r.Percent.Value / 100), 4);
            }
            if (!String.IsNullOrEmpty(r.Note))
                using (var brush = new SolidBrush(Theme.Muted))
                    g.DrawString(r.Note, Theme.Font(12, false), brush,
                                 new RectangleF(23, y + head, Math.Max(1, w - 46), tops[i + 1] - y - head));
            if (Focused && i == focus)
                using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                    g.DrawRectangle(pen, 14, y + 2, Math.Max(1, w - 28), tops[i + 1] - y - 4);
        }
    }
    static void Frame(Graphics g, float x, float y, float width, float height)
    {
        const int d = 16;
        if (width < d) return;
        using (var path = new GraphicsPath())
        using (var fill = new SolidBrush(Theme.Surface))
        using (var edge = new Pen(Theme.Edge))
        {
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }
    }
}

internal sealed class Dashboard : Form
{
    readonly MonitorApp app;
    readonly ReadoutView view;
    readonly Panel head = new Panel();
    readonly Button pin, settings, close, pc;
    bool pcExpanded;
    Point? trayAnchor;
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int rounded = 2;
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
    }
    readonly Icon brandIcon;
    readonly Bitmap mark;
    Screen screen;
    bool laying;
    public Dashboard(MonitorApp app)
    {
        this.app = app;
        Text = "QuotaPeek";
        Name = "QuotaPeek.Dashboard";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Icon = brandIcon = Brand.Icon(64);
        mark = brandIcon.ToBitmap();
        Padding = new Padding(1);
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        view = new ReadoutView(app.OpenTarget) { Dock = DockStyle.Fill };
        head.Dock = DockStyle.Top;
        pc = Ui.Button("", UiText.Pick("Chỉ số máy tính", "PC metrics"), delegate { pcExpanded = !pcExpanded; UpdateValues(); view.AutoScrollPosition = Point.Empty; });
        pc.Dock = DockStyle.Bottom;
        pc.TextAlign = ContentAlignment.MiddleLeft;
        pc.AutoEllipsis = true;
        Controls.Add(view);
        Controls.Add(pc);
        Controls.Add(head);
        pin = Ui.Button(UiText.Pick("Ghim", "Pin"), UiText.Pick("Ghim hoặc ẩn widget", "Show or hide widget"), app.ToggleWidget);
        settings = Ui.Button(UiText.Pick("Cài đặt", "Settings"), UiText.Pick("Mở cài đặt", "Open settings"), app.OpenSettings);
        close = Ui.Button("×", UiText.Pick("Ẩn bảng", "Hide panel"), Hide);
        head.Controls.AddRange(new Control[] { pin, settings, close });
        foreach (Button button in new[] { pin, settings, close, pc })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
        }
        Deactivate += delegate { if (Visible) Hide(); };
        head.Paint += delegate(object sender, PaintEventArgs e)
        {
            float s = Theme.Scale(this);
            e.Graphics.ScaleTransform(s, s);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(mark, new Rectangle(13, 12, 21, 21));
            Theme.Words(e.Graphics, "QuotaPeek", 40, 0, 130, 45, 19, Theme.Text, true, false);
            Theme.Line(e.Graphics, Theme.Edge, 0, 44, head.Width / s, 44);
        };
        KeyDown += delegate(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
                Hide();
            if (e.KeyCode == Keys.F5)
                app.Hub.Refresh(null);
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }
    public void Present(Point? anchor = null)
    {
        trayAnchor = anchor;
        screen = Screen.FromPoint(anchor ?? Cursor.Position);
        if (!Visible || Screen.FromControl(this).DeviceName != screen.DeviceName)
            Location = new Point(screen.WorkingArea.Right - 120, screen.WorkingArea.Bottom - 100);
        var h = Handle;
        UpdateValues();
        Ui.ShowInteractive(this);
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            if (mark != null)
                mark.Dispose();
            if (brandIcon != null)
                brandIcon.Dispose();
        }
    }
    internal static Point AnchorPosition(Rectangle screen, Rectangle area, Size size, int gap, Point? anchor)
    {
        int x = area.Right - size.Width - gap, y = area.Bottom - size.Height - gap;
        if (screen.Left < area.Left || screen.Right > area.Right)
        {
            if (screen.Left < area.Left) x = area.Left + gap;
            if (anchor.HasValue) y = anchor.Value.Y - size.Height / 2;
        }
        else
        {
            if (screen.Top < area.Top) y = area.Top + gap;
            if (anchor.HasValue) x = anchor.Value.X - size.Width / 2;
        }
        return Ui.Clamp(new Point(x, y), size, area);
    }
    public void UpdateValues()
    {
        if (laying)
            return;
        laying = true;
        try
        {
            screen =
                (screen == null ? null
                                : Screen.AllScreens.FirstOrDefault(m => m.DeviceName == screen.DeviceName)) ??
                Screen.FromControl(this);
            var area = screen.WorkingArea;
            float s = Theme.Scale(this);
            BackColor = Theme.Edge;
            head.BackColor = Theme.Base;
            var rows = app.Rows(false);
            var metrics = rows.Where(r => r.Kind == RowKind.Metric && r.ActionId == null).ToList();
            bool mixed = metrics.Count > 0 && rows.Any(r => r.Kind == RowKind.Provider);
            pc.Visible = mixed;
            if (mixed)
            {
                // Detailed rows already start with the hardware section, followed by its metrics.
                if (!pcExpanded) rows.RemoveRange(0, metrics.Count + 1);
                var summary = app.Choices.Where(c => app.Prefs.On(c.Key) &&
                                             (c.Key == "cpu" || c.Key == "ram" || c.Key == "net/rx"))
                    .Select(c => (c.Key == "net/rx" ? "↓" : c.Label) + " " + c.Value);
                string line = String.Join("    ·    ", summary);
                pc.Text = (pcExpanded ? "⌃   " : "›   ") + (line.Length == 0 ? UiText.Pick("Chỉ số máy tính", "PC metrics") : line);
                pc.AccessibleName = (pcExpanded ? UiText.Pick("Thu gọn", "Collapse") : UiText.Pick("Xem chi tiết", "Show details")) + UiText.Pick(" chỉ số máy tính. ", " PC metrics. ") + line;
                pc.Height = Ui.D(s, 35);
                pc.Padding = new Padding(Ui.D(s, 10), 0, 0, 0);
            }
            pin.Text = app.Prefs.Widget && !app.Prefs.TrayOnly ? UiText.Pick("Bỏ ghim", "Unpin") : UiText.Pick("Ghim", "Pin");
            int gap = Ui.D(s, 8), width = Math.Min(area.Width, Ui.D(s, 390));
            view.SetRows(rows, (int)((width - 2) / s));
            Size = new Size(width, Math.Min(Math.Max(80, area.Height - 2 * gap),
                                            Math.Min(Ui.D(s, 600), Ui.D(s, 46) + view.DesiredHeight + (mixed ? pc.Height : 0) + 2)));
            Location = app.PanelLocation(AnchorPosition(screen.Bounds, area, Size, gap, trayAnchor), Size, area, gap);
            int w = (int)(ClientSize.Width / s);
            head.Height = Ui.D(s, 46);
            Ui.Place(close, s, w - 38, 8, 30, 28);
            Ui.Place(settings, s, w - 108, 8, 65, 28);
            Ui.Place(pin, s, w - 180, 8, 67, 28);
            foreach (Button b in new[] { close, settings, pin, pc })
            {
                b.Font = Theme.Font(12 * s, false);
                b.ForeColor = Theme.Text;
                b.BackColor = b == pc ? Theme.Surface : Theme.Base;
                b.FlatAppearance.MouseOverBackColor = Theme.Edge;
            }
            head.Invalidate();
        }
        finally
        {
            laying = false;
        }
    }
}

internal sealed class FloatingWidget : Form
{
    readonly MonitorApp app;
    bool held, moved, placing;
    int scroll, maxScroll;
    Point origin, drag;
    string last;
    List<ViewRow> rows = new List<ViewRow>();
    public FloatingWidget(MonitorApp app)
    {
        this.app = app;
        Text = "QuotaPeek · Widget";
        Name = "QuotaPeek.Widget";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        AccessibleName = "QuotaPeek";
    }
    protected override CreateParams CreateParams
    {
        get {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x08000000;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation
    {
        get {
            return true;
        }
    }
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        last = null;
        Place();
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            app.Menu.Show(Cursor.Position);
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            held = true;
            moved = false;
            drag = Cursor.Position;
            origin = Location;
            Capture = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!held)
            return;
        var pos = Cursor.Position;
        var dx = pos.X - drag.X;
        var dy = pos.Y - drag.Y;
        if (!moved && Math.Abs(dx) + Math.Abs(dy) < 4)
            return;
        moved = true;
        Location = Ui.Clamp(new Point(origin.X + dx, origin.Y + dy), Size, Screen.FromPoint(pos).WorkingArea);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!held)
            return;
        held = false;
        Capture = false;
        if (!moved)
        {
            var row = rows.Count == 1 ? rows[0] : null;
            if (row != null && row.ActionId != null)
                app.OpenTarget(row.ActionId);
            else
                app.OpenDashboard();
            return;
        }
        var s = Screen.FromRectangle(Bounds);
        var a = s.WorkingArea;
        var p = app.Prefs;
        float dpi = Theme.Scale(this);
        int near = Ui.D(dpi, 18);
        p.Display = s.DeviceName;
        p.Corner = "free";
        bool l = Left - a.Left <= near, r = a.Right - Right <= near, t = Top - a.Top <= near,
             b = a.Bottom - Bottom <= near;
        if ((l || r) && (t || b))
            p.Corner = (t ? "t" : "b") + (l ? "l" : "r");
        p.OffsetX = (int)Math.Round((Left - a.X) / dpi);
        p.OffsetY = (int)Math.Round((Top - a.Y) / dpi);
        app.SavePosition();
        Place();
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        scroll = Math.Min(maxScroll, Math.Max(0, scroll - Math.Sign(e.Delta) * 48));
        Place();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (held && !Capture)
        {
            held = false;
            Place();
        }
    }
    public void UpdateValues()
    {
        if (Visible)
            Place();
    }
    public void Place()
    {
        if (held || placing)
            return;
        placing = true;
        try
        {
            var p = app.Prefs;
            var screen =
                Screen.AllScreens.FirstOrDefault(v => v.DeviceName == p.Display) ?? Screen.PrimaryScreen;
            var area = screen.WorkingArea;
            if (!IsHandleCreated || Screen.FromRectangle(Bounds).DeviceName != screen.DeviceName)
                Location = area.Location;
            var handle = Handle;
            float s = Theme.Scale(this);
            rows = app.Rows(true);
            int logicalHeight = WidgetPainter.Height(rows, p.WidgetStyle), margin = Ui.D(s, 8);
            var size = new Size(Math.Min(area.Width, Ui.D(s, WidgetPainter.Width(rows, p.WidgetStyle))),
                                Math.Min(Math.Max(1, area.Height - 2 * margin), Ui.D(s, logicalHeight)));
            maxScroll = Math.Max(0, logicalHeight - (int)(size.Height / s));
            scroll = Math.Min(scroll, maxScroll);
            string corner = p.Corner ?? "br";
            var at = Ui.Clamp(new Point(corner == "free"       ? area.X + Ui.D(s, p.OffsetX)
                                        : corner.EndsWith("l") ? area.Left + margin
                                                               : area.Right - size.Width - margin,
                                        corner == "free"         ? area.Y + Ui.D(s, p.OffsetY)
                                        : corner.StartsWith("t") ? area.Top + margin
                                                                 : area.Bottom - size.Height - margin),
                              size, area);
            Bounds = new Rectangle(at, size);
            if (TopMost != p.AlwaysOnTop)
                TopMost = p.AlwaysOnTop;
            string inks = p.WidgetStyle == "text" && p.WidgetTextColor == "auto" && !SystemInformation.HighContrast
                        ? WidgetPainter.ScreenTextInks(rows, at, size, s, scroll) : null;
            string signature = String.Join("|", rows.Select(r => r.Kind + "/" + r.Label + "/" + r.Value + "/" + r.Percent + "/" + r.Level)) +
                               "|" + p.WidgetStyle + "|" + p.WidgetTextColor + "|" + inks + "|" + p.GlassOpacity + "|" + scroll + "|" + s + "|" +
                               size + "|" + at + "|" + Theme.Text.ToArgb() + "|" +
                               SystemInformation.HighContrast;
            if (signature == last)
                return;
            AccessibleDescription = String.Join(", ", rows.Select(r => r.Label + " " + r.Value));
            using (var bitmap = WidgetPainter.Render(rows, p.WidgetStyle, p.GlassOpacity, s, size,
                                                     scroll, p.WidgetTextColor, inks)) if (LayeredSurface.Present(handle, at, bitmap))
                last = signature;
        }
        finally
        {
            placing = false;
        }
    }
    public void InvalidateStyle()
    {
        last = null;
    }
}
}
