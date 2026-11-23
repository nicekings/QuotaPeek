using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NhipPC
{
internal sealed partial class SettingsWindow
{
    string previewSignature;
    ComboBox textColor;
    void BuildAppearance()
    {
        string languageLabel = UiText.Pick("Ngôn ngữ", "Language");
        var languageCard = Card(2, languageLabel);
        Caption(languageCard, languageLabel, 0, 0, 2, true);
        var language = Picker(languageCard, languageLabel, 1);
        language.Items.AddRange(new string[] { "Tiếng Việt", "English" });
        language.SelectedIndex = P.Language == "en" ? 1 : 0;
        language.SelectionChangeCommitted += delegate
        {
            string selected = language.SelectedIndex == 1 ? "en" : "vi";
            BeginInvoke(new Action(delegate { app.SetLanguage(selected); }));
        };
        var look = Card(2, UiText.Pick("Widget trên màn hình", "Desktop widget"));
        Caption(look, UiText.Pick("Widget trên màn hình", "Desktop widget"), 0, 0, 2, true);
        widget = Toggle(look, UiText.Pick("Hiện widget", "Show widget"), 1,
                         v =>
                         {
                             if (v)
                                 P.TrayOnly = false;
                             P.Widget = v;
                             app.Apply();
                         }, 0, 1);
        onTop = Toggle(look, UiText.Pick("Luôn ở trên cùng", "Always on top"), 1,
                        v =>
                        {
                            P.AlwaysOnTop = v;
                            app.Apply();
                        }, 1, 1);
        Caption(look, UiText.Pick("Kiểu hiển thị", "Display style"), 3, 0, 1);
        styles = Picker(look, UiText.Pick("Kiểu hiển thị", "Display style"), 4, 0, 1);
        styles.Items.AddRange(WidgetPainter.Names);
        styles.SelectedIndexChanged += delegate
        {
            if (!syncing && styles.SelectedIndex >= 0)
            {
                P.WidgetStyle = WidgetPainter.Styles[styles.SelectedIndex];
                app.Apply();
                preview.Invalidate();
            }
        };
        opacityLabel = Caption(look, UiText.Pick("Độ đậm nền kính", "Glass opacity"), 3, 1, 1);
        opacity =
            Put(look,
                  new TrackBar { Minimum = 15, Maximum = 95, TickStyle = TickStyle.None, SmallChange = 1,
                                 LargeChange = 10, AccessibleName = UiText.Pick("Độ đậm nền kính", "Glass opacity") },
                  4, 1, 1);
        opacity.ValueChanged += delegate
        {
            if (!syncing)
            {
                P.GlassOpacity = opacity.Value;
                app.Apply();
                preview.Invalidate();
            }
        };
        textColor = Picker(look, UiText.Pick("Màu chữ widget", "Widget text color"), 5, 1, 1);
        textColor.Items.AddRange(new object[] { UiText.Pick("Tự động theo nền", "Automatic contrast"), UiText.Pick("Trắng · cho hình nền tối", "White · for dark backgrounds"), UiText.Pick("Đậm · cho hình nền sáng", "Dark · for light backgrounds") });
        textColor.SelectedIndexChanged += delegate
        {
            if (!syncing && textColor.SelectedIndex >= 0)
            {
                P.WidgetTextColor = textColor.SelectedIndex == 2 ? "dark" : textColor.SelectedIndex == 1 ? "light" : "auto";
                app.Apply();
            }
        };
        preview = Put(look, new Panel { AccessibleName = UiText.Pick("Xem trước widget", "Widget preview"), Tag = 98 }, 2);
        preview.Paint += PreviewPaint;
        var position = Card(2, UiText.Pick("Vị trí widget", "Widget position"));
        Caption(position, UiText.Pick("Vị trí widget", "Widget position"), 0, 0, 1, true);
        Caption(position, UiText.Pick("Màn hình", "Display"), 0, 1, 1, true);
        corner = Picker(position, UiText.Pick("Vị trí widget", "Widget position"), 1, 0, 1);
        corner.Items.AddRange(
            new[] { UiText.Pick("Góc dưới phải", "Bottom right"), UiText.Pick("Góc dưới trái", "Bottom left"), UiText.Pick("Góc trên phải", "Top right"), UiText.Pick("Góc trên trái", "Top left"), UiText.Pick("Tự do", "Free position") });
        corner.SelectedIndexChanged += delegate
        {
            if (!syncing && corner.SelectedIndex >= 0)
            {
                P.Corner = corners[corner.SelectedIndex];
                app.Apply();
            }
        };
        display = Picker(position, UiText.Pick("Màn hình", "Display"), 1, 1, 1);
        foreach (var s in Screen.AllScreens)
        {
            screenIds.Add(s.DeviceName);
            display.Items.Add(UiText.Pick("Màn hình ", "Display ") + screenIds.Count + (s.Primary ? UiText.Pick(" · chính", " · primary") : ""));
        }
        display.SelectedIndexChanged += delegate
        {
            if (!syncing && display.SelectedIndex >= 0)
            {
                P.Display = screenIds[display.SelectedIndex];
                app.Apply();
            }
        };
        var formats = Card(2, UiText.Pick("Mạng và thời gian", "Network and time"));
        Caption(formats, UiText.Pick("Bộ điều hợp mạng", "Network adapter"), 0, 0, 1, true);
        network = Picker(formats, UiText.Pick("Bộ điều hợp mạng", "Network adapter"), 1, 0, 1);
        network.Items.Add(UiText.Pick("Tự chọn theo kết nối đang dùng", "Use the active connection"));
        try
        {
            foreach (var nic in HardwareSampler.Adapters())
            {
                networkIds.Add(nic.Id);
                network.Items.Add(nic.Name);
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
        }
        if (!String.IsNullOrEmpty(P.NetworkId) && !networkIds.Contains(P.NetworkId))
        {
            networkIds.Add(P.NetworkId);
            network.Items.Add(UiText.Pick("Card đã chọn · ngoại tuyến", "Selected adapter · offline"));
        }
        network.SelectedIndexChanged += delegate
        {
            if (!syncing && network.SelectedIndex >= 0)
            {
                P.NetworkId = networkIds[network.SelectedIndex];
                app.Apply();
            }
        };
        Caption(formats, UiText.Pick("Giờ đặt lại hạn mức", "Quota reset time"), 0, 1, 1, true);
        resetStyle = Picker(formats, UiText.Pick("Giờ đặt lại hạn mức", "Quota reset time"), 1, 1, 1);
        resetStyle.Items.AddRange(new object[] { UiText.Pick("Đếm ngược (sau 3 giờ)", "Countdown (in 3 hours)"), UiText.Pick("Giờ cụ thể (23/11 13:29)", "Date and time (23/11 13:29)") });
        resetStyle.SelectedIndexChanged += delegate
        {
            if (!syncing && resetStyle.SelectedIndex >= 0)
            {
                P.ResetClock = resetStyle.SelectedIndex == 1;
                app.Apply();
            }
        };
    }
    void UpdateAppearance()
    {
        widget.Checked = P.Widget && !P.TrayOnly;
        onTop.Checked = P.AlwaysOnTop;
        resetStyle.SelectedIndex = P.ResetClock ? 1 : 0;
        styles.SelectedIndex = Math.Max(0, Array.IndexOf(WidgetPainter.Styles, P.WidgetStyle));
        opacity.Value = P.GlassOpacity;
        opacity.Enabled = P.WidgetStyle == "glass";
        bool bare = P.WidgetStyle == "text";
        textColor.Visible = bare;
        textColor.SelectedIndex = P.WidgetTextColor == "dark" ? 2 : P.WidgetTextColor == "light" ? 1 : 0;
        opacity.Visible = opacity.Enabled;
        opacityLabel.Visible = bare || opacity.Enabled;
        var styleCard = (SettingsCard)styles.Parent;
        if (styleCard.GetRow(styles) != (bare ? 5 : 4)) styleCard.SetRow(styles, bare ? 5 : 4);
        opacityLabel.Text = bare ? UiText.Pick("Màu chữ", "Text color") : UiText.Pick("Độ đậm nền kính · ", "Glass opacity · ") + P.GlassOpacity + "%";
        opacityLabel.Enabled = bare || opacity.Enabled;
        corner.SelectedIndex = Math.Max(0, Array.IndexOf(corners, P.Corner));
        display.SelectedIndex = Math.Max(0, screenIds.IndexOf(P.Display ?? Screen.PrimaryScreen.DeviceName));
        network.SelectedIndex = Math.Max(0, networkIds.IndexOf(P.NetworkId));
        var sample = app.Rows(true).Take(2);
        string signature = String.Join("|", sample.Select(r => r.Label + " " + r.Value + "/" + r.Percent + "/" + r.Level)) + "|" +
                           P.WidgetStyle + "|" + P.GlassOpacity + "|" + P.WidgetTextColor;
        if (signature != previewSignature)
        {
            previewSignature = signature;
            preview.Invalidate();
        }
    }
    void PreviewPaint(object sender, PaintEventArgs e)
    {
        if (preview.Width == 0 || preview.Height == 0) return;
        var rows = app.Rows(true).Take(2).ToList();
        int height = WidgetPainter.Height(rows, P.WidgetStyle);
        float fit = Math.Min(scale, (float)(preview.Height - 4) / height);
        var size = new Size(Ui.D(fit, WidgetPainter.Width(rows, P.WidgetStyle)), Ui.D(fit, height));
        int x = (preview.Width - size.Width) / 2, y = (preview.Height - size.Height) / 2;
        using (var back = new Bitmap(preview.Width, preview.Height))
        {
            using (var g = Graphics.FromImage(back))
            using (var wallpaper = new System.Drawing.Drawing2D.LinearGradientBrush(preview.ClientRectangle,
                       Color.FromArgb(65, 81, 86), Color.FromArgb(232, 233, 220), 20f))
                g.FillRectangle(wallpaper, preview.ClientRectangle);
            e.Graphics.DrawImageUnscaled(back, 0, 0);
            string inks = P.WidgetStyle == "text" && P.WidgetTextColor == "auto"
                        ? WidgetPainter.TextInks(rows, fit, 0, size.Height, (right, py) =>
                              back.GetPixel(Math.Max(0, Math.Min(back.Width - 1, x + (right ? size.Width - 1 : 0))), y + py)) : null;
            using (var bmp = WidgetPainter.Render(rows, P.WidgetStyle, P.GlassOpacity, fit, size, 0, P.WidgetTextColor, inks))
                e.Graphics.DrawImageUnscaled(bmp, x, y);
        }
    }
}
}
