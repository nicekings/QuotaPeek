using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace NhipPC
{
internal sealed class TrayIndicators : NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct IconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr Balloon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    static extern bool Notify(uint command, ref IconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr handle);
    const int Callback = 0x8000 + 45;
    readonly uint restored = RegisterWindowMessage("TaskbarCreated");
    readonly Action<Point> click;
    readonly ContextMenuStrip menu;
    sealed class Entry
    {
        public Guid Guid;
        public uint Id;
        public Icon Icon;
        public string Face, Tip;
        public bool Added, Version4;
        public long Retry;
    }
    readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
    uint sequence;
    public int RegisteredCount
    {
        get {
            return entries.Values.Count(e => e.Added);
        }
    }
    public TrayIndicators(Action<Point> click, ContextMenuStrip menu)
    {
        this.click = click;
        this.menu = menu;
        CreateHandle(new CreateParams { Caption = "QuotaPeek.Notification", ExStyle = 0x80,
                                        Style = unchecked((int)0x80000000) });
    }
    internal static Guid Identity(string key)
    {
        using (var sha = SHA256.Create())
        {
            var hash =
                sha.ComputeHash(Encoding.UTF8.GetBytes("QuotaPeek|" + Tools.Self.ToUpperInvariant() + "|" +
                                                       Store.Root.ToUpperInvariant() + "|" + key));
            var id = new byte[16];
            Array.Copy(hash, id, 16);
            return new Guid(id);
        }
    }
    IconData Data(Entry e)
    {
        return new IconData { Size = (uint)Marshal.SizeOf(typeof(IconData)), Window = Handle, Id = e.Id,
                              Guid = e.Guid, Flags = 0x20 };
    }
    public void Update(List<PinChoice> items)
    {
        if (items.Count == 0)
            items = new List<PinChoice> { new PinChoice { Key = "launcher", Tip = UiText.Pick("Mở bảng số liệu", "Open dashboard") } };
        foreach (var c in items)
        {
            Entry e;
            if (!entries.TryGetValue(c.Key, out e))
                entries[c.Key] = e = new Entry { Guid = Identity(c.Key), Id = ++sequence };
            var tip = "QuotaPeek · " + c.Tip;
            if (tip.Length > 127)
                tip = tip.Substring(0, 127);
            string bars = c.Bars == null ? "" : String.Join(",", c.Bars.Select(b => b.Key + ":" + b.Percent + ":" + b.Level + ":" + b.Stale));
            string face = c.Ring + "|" + c.RingSize + "|" + c.Number + "|" + bars + "|" + c.Stale + "|" + c.Level + "|" + c.Parent + "|" + Theme.TrayDark + "|" +
                          SystemInformation.HighContrast + "|" + SystemInformation.SmallIconSize.Height;
            bool redraw = face != e.Face;
            if (e.Added && !redraw && e.Tip == tip)
                continue;
            if (!e.Added && e.Retry > Fmt.Now)
                continue;
            using (var replacement = !redraw ? null
                                     : c.Ring ? MakeRing(c.Bars, Accent(c.Parent), c.RingSize)
                                     : c.Bars != null ? MakeBars(c.Bars, c.Stale, Accent(c.Parent))
                                                      : MakeIcon(c.Number, c.Stale, c.Level, Accent(c.Parent), Mark(c.Key)))
            {
                var data = Data(e);
                data.Flags |= 1 | 4 | 0x80;
                data.Callback = Callback;
                data.Tip = tip;
                if (redraw || !e.Added)
                {
                    data.Flags |= 2;
                    data.Icon = (replacement ?? e.Icon).Handle;
                }
                if (!Notify(e.Added ? 1u : 0u, ref data))
                {
                    // A failed modify can race Explorer's restart. Remove the stale registration before
                    // retrying.
                    var stale = Data(e);
                    Notify(2, ref stale);
                    e.Added = false;
                    e.Retry = Fmt.Now + 3;
                    continue;
                }
                if (!e.Added)
                {
                    var version = Data(e);
                    version.Version = 4;
                    e.Version4 = Notify(4, ref version);
                }
                if (redraw)
                {
                    if (e.Icon != null)
                        e.Icon.Dispose();
                    e.Icon = (Icon)replacement.Clone();
                }
                e.Added = true;
                e.Tip = tip;
                e.Face = face;
            }
        }
        var keys = new HashSet<string>(items.Select(c => c.Key));
        foreach (var key in entries.Keys.Where(k => !keys.Contains(k)).ToArray())
            Remove(key);
    }
    // Drop and re-add every icon so Explorer applies a changed "show on taskbar" setting.
    public void Reregister()
    {
        foreach (var key in entries.Keys.ToArray())
            Remove(key);
    }
    // Windows keeps one entry per notification icon under NotifyIconSettings. IsPromoted = 1 is what
    // Settings > Taskbar > Other system tray icons writes when the user turns an icon on.
    public static int Promote()
    {
        int count = 0;
        try
        {
            using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings"))
            {
                if (root == null)
                    return 0;
                foreach (string name in root.GetSubKeyNames())
                    using (var key = root.OpenSubKey(name, true))
                    {
                        if (key == null || !String.Equals(key.GetValue("ExecutablePath") as string, Tools.Self,
                                                          StringComparison.OrdinalIgnoreCase))
                            continue;
                        key.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        count++;
                    }
            }
        }
        catch (System.Security.SecurityException) {}
        catch (UnauthorizedAccessException) {}
        return count;
    }
    // CodexBar-style meter: a thick bar for the short period (5 hours) above a thin bar for the week.
    // Bars fill with the share that is left, in the service colour, or amber/red when it runs low.
    internal static Icon MakeBars(List<PinChoice> bars, bool stale, Color? accent)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Height);
        using (var bmp = new Bitmap(size, size)) using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.None;
            var ink = Theme.TrayDark ? Color.White : Color.FromArgb(23, 23, 23);
            bool contrast = SystemInformation.HighContrast;
            float left = 1, width = size - 2;
            float thick = Math.Max(4, (float)Math.Round(size * 0.3)), thin = Math.Max(3, (float)Math.Round(size * 0.2));
            float gap = Math.Max(2, (float)Math.Round(size * 0.12));
            var heights = bars.Count == 1
                ? new[] { DisplayData.Duration(bars[0].Key) >= 604800 ? thin : thick }
                : new[] { thick, thin };
            float total = heights.Sum() + gap * (heights.Length - 1), y = (size - total) / 2;
            for (int i = 0; i < heights.Length; i++)
            {
                var bar = bars[i];
                using (var track = new SolidBrush(Color.FromArgb(70, ink)))
                    g.FillRectangle(track, left, y, width, heights[i]);
                double left100 = bar.Percent ?? 0;
                var fill = contrast ? ink
                         : bar.Level >= 2 ? (Theme.TrayDark ? Color.FromArgb(255, 120, 110) : Color.FromArgb(190, 30, 30))
                         : bar.Level == 1 ? (Theme.TrayDark ? Color.FromArgb(255, 200, 90) : Color.FromArgb(160, 95, 0))
                         : accent ?? ink;
                float w = (float)Math.Round(width * Math.Max(0, Math.Min(100, left100)) / 100);
                if (bar.Percent.HasValue && w < 1 && left100 > 0)
                    w = 1;
                using (var brush = new SolidBrush(Color.FromArgb(stale ? 120 : 255, fill)))
                    g.FillRectangle(brush, left, y, w, heights[i]);
                y += heights[i] + gap;
            }
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var wrapped = Icon.FromHandle(h)) return (Icon)wrapped.Clone();
            }
            finally
            {
                Theme.DestroyIcon(h);
            }
        }
    }
    internal static Icon MakeRing(List<PinChoice> periods, Color? service, RingSize ringSize = RingSize.Large)
    {
        return MakeRingAtSize(periods, service, ringSize, Math.Max(16, SystemInformation.SmallIconSize.Height));
    }
    internal static Icon MakeRingAtSize(List<PinChoice> periods, Color? service, RingSize ringSize, int size)
    {
        var center = periods[0];
        var outer = periods[periods.Count - 1];
        bool contrast = SystemInformation.HighContrast;
        Color ink = contrast ? SystemColors.WindowText : Theme.TrayDark ? Color.White : Color.FromArgb(22, 30, 34);
        Color accent = contrast ? ink : service ?? ink;
        using (var bmp = new Bitmap(size, size))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 16f, size / 16f);
            // Outer diameter in a 16-unit Explorer slot, including the stroke.
            g.PixelOffsetMode = PixelOffsetMode.Half;
            bool medium = ringSize == RingSize.Medium;
            float diameter = medium ? 14.5f : 16f;
            float stroke = medium ? 1.25f : 1.95f;
            float inset = (16 - diameter + stroke) / 2;
            var circle = new RectangleF(inset, inset, diameter - stroke, diameter - stroke);
            using (var track = new Pen(Color.FromArgb(contrast ? 140 : 65, ink), stroke * .86f))
            using (var fill = new Pen(Color.FromArgb(outer.Stale ? 140 : 255, accent), stroke))
            {
                if (!outer.Percent.HasValue) track.DashStyle = DashStyle.Dot;
                g.DrawEllipse(track, circle);
                float arc = (float)(outer.Percent.GetValueOrDefault() * 3.6);
                if (arc >= 360) g.DrawEllipse(fill, circle);
                else if (arc > 0) g.DrawArc(fill, circle, -90, arc);
            }
            Color number = ink;
            if (!contrast && center.Level > 0)
                number = center.Level >= 2 ? (Theme.TrayDark ? Color.Salmon : Color.Firebrick)
                                           : (Theme.TrayDark ? Color.Khaki : Color.SaddleBrown);
            using (var brush = new SolidBrush(Color.FromArgb(center.Stale ? 165 : 255, number)))
            {
                string text = center.Number ?? "—";
                if (medium && size == 16 && text.Length >= 2 && text.Length <= 3 && text.All(c => c >= '0' && c <= '9'))
                    DrawSmallRingDigits(g, brush, text);
                else
                    DrawRingText(g, brush, text, diameter / 2 - stroke - 1f);
            }
            IntPtr h = bmp.GetHicon();
            try { using (var icon = Icon.FromHandle(h)) return (Icon)icon.Clone(); }
            finally { Theme.DestroyIcon(h); }
        }
    }
    // At 16 px a scaled font can blur both counters of an 8 into the surrounding strokes.
    // Each row encodes three pixels, with the most significant bit on the left.
    static readonly string[] SmallDigits = { "75557", "26227", "71747", "71717", "55711",
                                             "74717", "74757", "71111", "75757", "75717" };
    static void DrawSmallRingDigits(Graphics g, Brush brush, string text)
    {
        bool compact = text.Length == 3;
        int width = text.Length * 4 - 1;
        if (compact && text[0] == '1') width -= 2;
        int x = (16 - width) / 2;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        for (int i = 0; i < text.Length; i++)
        {
            bool narrowOne = compact && i == 0 && text[i] == '1';
            int y = compact ? 5 : 4;
            string rows = SmallDigits[text[i] - '0'];
            for (int row = 0; row < rows.Length; row++)
            {
                int height = !compact && (row == 1 || row == 3) ? 2 : 1;
                int bits = narrowOne ? 4 : rows[row] - '0';
                for (int col = 0; col < 3; col++)
                    if ((bits & (4 >> col)) != 0) g.FillRectangle(brush, x + col, y, 1, height);
                y += height;
            }
            x += narrowOne ? 2 : 4;
        }
    }
    static void DrawRingText(Graphics g, Brush brush, string text, float safeRadius)
    {
        using (var font = SystemFonts.MessageBoxFont)
        using (var path = new GraphicsPath())
        {
            path.AddString(text, font.FontFamily, (int)FontStyle.Bold, 24, PointF.Empty,
                           StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                float cx = bounds.Left + bounds.Width / 2, cy = bounds.Top + bounds.Height / 2;
                // Condense wide readings slightly so three digits keep their height.
                float widthScale = bounds.Width > 1.6f * bounds.Height ? .8f : 1f;
                float radiusSquared = 0;
                foreach (var point in path.PathPoints)
                {
                    float dx = (point.X - cx) * widthScale, dy = point.Y - cy;
                    radiusSquared = Math.Max(radiusSquared, dx * dx + dy * dy);
                }
                // Keeping even the Bezier control points inside this disk clears the arc at every angle.
                float zoom = safeRadius / (float)Math.Sqrt(radiusSquared);
                g.TranslateTransform(8, 8);
                g.ScaleTransform(zoom * widthScale, zoom);
                g.TranslateTransform(-cx, -cy);
                g.FillPath(brush, path);
            }
        }
    }
    void Remove(string key)
    {
        var e = entries[key];
        var data = Data(e);
        Notify(2, ref data);
        if (e.Icon != null)
            e.Icon.Dispose();
        entries.Remove(key);
    }
    // A brand-coloured stripe under the number says which service it belongs to. The number's own
    // colour stays free for the warning level, so the two signals never compete.
    internal static Color? Accent(string provider)
    {
        switch (provider)
        {
        case "codex": return Color.FromArgb(16, 163, 127);       // OpenAI green
        case "claude": return Color.FromArgb(217, 119, 87);      // Claude orange
        case "antigravity": return Color.FromArgb(66, 133, 244); // Google blue
        case "gemini": return Color.FromArgb(145, 104, 192);     // Gemini purple
        case "grok": return Color.FromArgb(150, 155, 165);       // neutral grey
        default: return null;
        }
    }
    // The stripe's shape says which period a number covers; its colour says which service.
    internal enum Stripe { Full, Short, Dashed, Dot }
    internal static Stripe Mark(string key)
    {
        string k = (key ?? "").ToLowerInvariant();
        int slash = k.LastIndexOf('/');
        string tail = slash >= 0 ? k.Substring(slash + 1) : k;
        if (tail == "credits" || tail.StartsWith("spend"))
            return Stripe.Dot;
        if (tail == "five_hour" || tail == "300" || tail.EndsWith("-5h") || tail.Contains("five") || tail.Contains("session"))
            return Stripe.Short;
        if (tail.StartsWith("seven_day_") || (k.StartsWith("ai/codex/") && !k.StartsWith("ai/codex/codex/")))
            return Stripe.Dashed;   // a weekly limit for one model or one Codex bucket
        return Stripe.Full;         // the main weekly limit
    }
    internal static Icon MakeIcon(string number, bool stale, int level, Color? accent, Stripe mark = Stripe.Full)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Height);
        if (number == null)
            return Brand.Icon(size);
        using (var bmp = new Bitmap(size, size)) using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var ink = Theme.TrayDark ? Color.White : Color.FromArgb(23, 23, 23);
            // Amber at 20% or less left, red at 5% or less. High contrast keeps the system ink.
            if (level > 0 && !SystemInformation.HighContrast)
                ink = level >= 2 ? (Theme.TrayDark ? Color.FromArgb(255, 120, 110) : Color.FromArgb(190, 30, 30))
                                 : (Theme.TrayDark ? Color.FromArgb(255, 200, 90) : Color.FromArgb(160, 95, 0));
            using (var sys = SystemFonts.MessageBoxFont) using (
                var fmt = (StringFormat)StringFormat.GenericTypographic
                              .Clone()) using (var path = new GraphicsPath()) using (var brush =
                                                                                         new SolidBrush(ink))
            {
                path.AddString(number, sys.FontFamily, (int)(sys.Style | FontStyle.Bold), 32, PointF.Empty,
                               fmt);
                var b = path.GetBounds();
                if (b.Width > 0 && b.Height > 0)
                {
                    // Leave room at the bottom for the service stripe.
                    int stripe = accent.HasValue && !SystemInformation.HighContrast ? Math.Max(2, size / 8) : 0;
                    float room = size - stripe - (stripe > 0 ? 1 : 0);
                    float scale = Math.Min((size - 1) / b.Width, (room - 1) / b.Height);
                    g.TranslateTransform((size - b.Width * scale) / 2, (room - b.Height * scale) / 2);
                    g.ScaleTransform(scale, scale);
                    g.TranslateTransform(-b.Left, -b.Top);
                    g.FillPath(brush, path);
                    g.ResetTransform();
                }
                if (accent.HasValue && !SystemInformation.HighContrast)
                {
                    int stripe = Math.Max(2, size / 8);
                    // An unconfirmed reading ("~") shows a faded stripe.
                    using (var bar = new SolidBrush(Color.FromArgb(stale ? 110 : 255, accent.Value)))
                    {
                        float y0 = size - stripe;
                        switch (mark)
                        {
                        case Stripe.Short:
                            g.FillRectangle(bar, size * 0.3f, y0, size * 0.4f, stripe);
                            break;
                        case Stripe.Dashed:
                            float dash = (size - 2) / 5f;
                            for (int i = 0; i < 5; i += 2)
                                g.FillRectangle(bar, 1 + i * dash, y0, dash, stripe);
                            break;
                        case Stripe.Dot:
                            g.FillEllipse(bar, size / 2f - stripe, y0 - stripe / 2f, stripe * 2, stripe * 2);
                            break;
                        default:
                            g.FillRectangle(bar, 1, y0, size - 2, stripe);
                            break;
                        }
                    }
                }
                else if (stale)
                    g.FillEllipse(brush, size / 2f - 1, size - 2, 2, 2);
            }
            IntPtr h = bmp.GetHicon();
            try
            {
                using (var wrapped = Icon.FromHandle(h)) return (Icon)wrapped.Clone();
            }
            finally
            {
                Theme.DestroyIcon(h);
            }
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == restored)
            foreach (var e in entries.Values)
            {
                e.Added = false;
                e.Retry = 0;
            }
        else if (m.Msg == Callback)
        {
            long code = m.LParam.ToInt64();
            int msg = (int)(code & 65535);
            // Version 4 sends a selection or context-menu notification in addition to raw mouse events.
            uint legacyId = unchecked((uint)m.WParam.ToInt64());
            bool legacy = (code >> 16) == 0 && (msg == 0x202 || msg == 0x205) &&
                          entries.Values.Any(e => e.Added && !e.Version4 && e.Id == legacyId);
            long packed = m.WParam.ToInt64();
            var anchor = new Point((short)packed, (short)(packed >> 16));
            Point position = legacy || anchor == new Point(-1, -1) ? Cursor.Position : anchor;
            if (msg == 0x400 || msg == 0x401 || (legacy && msg == 0x202))
                click(position);
            else if ((msg == 0x7b || (legacy && msg == 0x205)) && !menu.Visible)
            {
                SetForegroundWindow(Handle);
                menu.Show(position);
            }
        }
        base.WndProc(ref m);
    }
    public void Dispose()
    {
        foreach (var key in entries.Keys.ToArray())
            Remove(key);
        DestroyHandle();
    }
}
}
