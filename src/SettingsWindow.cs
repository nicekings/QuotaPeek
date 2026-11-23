using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NhipPC
{
internal sealed partial class SettingsWindow : Form
{
    readonly MonitorApp app;
    readonly Icon brandIcon;
    readonly List<Panel> pages = new List<Panel>();
    CheckBox pc, ai, trayOnly, widget, onTop;
    CheckedListBox pcList, aiList, pins;
    Label trayStyleHelp, ringSizeLabel;
    ComboBox ringSize;
    ComboBox styles, corner, display, network, trayStyle, resetStyle;
    TrackBar opacity;
    Label opacityLabel;
    Panel preview;
    Label emptyPins;
    Button emptyPinsAction, clearPins;
    string pinTarget;
    readonly string[] corners = { "br", "bl", "tr", "tl", "free" };
    readonly List<string> networkIds = new List<string> { null }, screenIds = new List<string>();
    bool syncing, constructed, scheduled;
    float scale;
    class Choice
    {
        public readonly string Key, Name;
        public Choice(string k, string n)
        {
            Key = k;
            Name = n;
        }
        public override string ToString()
        {
            return Name;
        }
    }
    Preferences P
    {
        get {
            return app.Prefs;
        }
    }
    public SettingsWindow(MonitorApp app)
    {
        this.app = app;
        Text = UiText.Pick("Cài đặt QuotaPeek", "QuotaPeek settings");
        Name = "QuotaPeek.Settings";
        StartPosition = FormStartPosition.CenterScreen;
        Icon = brandIcon = Brand.Icon(32);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        Font = SystemFonts.MessageBoxFont;
        var h = Handle;
        scale = Theme.Scale(this);
        SizeForScreen(Screen.FromControl(this).WorkingArea);
        syncing = true;
        BuildNavigation();
        var presets = Card(0, UiText.Pick("Chế độ theo dõi", "Tracking mode"), 3);
        Caption(presets, UiText.Pick("Chế độ theo dõi", "Tracking mode"), 0, 0, 3, true);
        Put(presets, Ui.Button(UiText.Pick("Chỉ AI", "AI only"), UiText.Pick("Chỉ hiển thị hạn mức AI", "Show AI quota only"), delegate { Groups(false, true); }), 1, 0, 1);
        Put(presets, Ui.Button(UiText.Pick("Chỉ PC", "PC only"), UiText.Pick("Chỉ hiển thị chỉ số máy tính", "Show PC metrics only"), delegate { Groups(true, false); }), 1, 1, 1);
        Put(presets, Ui.Button(UiText.Pick("PC và AI", "PC and AI"), UiText.Pick("Hiển thị máy tính và AI", "Show PC and AI metrics"), delegate { Groups(true, true); }), 1, 2, 1);
        var items = Card(0, UiText.Pick("Chọn chỉ số", "Select metrics"));
        pc = Toggle(items, UiText.Pick("Máy tính", "Computer"), 0, v => Groups(v, P.ShowAi), 0, 1);
        ai = Toggle(items, UiText.Pick("Hạn mức AI", "AI quota"), 0, v => Groups(P.ShowPc, v), 1, 1);
        pcList = Put(items, CreateList(UiText.Pick("Chỉ số máy tính", "PC metrics"), ChangeItem), 1, 0, 1);
        aiList = Put(items, CreateList(UiText.Pick("Dịch vụ và kỳ hạn", "Services and quota periods"), ChangeItem), 1, 1, 1);
        pcList.Tag = aiList.Tag = 178;
        Caption(items, UiText.Pick("Các chỉ số đã chọn được lưu lại khi bạn tắt một nhóm.", "Your selections are saved when you turn off a group."), 2);
        BuildTrayOptions();
        BuildAppearance();
        BuildConnections();
        BuildAbout();
        StyleSettings();
        SelectPage(0);
        KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
                Close();
        };
        syncing = false;
        constructed = true;
        UpdateValues();
    }
    void BuildAbout()
    {
        const int page = 4;
        const string url = "https://premiumvns.com";
        var info = Card(page, "QuotaPeek");
        Caption(info, "QuotaPeek", 0, 0, 2, true);
        Caption(info, UiText.Pick("Theo dõi hạn mức AI và chỉ số máy tính ngay trên màn hình Windows.", "Track AI quota and PC performance on your Windows desktop."), 1);
        var web = Card(page, UiText.Pick("Trang web", "Website"));
        Caption(web, UiText.Pick("Trang web", "Website"), 0, 0, 2, true);
        var link = Put(web, new LinkLabel { Text = url, AutoSize = true, TabStop = true,
                       AccessibleName = UiText.Pick("Mở trang web premiumvns.com", "Visit premiumvns.com") }, 1);
        link.Links.Clear();
        link.Links.Add(0, url.Length, url);
        link.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e)
        {
            app.OpenLink((string)e.Link.LinkData);
        };
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && brandIcon != null)
            brandIcon.Dispose();
    }
    void SizeForScreen(Rectangle area)
    {
        MinimumSize = new Size(Math.Min(area.Width, Ui.D(scale, 380)), Math.Min(area.Height, Ui.D(scale, 330)));
        ClientSize = new Size(Math.Min(area.Width - 20, Ui.D(scale, 750)), Math.Min(area.Height - 60, Ui.D(scale, 600)));
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!constructed || e.DeviceDpiNew <= 0) return;
        scale = e.DeviceDpiNew / 96f;
        SystemThemeChanged();
    }
    CheckedListBox CreateList(string name, Action<string, bool> changed)
    {
        var list = new CheckedListBox { CheckOnClick = true, IntegralHeight = false, AccessibleName = name,
                                        HorizontalScrollbar = true, BorderStyle = BorderStyle.FixedSingle };
        list.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
        {
            if (!syncing)
                changed(((Choice)list.Items[e.Index]).Key, e.NewValue == CheckState.Checked);
        };
        return list;
    }
    void Groups(bool computer, bool quotas)
    {
        P.ShowPc = computer;
        P.ShowAi = quotas;
        app.Apply();
    }
    void ChangeItem(string key, bool check)
    {
        P.Enabled[key] = check;
        ScheduleApply();
    }
    void ChangePin(string key, bool check)
    {
        if (P.TrayItems == null)
            P.TrayItems = DisplayData.VisibleTray(P, app.Choices).Select(c => c.Key).ToList();
        P.TrayItems.Remove(key);
        if (check)
            P.TrayItems.Add(key);
        ScheduleApply();
    }
    void ScheduleApply()
    {
        if (scheduled)
            return;
        scheduled = true;
        BeginInvoke(new Action(delegate {
            scheduled = false;
            app.Apply();
        }));
    }
    void FillList(CheckedListBox list, List<Choice> items, Func<string, bool> isChecked)
    {
        bool changed =
            list.Items.Count != items.Count || !list.Items.Cast<Choice>()
                                                    .Select(v => v.Key + "|" + v.Name)
                                                    .SequenceEqual(items.Select(v => v.Key + "|" + v.Name));
        if (changed)
        {
            int top = list.TopIndex;
            string selection = list.SelectedItem == null ? null : ((Choice)list.SelectedItem).Key;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var item in items)
                    list.Items.Add(item, isChecked(item.Key));
                if (selection != null)
                    list.SelectedIndex = items.FindIndex(v => v.Key == selection);
                if (items.Count > 0)
                    list.TopIndex = Math.Max(0, Math.Min(top, items.Count - 1));
            }
            finally
            {
                list.EndUpdate();
            }
        }
        else
            for (int i = 0; i < items.Count; i++)
            {
                bool check = isChecked(items[i].Key);
                if (list.GetItemChecked(i) != check)
                    list.SetItemChecked(i, check);
            }
    }
    List<Choice> Content(string[] categories)
    {
        var list = new List<Choice>();
        foreach (string id in categories)
        {
            list.Add(new Choice(id, UiText.FromReading(Catalog.Name(id))));
            if (!P.Checked(id))
                continue;
            foreach (var c in app.Choices.Where(c => c.Parent == id && c.Key != id))
                list.Add(new Choice(c.Key, "     " + c.Label));
        }
        return list;
    }
    public void SelectTarget(string id)
    {
        if (Catalog.Providers.Contains(id))
        {
            source.SelectedIndex = Array.IndexOf(Catalog.Providers, id);
            SelectPage(3);
            if (sourceState == null)
                BuildGuide();
        }
        else
            SelectPage(id == "tray" ? 1 : id == "style" ? 2 : id == "about" ? 4 : 0);
        UpdateValues();
    }
    public void SystemThemeChanged()
    {
        Font = SystemFonts.MessageBoxFont;
        StyleSettings();
        preview.Invalidate();
    }
    public void UpdateValues()
    {
        if (!constructed || syncing || scheduled)
            return;
        syncing = true;
        try
        {
            switch (selectedPage)
            {
            case 0:
                pc.Checked = P.ShowPc;
                ai.Checked = P.ShowAi;
                pcList.Enabled = P.ShowPc;
                aiList.Enabled = P.ShowAi;
                FillList(pcList, Content(Catalog.Sensors), P.Checked);
                FillList(aiList, Content(Catalog.Providers), P.Checked);
                break;
            case 1:
                UpdateTrayOptions();
                break;
            case 2:
                UpdateAppearance();
                break;
            case 3:
                UpdateGuide();
                break;
            }
        }
        finally
        {
            syncing = false;
        }
    }
    void UpdateTrayOptions()
    {
        trayOnly.Checked = P.TrayOnly;
        trayStyle.SelectedIndex = P.TrayStyle == "ring" ? 2 : P.TrayStyle == "bars" ? 1 : 0;
        bool ring = P.TrayStyle == "ring";
        ringSizeLabel.Visible = ringSize.Visible = ring;
        ringSize.SelectedIndex = (int)P.GetRingSize();
        trayStyleHelp.Text = ring
            ? UiText.Pick("Vòng ngoài và số giữa hiển thị phần hạn mức còn lại. Chọn kỳ hạn cho từng dịch vụ bên dưới.", "The outer ring and center number show remaining quota. Choose a period for each service below.")
            : P.TrayStyle == "bars"
            ? UiText.Pick("Mỗi dịch vụ có một biểu tượng. Thanh dày hiển thị kỳ ngắn, thanh mảnh hiển thị kỳ dài. Phần tô màu là hạn mức còn lại.", "Each service has one icon. The thick bar shows the shorter period and the thin bar shows the longer one. The filled portion is the remaining quota.")
            : UiText.Pick("Mỗi kỳ có một biểu tượng riêng. Màu vạch cho biết dịch vụ. Vạch dài là kỳ tuần, vạch ngắn là kỳ 5 giờ, vạch đứt là hạn mức tuần theo mô hình và dấu chấm là số dư credit.", "Each period has its own icon. Color identifies the service. A long line means weekly, a short line means five hours, a dashed line means weekly per model, and a dot means credits.");
        var pinned = DisplayData.VisibleTray(P, app.Choices);
        UpdateRingEditor(ring, pinned);
        var active = new HashSet<string>(pinned.Select(c => c.Key));
        var choices = app.Choices.Where(c => P.On(c.Key)).Select(c => new Choice(c.Key, c.Name)).ToList();
        FillList(pins, choices, active.Contains);
        clearPins.Enabled = active.Count > 0 || (P.TrayItems != null && P.TrayItems.Count > 0);
        bool empty = choices.Count == 0;
        int lines = Math.Max(3, Math.Min(7, choices.Count));
        pins.Height = lines * pins.ItemHeight + 4;
        pins.Visible = !empty;
        emptyPins.Visible = empty;
        emptyPinsAction.Visible = empty;
        if (!empty)
            return;
        string waiting = Catalog.Providers.FirstOrDefault(id => P.On(id) && app.Hub[id].Snapshot == null);
        pinTarget = waiting ?? "content";
        emptyPins.Text = waiting == null
                             ? UiText.Pick("Chọn chỉ số trong mục Nội dung để ghim cạnh đồng hồ.", "Select metrics under Content to pin them to the taskbar.")
                             : UiText.Pick("Các kỳ hạn sẽ xuất hiện khi QuotaPeek đọc được hạn mức từ tài khoản.", "Quota periods appear after QuotaPeek reads your account’s limits.");
        emptyPinsAction.Text = waiting == null ? UiText.Pick("Chọn nội dung…", "Select content…") : UiText.Pick("Kết nối ", "Connect ") + Catalog.Name(waiting) + "…";
        emptyPinsAction.AccessibleName = emptyPinsAction.Text;
    }
}
}
