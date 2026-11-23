using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace NhipPC
{
internal sealed class SettingsCard : TableLayoutPanel
{
    public SettingsCard(string name, int columns)
    {
        AccessibleName = name;
        ColumnCount = columns;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        DoubleBuffered = true;
        for (int i = 0; i < columns; i++) ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        if (Parent == null || !Visible) return base.GetPreferredSize(proposedSize);
        int width = Math.Max(1, Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal);
        var heights = new int[Controls.Cast<Control>().Select(c => GetRow(c) + 1).DefaultIfEmpty(0).Max()];
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            int row = GetRow(c);
            if (row < 0) continue; // WinForms measures during insertion, before it assigns the cell.
            int columnWidth = (width - Padding.Horizontal) * GetColumnSpan(c) / ColumnCount;
            int available = Math.Max(1, columnWidth - c.Margin.Horizontal);
            int height = c.AutoSize ? c.GetPreferredSize(new Size(available, 0)).Height : c.Height;
            heights[row] = Math.Max(heights[row], height + c.Margin.Vertical);
        }
        return new Size(width, Padding.Vertical + heights.Sum());
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Base);
        if (Width < 16 || Height < 16) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = new GraphicsPath())
        using (var surface = new SolidBrush(Theme.Surface))
        using (var line = new Pen(Theme.Edge))
        {
            float d = Math.Min(Width - 2, Math.Min(Height - 2, 18 * Theme.Scale(this)));
            path.AddArc(.5f, .5f, d, d, 180, 90);
            path.AddArc(Width - d - 1, .5f, d, d, 270, 90);
            path.AddArc(Width - d - 1, Height - d - 1, d, d, 0, 90);
            path.AddArc(.5f, Height - d - 1, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(surface, path);
            g.DrawPath(line, path);
        }
    }
}

internal sealed class SettingsNavItem : RadioButton
{
    readonly string glyph;
    public SettingsNavItem(string title, string glyph)
    {
        Text = AccessibleName = title;
        this.glyph = glyph;
        Appearance = Appearance.Button;
        TextAlign = ContentAlignment.MiddleLeft;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        float s = Theme.Scale(this);
        var g = e.Graphics;
        Color bg = Checked ? (Theme.Dark ? Color.FromArgb(47, 66, 81) : Color.FromArgb(227, 238, 247)) : Theme.Surface;
        Color ink = Checked ? Theme.Accent : Theme.Soft;
        if (SystemInformation.HighContrast && Checked) { bg = SystemColors.Highlight; ink = SystemColors.HighlightText; }
        g.Clear(bg);
        g.ScaleTransform(s, s);
        if (Checked) using (var brush = new SolidBrush(ink)) g.FillRectangle(brush, 0, 10, 3, Height/s - 20);
        using (var iconFont = new Font("Segoe MDL2 Assets", 17, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(ink))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center })
            g.DrawString(glyph, iconFont, brush, new RectangleF(12, 0, 26, Height/s), format);
        Theme.Words(g, Text, 42, 0, Width/s - 46, Height/s, 14, ink, Checked, false);
        g.ResetTransform();
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -2, -2), ink, bg);
    }
}

internal sealed partial class SettingsWindow
{
    readonly string[] sectionNames = { UiText.Pick("Nội dung", "Content"), UiText.Pick("Cạnh đồng hồ", "Taskbar"), UiText.Pick("Giao diện", "Appearance"), UiText.Pick("Kết nối AI", "AI connections"), UiText.Pick("Giới thiệu", "About") };
    readonly SettingsNavItem[] navItems = new SettingsNavItem[5];
    readonly Panel navigation = new Panel(), pageHost = new Panel();
    readonly ComboBox compactNav = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = UiText.Pick("Mục cài đặt", "Settings section") };
    readonly Panel brand = new Panel();
    readonly TableLayoutPanel[] sections = new TableLayoutPanel[5];
    int selectedPage = -1;

    void BuildNavigation()
    {
        navigation.AccessibleName = UiText.Pick("Điều hướng cài đặt", "Settings navigation");
        navigation.Controls.Add(brand);
        brand.Paint += delegate(object sender, PaintEventArgs e) {
            float s = scale;
            e.Graphics.ScaleTransform(s, s);
            e.Graphics.DrawIcon(brandIcon, new Rectangle(0, 7, 27, 27));
            Theme.Words(e.Graphics, "QuotaPeek", 35, 0, 112, 41, 19, Theme.Text, true, false);
        };
        string[] glyphs = { "\uE14C", "\uE91B", "\uE771", "\uE774", "\uE946" };
        for (int i = 0; i < sectionNames.Length; i++)
        {
            int index = i;
            var button = navItems[i] = new SettingsNavItem(sectionNames[i], glyphs[i]);
            navigation.Controls.Add(button);
            button.CheckedChanged += delegate { if (button.Checked) SelectPage(index); };
            var page = new Panel { Name = "section" + i, AccessibleName = sectionNames[i], AutoScroll = true,
                                   Dock = DockStyle.Fill, Visible = false };
            var stack = sections[i] = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Top,
                                                            AutoSizeMode = AutoSizeMode.GrowAndShrink };
            stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            page.Controls.Add(stack);
            pages.Add(page);
            pageHost.Controls.Add(page);
            var title = new Label { Text = sectionNames[i], AutoSize = true, Dock = DockStyle.Top, Tag = "title" };
            stack.Controls.Add(title, 0, 0);
        }
        compactNav.Items.AddRange(sectionNames);
        compactNav.SelectedIndexChanged += delegate { if (compactNav.SelectedIndex >= 0) SelectPage(compactNav.SelectedIndex); };
        Controls.AddRange(new Control[] { navigation, compactNav, pageHost });
        ClientSizeChanged += delegate { if (constructed) ArrangeShell(); };
        ArrangeShell();
    }
    void ArrangeShell()
    {
        int width = ClientSize.Width, height = ClientSize.Height;
        bool compact = width < Ui.D(scale, 650);
        int side = compact ? 0 : Ui.D(scale, 166);
        int top = compact ? Ui.D(scale, 45) : 0;
        navigation.Visible = !compact;
        compactNav.Visible = compact;
        navigation.SetBounds(0, 0, side, height);
        Ui.Place(brand, scale, 18, 15, 144, 41);
        for (int i = 0; i < navItems.Length; i++)
            Ui.Place(navItems[i], scale, 8, 76 + i * 42, 150, 36);
        compactNav.SetBounds(Ui.D(scale, 14), Ui.D(scale, 9), Math.Max(1, width - Ui.D(scale, 28)), compactNav.PreferredHeight);
        pageHost.SetBounds(side, top, Math.Max(1, width - side), Math.Max(1, height - top));
    }
    void SelectPage(int index)
    {
        if (index == selectedPage) return;
        if (selectedPage >= 0) pages[selectedPage].Visible = false;
        selectedPage = index;
        pages[index].Visible = true;
        pages[index].BringToFront();
        navItems[index].Checked = true;
        compactNav.SelectedIndex = index;
        if (constructed && index == 3) BuildGuide();
        if (constructed) UpdateValues();
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            int step = (keyData & Keys.Shift) != 0 ? -1 : 1;
            SelectPage((selectedPage + step + pages.Count) % pages.Count);
            if (compactNav.Visible) compactNav.Focus(); else navItems[selectedPage].Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    SettingsCard Card(int page, string name, int columns = 2)
    {
        var card = new SettingsCard(name, columns);
        sections[page].Controls.Add(card, 0, sections[page].Controls.Count);
        StyleControl(card);
        return card;
    }
    T Put<T>(SettingsCard card, T c, int row, int col = 0, int span = 0) where T : Control
    {
        c.Dock = DockStyle.Top;
        card.Controls.Add(c, col, row);
        card.SetColumnSpan(c, span == 0 ? card.ColumnCount : span);
        StyleControl(c);
        return c;
    }
    Label Caption(SettingsCard card, string text, int row, int col = 0, int span = 0, bool heading = false)
    {
        return Put(card, new Label { Text = text, AutoSize = true, UseMnemonic = false, Tag = heading ? "heading" : "body",
                                     TextAlign = ContentAlignment.MiddleLeft }, row, col, span);
    }
    ComboBox Picker(SettingsCard card, string name, int row, int col = 0, int span = 0)
    {
        return Put(card, new ComboBox { AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList }, row, col, span);
    }
    CheckBox Toggle(SettingsCard card, string text, int row, Action<bool> change, int col = 0, int span = 0)
    {
        var box = Put(card, new CheckBox { Text = text, AccessibleName = text, AutoSize = true }, row, col, span);
        box.CheckedChanged += delegate { if (!syncing) change(box.Checked); };
        return box;
    }
    void StyleControl(Control c)
    {
        int spacing = Ui.D(scale, 4);
        var card = c as SettingsCard;
        if (card != null)
        {
            card.Padding = new Padding(Ui.D(scale, 12), Ui.D(scale, 10), Ui.D(scale, 12), Ui.D(scale, 10));
            card.Margin = new Padding(0, 0, 0, Ui.D(scale, 12));
            card.BackColor = Theme.Surface;
            card.Invalidate();
            return;
        }
        bool title = Object.Equals(c.Tag, "title"), heading = Object.Equals(c.Tag, "heading");
        c.Margin = title ? new Padding(0, 0, 0, Ui.D(scale, 14)) : new Padding(spacing);
        c.Font = Theme.Font((title ? 25 : heading ? 15 : 13) * scale, title || heading);
        c.ForeColor = c is Label && !title && !heading ? Theme.Soft : Theme.Text;
        c.BackColor = title ? Theme.Base : Theme.Surface;
        if (c.Tag is int) c.Height = Ui.D(scale, (int)c.Tag);
        var b = c as Button;
        if (b != null)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.Edge;
            b.FlatAppearance.MouseOverBackColor = Theme.Base;
            b.UseVisualStyleBackColor = false;
            b.AutoSize = false;
            b.Dock = DockStyle.Top;
            b.Height = Math.Max(Ui.D(scale, 32), b.Font.Height + Ui.D(scale, 10));
        }
        var link = c as LinkLabel;
        if (link != null) link.LinkColor = Theme.Accent;
    }
    void StyleSettings()
    {
        BackColor = pageHost.BackColor = Theme.Base;
        navigation.BackColor = brand.BackColor = Theme.Surface;
        foreach (var stack in sections)
        {
            stack.BackColor = stack.Parent.BackColor = Theme.Base;
            stack.Padding = new Padding(Ui.D(scale, 20), Ui.D(scale, 18), Ui.D(scale, 20), Ui.D(scale, 8));
            foreach (Control element in stack.Controls)
            {
                StyleControl(element);
                foreach (Control child in element.Controls) StyleControl(child);
            }
        }
        navigation.Invalidate(true);
        ArrangeShell();
    }
}
}
