using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NhipPC
{
internal static partial class Checks
{
    // obj\QuotaPeek.Tests.exe --tray-preview <png>: taskbar numbers, enlarged, on both taskbar themes.
    static void SaveTrayPreview(string path)
    {
        var samples = new[] { new { N = "37", P = "codex", K = "ai/codex/codex/10080", L = 0, S = false },
                              new { N = "52", P = "claude", K = "ai/claude/five_hour", L = 0, S = false },
                              new { N = "78", P = "claude", K = "ai/claude/seven_day", L = 0, S = false },
                              new { N = "15", P = "claude", K = "ai/claude/seven_day_fable", L = 1, S = false },
                              new { N = "12", P = "codex", K = "ai/codex/codex/credits", L = 0, S = false },
                              new { N = "62", P = "antigravity", K = "ai/antigravity/gemini-weekly", L = 0, S = true },
                              new { N = "35", P = "ram", K = "ram", L = 0, S = false } };
        const int zoom = 4, cell = 24 * zoom + 16;
        const int row2 = 2;
        using (var sheet = new Bitmap(cell * samples.Length + 16, cell * 3 + 16, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            for (int row = 0; row < 2; row++)
            {
                Theme.TrayDark = row == 1;
                using (var back = new SolidBrush(row == 1 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(214, 234, 190)))
                    g.FillRectangle(back, 0, 8 + row * cell, sheet.Width, cell);
                for (int i = 0; i < samples.Length; i++)
                    using (var icon = TrayIndicators.MakeIcon(samples[i].N, samples[i].S, samples[i].L, TrayIndicators.Accent(samples[i].P), TrayIndicators.Mark(samples[i].K)))
                    using (var bmp = icon.ToBitmap())
                        g.DrawImage(bmp, new Rectangle(16 + i * cell, 16 + row * cell, bmp.Width * zoom, bmp.Height * zoom));
            }
            // Third row: 2-bar style on both taskbar themes.
            var claude = new System.Collections.Generic.List<PinChoice> {
                new PinChoice { Percent = 48, Level = 0 }, new PinChoice { Percent = 78, Level = 0 } };
            var low = new System.Collections.Generic.List<PinChoice> {
                new PinChoice { Percent = 12, Level = 1 }, new PinChoice { Percent = 56, Level = 0 } };
            var codex = new System.Collections.Generic.List<PinChoice> { new PinChoice { Percent = 37, Level = 0 } };
            var sets = new[] { claude, low, codex };
            var owners = new[] { "claude", "claude", "codex" };
            for (int t = 0; t < 2; t++)
            {
                Theme.TrayDark = t == 1;
                for (int i = 0; i < sets.Length; i++)
                    using (var icon = TrayIndicators.MakeBars(sets[i], false, TrayIndicators.Accent(owners[i])))
                    using (var bmp = icon.ToBitmap())
                    {
                        int x = 16 + (t * 3 + i) * cell, y = 16 + row2 * cell;
                        using (var back = new SolidBrush(t == 1 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(214, 234, 190)))
                            g.FillRectangle(back, x - 8, y - 8, cell, cell);
                        g.DrawImage(bmp, new Rectangle(x, y, bmp.Width * zoom, bmp.Height * zoom));
                    }
            }
            sheet.Save(path, ImageFormat.Png);
        }
        Theme.Refresh();
    }
}
}
