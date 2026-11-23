using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NhipPC
{
internal static class WidgetPainter
{
    public static readonly string[] Styles = { "system", "glass", "text", "meters", "tiles" };
    public static string[] Names
    {
        get { return new[] { UiText.Pick("Theo Windows", "Windows theme"), UiText.Pick("Kính khói", "Smoked glass"), UiText.Pick("Chỉ chữ", "Text only"), UiText.Pick("Thanh hạn mức", "Quota bars"), UiText.Pick("Ô chỉ số", "Metric tiles") }; }
    }
    static string EffectiveStyle(string style)
    {
        return SystemInformation.HighContrast ? "system" : style;
    }
    static bool Break(List<ViewRow> rows, int i)
    {
        return i > 0 && rows[i - 1].Kind != rows[i].Kind;
    }
    // Logical coordinates shared by measurement, the layered window and its preview.
    static RectangleF[] Layout(List<ViewRow> rows, string style, float width)
    {
        var boxes = new RectangleF[rows.Count];
        float y = 8;
        bool secondColumn = false;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (style == "tiles")
            {
                // Keep compound network rates, balances and longer model names on a full-width tile.
                bool wide = rows.Count == 1 || !row.Percent.HasValue || row.Label.Length > 20;
                if (secondColumn && (wide || Break(rows, i))) { y += 62; secondColumn = false; }
                float half = (width - 22) / 2;
                boxes[i] = new RectangleF(secondColumn ? 14 + half : 8, y, wide ? width - 16 : half, 56);
                if (wide || secondColumn) y += 62;
                secondColumn = !wide && !secondColumn;
            }
            else
            {
                if (Break(rows, i)) y += 7;
                float h = style == "meters" && row.Percent.HasValue ? 36 : style == "text" ? 31 : 24;
                boxes[i] = new RectangleF(11, y, width - 22, h);
                y += h;
            }
        }
        return boxes;
    }
    public static int Height(List<ViewRow> rows, string style = "system")
    {
        var boxes = Layout(rows, EffectiveStyle(style), 252);
        return boxes.Length == 0 ? 16 : (int)boxes[boxes.Length - 1].Bottom + 8;
    }
    public static int Width(List<ViewRow> rows, string style = "system")
    {
        style = EffectiveStyle(style);
        if (style == "tiles") return rows.Count == 1 && rows[0].Percent.HasValue ? 174 : 312;
        if (style == "text" || style == "meters") return rows.Count == 1 ? Math.Max(174, Width(rows)) : (style == "text" ? 248 : 276);
        // A single percentage fits into a small corner. Reserve room for 100% so the widget stays still as
        // the number changes.
        if (rows.Count != 1)
            return 252;
        var row = rows[0];
        const TextFormatFlags flags =
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int label = TextRenderer.MeasureText(row.Label, Theme.Font(13, false), Size.Empty, flags).Width;
        string sample = row.Value;
        if (row.Percent.HasValue)
        {
            // Keep the direction label, e.g. "Còn lại", and reserve space for a stale decimal percentage.
            string value = sample.TrimStart('~', ' ');
            sample = "~ " + value.Substring(0, value.LastIndexOf(' ') + 1) + "99,9%";
        }
        if (sample.Contains("/s"))
            sample = sample.Contains(" / ") || sample.Contains("↑") ? "↓ 999 KB/s   ↑ 999 KB/s" : "999 KB/s";
        int valueWidth = TextRenderer.MeasureText(sample, Theme.Font(13, true), Size.Empty, flags).Width;
        return Math.Min(252, Math.Max(120, label + valueWidth + 38));
    }
    static GraphicsPath Border(float w, float h)
    {
        const float d = 15;
        var p = new GraphicsPath();
        p.AddArc(.5f, .5f, d, d, 180, 90);
        p.AddArc(w - d - .5f, .5f, d, d, 270, 90);
        p.AddArc(w - d - .5f, h - d - .5f, d, d, 0, 90);
        p.AddArc(.5f, h - d - .5f, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
    static void ShadowText(Graphics g, string text, RectangleF box, float size, Color ink, bool bold, bool right)
    {
        // A feathered shadow behind the glyphs keeps the counters and Vietnamese accents open.
        foreach (Point offset in shadowOffsets)
            Theme.Words(g, text, box.X + offset.X, box.Y + offset.Y, box.Width, box.Height, size,
                        Color.FromArgb(offset.Y == 1 ? 120 : 50, 16, 24, 31), bold, right);
        Theme.Words(g, text, box.X, box.Y, box.Width, box.Height, size, ink, bold, right);
    }
    static readonly Point[] shadowOffsets = { new Point(-1, 2), new Point(1, 2),
        new Point(-1, 1), new Point(1, 1), new Point(0, 2) };
    static Color ValueInk(ViewRow row, Color normal, bool onDark)
    {
        if (row.Level == 0) return normal;
        if (!onDark) return Theme.ForLevel(row.Level, normal);
        return row.Level >= 2 ? Color.FromArgb(255, 158, 145) : Color.FromArgb(255, 213, 133);
    }
    static void PaintTile(Graphics g, ViewRow row, RectangleF box)
    {
        Color ink = Color.FromArgb(39, 48, 43), subtle = Color.FromArgb(78, 93, 85);
        using (var path = Border(box.Width, box.Height))
        using (var mat = new Matrix())
        using (var paper = new SolidBrush(Color.FromArgb(251, 249, 241)))
        {
            mat.Translate(box.X, box.Y);
            path.Transform(mat);
            g.FillPath(paper, path);
        }
        float x = box.X + 10, width = box.Width - 20;
        Theme.Words(g, row.Label, x, box.Y + 4, width, 19, 12, subtle, false, false);
        string value = row.Value.Replace(UiText.Pick("Còn lại ", "Remaining "), UiText.Pick("Còn ", "Left "));
        Theme.Words(g, value, x, box.Y + 21, width, 30, row.Percent.HasValue ? 20 : 16,
                    row.Level >= 2 ? Color.Maroon : row.Level == 1 ? Color.SaddleBrown : ink, true, false);
    }
    static bool NeedsDarkInk(Color c)
    {
        // Compare perceived background luminance with the crossover for black and white text.
        return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B) > .18;
    }
    static double Linear(byte value)
    {
        double v = value / 255.0;
        return v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4);
    }
    // One character per row: bit 0 controls the label, bit 1 controls the value.
    internal static string TextInks(List<ViewRow> rows, float scale, int scroll, int height,
                                    Func<bool, int, Color> sample)
    {
        var boxes = Layout(rows, "text", 252);
        var result = new char[rows.Count];
        for (int i = 0; i < boxes.Length; i++)
        {
            var box = boxes[i];
            int y = Math.Max(0, Math.Min(height - 1, Ui.D(scale, box.Y + box.Height / 2 - scroll)));
            result[i] = (char)('0' + (NeedsDarkInk(sample(false, y)) ? 1 : 0) + (NeedsDarkInk(sample(true, y)) ? 2 : 0));
        }
        return new string(result);
    }
    public static string ScreenTextInks(List<ViewRow> rows, Point at, Size size, float scale, int scroll)
    {
        try
        {
            // Read just the two transparent margins. The widget's own glyphs never influence the next frame.
            using (var strips = new Bitmap(2, size.Height))
            {
                using (var g = Graphics.FromImage(strips))
                {
                    var column = new Size(1, size.Height);
                    int inset = Math.Min(size.Width / 2, Ui.D(scale, 4));
                    g.CopyFromScreen(new Point(at.X + inset, at.Y), Point.Empty, column);
                    g.CopyFromScreen(new Point(at.X + size.Width - 1 - inset, at.Y), new Point(1, 0), column);
                }
                return TextInks(rows, scale, scroll, size.Height, (right, y) => strips.GetPixel(right ? 1 : 0, y));
            }
        }
        catch (System.ComponentModel.Win32Exception) { return Theme.Dark ? "0" : "3"; }
    }
    public static Bitmap Render(List<ViewRow> rows, string style, int opacity, float scale, Size size,
                                int scroll, string textColor = "auto", string inks = null)
    {
        style = EffectiveStyle(style);
        bool glass = style == "glass", bare = style == "text", meters = style == "meters";
        bool darkInkSurface = glass || bare || meters;
        var image = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(image))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.ScaleTransform(scale, scale);
            float w = image.Width / scale, h = image.Height / scale;
            Color ink = darkInkSurface ? Color.White : Theme.Text;
            Color labels = darkInkSurface ? Color.FromArgb(230, 237, 238) : Theme.Soft;
            Color rule = darkInkSurface ? Color.FromArgb(68, 213, 229, 227) : Theme.Edge;
            if (!bare && style != "tiles")
            {
                var background = glass ? Color.FromArgb((int)Math.Round(2.55 * Math.Max(15, Math.Min(95, opacity))), 29, 32, 36)
                               : meters ? Color.FromArgb(31, 47, 49) : Theme.Base;
                using (var path = Border(w, h))
                using (var fill = new SolidBrush(background))
                using (var edge = new Pen(rule))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                }
            }
            g.SetClip(new RectangleF(2, 3, Math.Max(1, w - 4), Math.Max(1, h - 6)));
            var boxes = Layout(rows, style, w);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var box = boxes[i];
                box.Offset(0, -scroll);
                if (box.Bottom < 0 || box.Y > h) continue;
                if (style == "tiles")
                {
                    PaintTile(g, row, box);
                    continue;
                }
                if (!bare && Break(rows, i)) Theme.Line(g, rule, box.Left, box.Y - 4, box.Right, box.Y - 4);
                string value = bare || meters ? row.Value.Replace(UiText.Pick("Còn lại ", "Remaining "), UiText.Pick("Còn ", "Left ")) : row.Value;
                float fontSize = bare ? 18 : 13;
                float labelRoom = Math.Min(box.Width * .5f, g.MeasureString(row.Label, Theme.Font(13, false)).Width);
                float number = Math.Max(1, Math.Min(box.Width - labelRoom - 5,
                                                     g.MeasureString(value, Theme.Font(fontSize, true)).Width + 3));
                float lineHeight = bare ? box.Height : 24;
                var left = new RectangleF(box.X, box.Y, box.Width - number - 5, lineHeight);
                var right = new RectangleF(box.Right - number, box.Y, number, lineHeight);
                var valueInk = ValueInk(row, ink, darkInkSurface);
                if (bare)
                {
                    int flags = textColor == "dark" ? 3 : textColor == "light" || String.IsNullOrEmpty(inks)
                                ? 0 : inks[Math.Min(i, inks.Length - 1)] - '0';
                    if ((flags & 1) != 0)
                        Theme.Words(g, row.Label, left.X, left.Y, left.Width, left.Height, 13, Color.FromArgb(22, 29, 33), true, false);
                    else
                        ShadowText(g, row.Label, left, 13, Color.White, true, false);
                    if ((flags & 2) != 0)
                    {
                        var darkInk = row.Level >= 2 ? Color.Maroon : row.Level == 1 ? Color.FromArgb(104, 55, 0) : Color.FromArgb(15, 22, 26);
                        Theme.Words(g, value, right.X, right.Y, right.Width, right.Height, fontSize, darkInk, true, true);
                    }
                    else ShadowText(g, value, right, fontSize, valueInk, true, true);
                }
                else if (glass && opacity < 65)
                {
                    ShadowText(g, row.Label, left, 13, labels, false, false);
                    ShadowText(g, value, right, fontSize, valueInk, true, true);
                }
                else
                {
                    Theme.Words(g, row.Label, left.X, left.Y, left.Width, left.Height, 13, labels, false, false);
                    Theme.Words(g, value, right.X, right.Y, right.Width, right.Height, fontSize, valueInk, true, true);
                }
                if (meters && row.Percent.HasValue)
                {
                    Color accent = TrayIndicators.Accent(row.ProviderId) ?? Color.FromArgb(129, 211, 187);
                    using (var track = new SolidBrush(Color.FromArgb(73, 90, 91)))
                    using (var fill = new SolidBrush(ValueInk(row, accent, true)))
                    {
                        g.FillRectangle(track, box.X, box.Y + 25, box.Width, 3);
                        g.FillRectangle(fill, box.X, box.Y + 25, (float)(box.Width * row.Percent.Value / 100), 3);
                    }
                }
            }
        }
        return image;
    }
}

internal static class LayeredSurface
{
    [StructLayout(LayoutKind.Sequential)]
    struct Header
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Depth;
        public uint Compression, Bytes;
        public int X, Y;
        public uint Used, Important;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct Blend
    {
        public byte Op, Flags, Alpha, Format;
    }
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref Header info, uint usage, out IntPtr bits,
                                          IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr dc);
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    static extern void Copy(IntPtr target, IntPtr src, UIntPtr count);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UpdateLayeredWindow(IntPtr wnd, IntPtr target, ref Point pos, ref Size size, IntPtr dc,
                                           ref Point zero, uint key, ref Blend blend, uint flags);
    public static bool Present(IntPtr handle, Point location, Bitmap image)
    {
        var info = new Header { Size = (uint)Marshal.SizeOf(typeof(Header)), Width = image.Width,
                                Height = -image.Height, Planes = 1, Depth = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bits, old = IntPtr.Zero;
        IntPtr dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
        try
        {
            if (dc == IntPtr.Zero || dib == IntPtr.Zero)
                return false;
            old = SelectObject(dc, dib);
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly,
                                      PixelFormat.Format32bppPArgb);
            try
            {
                int stride = image.Width * 4;
                if (data.Stride == stride)
                    Copy(bits, data.Scan0, (UIntPtr)(uint)(stride * image.Height));
                else
                    for (int y = 0; y < image.Height; y++)
                        Copy(IntPtr.Add(bits, stride * y), IntPtr.Add(data.Scan0, data.Stride * y),
                             (UIntPtr)(uint)stride);
            }
            finally
            {
                image.UnlockBits(data);
            }
            var blend = new Blend { Alpha = 255, Format = 1 };
            var size = image.Size;
            var zero = Point.Empty;
            return UpdateLayeredWindow(handle, IntPtr.Zero, ref location, ref size, dc, ref zero, 0,
                                       ref blend, 2);
        }
        finally
        {
            if (old != IntPtr.Zero)
                SelectObject(dc, old);
            if (dib != IntPtr.Zero)
                DeleteObject(dib);
            if (dc != IntPtr.Zero)
                DeleteDC(dc);
        }
    }
}
}
