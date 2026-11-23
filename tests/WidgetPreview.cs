using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace NhipPC
{
internal static partial class Checks
{
    static void SavePreview(string path)
    {
        var rows = SampleRows();
        const int margin = 30, gap = 15, tileWidth = 362, tileHeight = 174;
        const int width = margin * 2 + tileWidth * 3 + gap * 2, height = 522;
        const float scale = 1.08f;
        Theme.Refresh();
        var ink = Color.FromArgb(33, 40, 45);
        using (var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        using (var background = PreviewBackdrop(tileWidth, tileHeight))
        {
            g.Clear(Color.FromArgb(247, 247, 244));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Theme.Words(g, "Widget QuotaPeek", margin - 2, 11, 590, 46, 26, ink, true, false);
            Theme.Words(g, "Số liệu minh họa", width - margin - 270, 12, 270, 46, 14,
                        Color.FromArgb(87, 92, 94), false, true);
            for (int i = 0; i < WidgetPainter.Styles.Length; i++)
            {
                string style = WidgetPainter.Styles[i];
                var widgetSize = new Size(Ui.D(scale, WidgetPainter.Width(rows, style)), Ui.D(scale, WidgetPainter.Height(rows, style)));
                int x = margin + i % 3 * (tileWidth + gap), y = 66 + i / 3 * 224;
                g.DrawImageUnscaled(background, x, y);
                int dx = (tileWidth - widgetSize.Width) / 2, dy = (tileHeight - widgetSize.Height) / 2;
                string inks = style == "text" ? WidgetPainter.TextInks(rows, scale, 0, widgetSize.Height,
                    (right, py) => background.GetPixel(dx + (right ? widgetSize.Width - 1 : 0), dy + py)) : null;
                using (var rendered =
                           WidgetPainter.Render(rows, WidgetPainter.Styles[i], 72, scale, widgetSize, 0, "auto", inks))
                    g.DrawImageUnscaled(rendered, x + (tileWidth - widgetSize.Width) / 2,
                                        y + (tileHeight - widgetSize.Height) / 2);
                Theme.Words(g, WidgetPainter.Names[i], x - 1, y + tileHeight + 3, tileWidth - 1, 34, 18, ink, true, false);
                if (i == 1)
                    Theme.Words(g, "Nền 72%", x + tileWidth - 95, y + tileHeight + 3, 95, 34, 13, Color.FromArgb(87, 92, 94),
                                false, true);
            }
            int trayX = margin + 2 * (tileWidth + gap), trayY = 290;
            g.DrawImageUnscaled(background, trayX, trayY);
            var periods = new System.Collections.Generic.List<PinChoice> {
                new PinChoice { Number = "34", Percent = 34 }, new PinChoice { Number = "52", Percent = 52 } };
            using (var bar = new SolidBrush(Theme.TrayDark ? Color.FromArgb(39, 44, 45) : Color.FromArgb(234, 240, 230)))
                g.FillRectangle(bar, trayX, trayY + 56, tileWidth, 58);
            for (int i = 0; i < 2; i++)
            {
                using (var icon = TrayIndicators.MakeRing(periods, TrayIndicators.Accent(i == 0 ? "claude" : "codex")))
                using (var bmp = icon.ToBitmap())
                    g.DrawImage(bmp, trayX + 133 + i * 34, trayY + 73, 23, 23);
                periods[0].Number = "38"; periods[0].Percent = 38; periods[1].Percent = 75;
            }
            Theme.Words(g, "14:02", trayX + 219, trayY + 56, 82, 58, 16,
                        Theme.TrayDark ? Color.White : ink, false, false);
            Theme.Words(g, "Cạnh đồng hồ · Vòng hạn mức", trayX - 1, trayY + tileHeight + 3,
                        tileWidth, 34, 18, ink, true, false);
            sheet.Save(Path.GetFullPath(path), ImageFormat.Png);
        }
    }
    static Bitmap PreviewBackdrop(int width, int height)
    {
        var art = new Bitmap(width, height);
        using (var g = Graphics.FromImage(art))
        using (var sky = new LinearGradientBrush(new Point(0, 0), new Point(0, height),
                   Color.FromArgb(223, 224, 213), Color.FromArgb(186, 202, 203)))
        using (var upper = new SolidBrush(Color.FromArgb(144, 166, 164)))
        using (var lower = new SolidBrush(Color.FromArgb(95, 127, 133)))
        using (var near = new SolidBrush(Color.FromArgb(60, 90, 98)))
        using (var ridge = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(sky, 0, 0, width, height);
            ridge.AddBezier(-2, 92, 86, 66, 164, 78, width + 2, 35);
            ridge.AddLine(width + 2, height + 1, -2, height + 1);
            ridge.CloseFigure();
            g.FillPath(upper, ridge);
            ridge.Reset();
            ridge.AddBezier(-2, 118, 84, 110, 206, 53, width + 2, 93);
            ridge.AddLine(width + 2, height + 1, -2, height + 1);
            ridge.CloseFigure();
            g.FillPath(lower, ridge);
            ridge.Reset();
            ridge.AddBezier(-2, 119, 105, 115, 188, 163, width + 2, 129);
            ridge.AddLine(width + 2, height + 1, -2, height + 1);
            ridge.CloseFigure();
            g.FillPath(near, ridge);
        }
        return art;
    }
}
}
