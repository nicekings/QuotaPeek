using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace NhipPC
{
internal sealed partial class SettingsWindow
{
    SettingsCard ringEditor;
    ComboBox ringProvider, centerField, outerField;
    Label centerCaption, outerCaption, ringInfo;

    void BuildRingEditor()
    {
        ringEditor = Card(1, UiText.Pick("Nội dung vòng theo dịch vụ", "Ring details by service"));
        Caption(ringEditor, UiText.Pick("Nội dung vòng theo dịch vụ", "Ring details by service"), 0, 0, 2, true);
        ringProvider = Picker(ringEditor, UiText.Pick("Dịch vụ của vòng", "Service"), 1);
        centerCaption = Caption(ringEditor, UiText.Pick("Số giữa", "Center number"), 2, 0, 1);
        outerCaption = Caption(ringEditor, UiText.Pick("Vòng ngoài", "Outer ring"), 2, 1, 1);
        centerField = Picker(ringEditor, UiText.Pick("Số giữa", "Center number"), 3, 0, 1);
        outerField = Picker(ringEditor, UiText.Pick("Vòng ngoài", "Outer ring"), 3, 1, 1);
        ringInfo = Caption(ringEditor, "", 4);
        ringProvider.SelectedIndexChanged += delegate { if (!syncing) UpdateValues(); };
        centerField.SelectedIndexChanged += delegate { SaveRingField(centerField, true); };
        outerField.SelectedIndexChanged += delegate { SaveRingField(outerField, false); };
    }
    void SaveRingField(ComboBox picker, bool center)
    {
        if (syncing) return;
        var service = ringProvider.SelectedItem as Choice;
        var choice = picker.SelectedItem as Choice;
        if (service == null || choice == null) return;
        RingFields fields;
        if (!P.RingFieldsByProvider.TryGetValue(service.Key, out fields) || fields == null)
            P.RingFieldsByProvider[service.Key] = fields = new RingFields();
        if (center) fields.Center = choice.Key; else fields.Outer = choice.Key;
        if (fields.Center == null && fields.Outer == null) P.RingFieldsByProvider.Remove(service.Key);
        app.Apply();
    }
    static void SyncChoices(ComboBox box, List<Choice> choices, string selected)
    {
        if (!box.Items.Cast<Choice>().Select(c => c.Key).SequenceEqual(choices.Select(c => c.Key)) ||
            !box.Items.Cast<Choice>().Select(c => c.Name).SequenceEqual(choices.Select(c => c.Name)))
        {
            box.BeginUpdate();
            try { box.Items.Clear(); box.Items.AddRange(choices.ToArray()); }
            finally { box.EndUpdate(); }
        }
        box.SelectedIndex = choices.Count == 0 ? -1 : Math.Max(0, choices.FindIndex(c => c.Key == selected));
    }
    void UpdateRingEditor(bool ring, List<PinChoice> pinned)
    {
        ringEditor.Visible = ring;
        if (!ring) return;
        var eligible = pinned.Where(DisplayData.RingPeriod).ToList();
        var services = eligible.Select(c => c.Parent).Distinct().Select(id => new Choice(id, Catalog.Name(id))).ToList();
        var previous = ringProvider.SelectedItem as Choice;
        SyncChoices(ringProvider, services, previous == null ? null : previous.Key);
        var service = ringProvider.SelectedItem as Choice;
        ringProvider.Visible = centerCaption.Visible = outerCaption.Visible =
            centerField.Visible = outerField.Visible = service != null;
        if (service == null)
        {
            ringInfo.Text = UiText.Pick("Ghim một hạn mức 5 giờ hoặc tuần ở phía trên để tùy chỉnh vòng.", "Pin a five-hour or weekly quota above to customize the ring.");
            return;
        }
        var periods = eligible.Where(c => c.Parent == service.Key).ToList();
        RingFields fields;
        P.RingFieldsByProvider.TryGetValue(service.Key, out fields);
        SyncRingField(centerField, periods, fields == null ? null : fields.Center, UiText.Pick("Tự động · ưu tiên 5 giờ", "Automatic · prefer 5 hours"));
        SyncRingField(outerField, periods, fields == null ? null : fields.Outer, UiText.Pick("Tự động · ưu tiên tuần", "Automatic · prefer weekly"));
        var parts = DisplayData.RingParts(P, service.Key, periods);
        ringInfo.Text = UiText.Pick("Số giữa · ", "Center · ") + DisplayData.RingReading(parts.First()) +
                        UiText.Pick("\nVòng ngoài · ", "\nOuter ring · ") + DisplayData.RingReading(parts.Last());
    }
    static void SyncRingField(ComboBox picker, List<PinChoice> periods, string key, string automatic)
    {
        var choices = new List<Choice> { new Choice(null, automatic) };
        choices.AddRange(periods.Select(p => new Choice(p.Key, DisplayData.RingLabel(p))));
        if (key != null && !choices.Any(c => c.Key == key))
            choices.Add(new Choice(key, UiText.Pick("Kỳ đã chọn chưa có · tạm dùng tự động", "Selected period unavailable · using automatic selection")));
        SyncChoices(picker, choices, key);
    }

    void BuildTrayOptions()
    {
        var metrics = Card(1, UiText.Pick("Ghim chỉ số", "Pin metrics"));
        Caption(metrics, UiText.Pick("Ghim chỉ số", "Pin metrics"), 0, 0, 1, true);
        clearPins = Ui.Button(UiText.Pick("Bỏ chọn tất cả", "Clear all"), UiText.Pick("Bỏ chọn tất cả chỉ số", "Clear all selected metrics"), delegate {
            P.TrayItems = new System.Collections.Generic.List<string>();
            app.Apply();
        });
        Put(metrics, clearPins, 0, 1, 1);
        Caption(metrics, UiText.Pick("Chọn các chỉ số bạn muốn xem trên thanh tác vụ.", "Choose the metrics to show next to the clock."), 1);
        pins = Put(metrics, CreateList(UiText.Pick("Chọn chỉ số cạnh đồng hồ", "Choose taskbar metrics"), ChangePin), 2);
        emptyPins = Caption(metrics, "", 3);
        emptyPinsAction = Put(metrics, Ui.Button(UiText.Pick("Chọn nội dung…", "Select content…"), UiText.Pick("Chọn nội dung", "Select content"), delegate { SelectTarget(pinTarget); }), 4);
        var icons = Card(1, UiText.Pick("Biểu tượng hạn mức", "Quota icons"));
        Caption(icons, UiText.Pick("Kiểu biểu tượng AI", "AI icon style"), 0, 0, 1, true);
        ringSizeLabel = Caption(icons, UiText.Pick("Kích thước vòng", "Ring size"), 0, 1, 1, true);
        trayStyle = Picker(icons, UiText.Pick("Kiểu biểu tượng AI", "AI icon style"), 1, 0, 1);
        trayStyle.Items.AddRange(new[] { UiText.Pick("Dạng số", "Numbers"), UiText.Pick("Hai thanh", "Dual bars"), UiText.Pick("Vòng hạn mức", "Quota ring") });
        ringSize = Picker(icons, UiText.Pick("Kích thước vòng", "Ring size"), 1, 1, 1);
        ringSize.Items.AddRange(new[] { UiText.Pick("Vừa", "Medium"), UiText.Pick("Lớn", "Large") });
        trayStyle.SelectedIndexChanged += delegate
        {
            if (syncing || trayStyle.SelectedIndex < 0) return;
            P.TrayStyle = trayStyle.SelectedIndex == 2 ? "ring" : trayStyle.SelectedIndex == 1 ? "bars" : "numbers";
            app.Apply();
        };
        ringSize.SelectedIndexChanged += delegate
        {
            if (syncing || ringSize.SelectedIndex < 0) return;
            P.TrayRingSize = (RingSize)ringSize.SelectedIndex;
            P.LargeTrayRing = P.TrayRingSize != RingSize.Medium;
            app.Apply();
        };
        trayStyleHelp = Caption(icons, "", 2);
        BuildRingEditor();
        var taskbar = Card(1, UiText.Pick("Thanh tác vụ", "Taskbar"));
        trayOnly = Toggle(taskbar, UiText.Pick("Chỉ hiển thị trên thanh tác vụ", "Taskbar only"), 0, app.SetTrayOnly);
        Caption(taskbar, UiText.Pick("Ẩn widget và bảng chi tiết. Bấm vào biểu tượng để mở lại bảng.", "Hide the widget and dashboard. Click an icon to reopen the dashboard."), 1);
        Caption(taskbar, UiText.Pick("Nếu biểu tượng bị ẩn, mở danh sách biểu tượng ẩn (^) và kéo biểu tượng ra cạnh đồng hồ.", "If an icon is hidden, open the overflow menu (^) and drag the icon next to the clock."), 2);
        Put(taskbar, Ui.Button(UiText.Pick("Hiện cạnh đồng hồ", "Show next to clock"), UiText.Pick("Hiện biểu tượng cạnh đồng hồ", "Show icons next to the clock"), delegate {
            int count = app.PromoteTray();
            MessageBox.Show(this, count > 0
                ? UiText.Pick("Đã yêu cầu Windows hiển thị ", "Asked Windows to show ") + count + UiText.Pick(" biểu tượng QuotaPeek cạnh đồng hồ.", " QuotaPeek icons next to the clock.")
                : UiText.Pick("Windows chưa nhận diện được biểu tượng QuotaPeek. Vui lòng thử lại sau ít giây.", "Windows has yet to register the QuotaPeek icons. Try again in a few seconds."), "QuotaPeek");
        }), 3, 0, 1);
        Put(taskbar, Ui.Button(UiText.Pick("Cài đặt thanh tác vụ", "Taskbar settings"), UiText.Pick("Mở cài đặt thanh tác vụ của Windows", "Open Windows taskbar settings"),
                                delegate { app.OpenLink("ms-settings:taskbar"); }), 3, 1, 1);
    }
}
}
