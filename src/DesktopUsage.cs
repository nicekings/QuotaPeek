using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace NhipPC
{
// Gemini Chat has no CLI or API for its plan limits, so QuotaPeek reads the Usage Limits panel
// of the open Gemini for Windows window. Claude and Antigravity use their CLIs instead.
internal static class DesktopUsage
{
    delegate bool WindowProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(WindowProc proc, IntPtr data);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    static string[] Names(string id)
    {
        return id == "gemini" ? new[] { "Gemini", "Google Gemini" } : new string[0];
    }
    public static List<IntPtr> Windows(string id)
    {
        var pids = new HashSet<uint>();
        foreach (var n in Names(id))
            foreach (var p in Process.GetProcessesByName(n))
                using (p) pids.Add((uint)p.Id);
        var windows = new List<IntPtr>();
        if (pids.Count > 0)
            EnumWindows(delegate(IntPtr h, IntPtr unused) {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid) && IsWindowVisible(h) && !IsIconic(h))
                    windows.Add(h);
                return true;
            }, IntPtr.Zero);
        return windows;
    }
    static string[] Anchors(string id)
    {
        return new[] { "Usage Limits", "Usage limits", "Usage limit", "Usage", "Plan usage", "Plan usage limits",
                       "Account usage", "Mức sử dụng", "Giới hạn sử dụng", "Hạn mức", "Hạn mức sử dụng" };
    }
    public static Reading Read(string id)
    {
        return ReadWindows(id, Windows(id));
    }
    static string AppName(string id)
    {
        return "Gemini";
    }
    static string PanelPath(string id)
    {
        return "Settings → Usage Limits";
    }
    internal static string NoWindowNotice(string id)
    {
        return "Mở hoặc khôi phục cửa sổ " + AppName(id) + ", rồi chọn " + PanelPath(id) + ".";
    }
    static string IdentityNotice(string id)
    {
        return "Đã thấy số hạn mức. QuotaPeek cần thấy email và gói trong cùng bảng để xác minh tài khoản.";
    }
    internal static Reading ReadWindows(string id, IEnumerable<IntPtr> windows)
    {
        var blank = Reading.Start(id, Catalog.Name(id) + " Desktop");
        Reading candidate = null;
        bool windowFound = false, inspected = false, panelFound = false, panelRead = false;
        var names = new OrCondition(
            Anchors(id)
                .Select(n => (Condition) new PropertyCondition(AutomationElement.NameProperty, n,
                                                               PropertyConditionFlags.IgnoreCase))
                .ToArray());
        foreach (IntPtr h in windows)
        {
            windowFound = true;
            try
            {
                var root = AutomationElement.FromHandle(h);
                var visited = new List<AutomationElement>();
                var anchors = root.FindAll(TreeScope.Descendants, names);
                inspected = true;
                foreach (AutomationElement anchor in anchors)
                {
                    if (anchor.Current.IsOffscreen)
                        continue;
                    var panel = UsageSection(anchor, root);
                    if (panel == null || visited.Any(previous => Automation.Compare(previous, panel)))
                        continue;
                    panelFound = true;
                    visited.Add(panel);
                    var reading = ReadPanel(panel, id, blank.Source);
                    panelRead = true;
                    if (!reading.Valid(id))
                        continue;
                    var scope = AccountScope.From(reading);
                    if (candidate != null && (scope == null || !scope.Matches(candidate)))
                        return Ambiguous(blank);
                    if (candidate == null || reading.Windows.Count > candidate.Windows.Count)
                        candidate = reading;
                }
            }
            catch (ElementNotAvailableException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (COMException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        if (AccountScope.From(candidate) != null)
            return candidate;
        if (candidate != null)
            blank.Notice = IdentityNotice(id);
        else if (!windowFound)
            blank.Notice = NoWindowNotice(id);
        else if (panelRead)
            blank.Notice = "Đã thấy " + PanelPath(id) + ". QuotaPeek đang chờ bảng hiển thị số hạn mức.";
        else if (!inspected || panelFound)
            blank.Notice = "Chưa đọc được cửa sổ " + AppName(id) + ". Giữ bảng " + PanelPath(id) + " hiển thị để thử lại.";
        else
            blank.Notice = "Chưa thấy bảng hạn mức. Trong " + AppName(id) + ", mở " + PanelPath(id) + " và giữ bảng đó hiển thị.";
        return blank;
    }
    static Reading Ambiguous(Reading blank)
    {
        blank.Notice = "Có nhiều bảng hạn mức. Chỉ mở bảng của tài khoản cần theo dõi.";
        return blank;
    }
    static AutomationElement UsageSection(AutomationElement anchor, AutomationElement root)
    {
        var panel = anchor;
        var kind = panel.Current.ControlType;
        if (kind == ControlType.TabItem)
        {
            object selection;
            if (!panel.TryGetCurrentPattern(SelectionItemPattern.Pattern, out selection) ||
                !((SelectionItemPattern)selection).Current.IsSelected)
                return null;
        }
        if (kind == ControlType.Text || kind == ControlType.Header || kind == ControlType.HeaderItem)
        {
            // An unnamed section can advertise its purpose through its own leading heading.
            // Never search upwards for a larger region containing unrelated visible fields.
            panel = TreeWalker.ControlViewWalker.GetParent(anchor);
            if (panel == null || !String.IsNullOrEmpty(panel.Current.Name))
                return null;
            var first = TreeWalker.ControlViewWalker.GetFirstChild(panel);
            if (first == null || !Automation.Compare(first, anchor))
                return null;
            kind = panel.Current.ControlType;
        }
        if ((kind != ControlType.Group && kind != ControlType.Pane && kind != ControlType.Document && kind != ControlType.TabItem) ||
            panel.Current.IsOffscreen || Automation.Compare(panel, root))
            return null;
        // A heading in a conversation can also say "Usage". Only a named Usage region inside
        // a Settings container can attest that its account fields describe the displayed quotas.
        var parent = panel;
        for (int level = 0; level < 10 && !Automation.Compare(parent, root); level++)
        {
            parent = TreeWalker.ControlViewWalker.GetParent(parent);
            if (parent == null || parent.Current.IsOffscreen)
                break;
            var parentType = parent.Current.ControlType;
            if ((parentType == ControlType.Window || parentType == ControlType.Pane ||
                 parentType == ControlType.Group || parentType == ControlType.Document) &&
                Regex.IsMatch(Fmt.Fold(parent.Current.Name),
                              @"^(?:(?:gemini|google gemini)\s*[-–—·:]?\s*)?(?:settings|preferences|cai dat|tuy chon)" +
                              @"(?:\s*[-–—·:]\s*(?:gemini|google gemini))?$"))
                return panel;
        }
        return null;
    }
    static Reading ReadPanel(AutomationElement panel, string id, string source)
    {
        var result = new List<string>();
        var fields = new List<string>();
        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsOffscreenProperty);
        cache.TreeScope = TreeScope.Element | TreeScope.Children;
        cache.Add(AutomationElement.LabeledByProperty);
        cache.Add(RangeValuePattern.ValueProperty);
        cache.Add(RangeValuePattern.MaximumProperty);
        cache.Add(RangeValuePattern.MinimumProperty);
        var kinds =
            new[] { ControlType.Text,        ControlType.Header,   ControlType.HeaderItem, ControlType.Group,
                    ControlType.ProgressBar, ControlType.DataItem, ControlType.ListItem };
        var condition = new OrCondition(
            kinds.Select(t => (Condition) new PropertyCondition(AutomationElement.ControlTypeProperty, t))
                .ToArray());
        bool bounded;
        using (cache.Activate())
        {
            var nodes = panel.FindAll(TreeScope.Descendants, condition);
            // A partial tree cannot establish that the panel identifies only one account.
            bounded = nodes.Count <= 500;
            for (int i = 0; i < Math.Min(500, nodes.Count); i++)
            {
                var n = nodes[i];
                if (n.Cached.IsOffscreen)
                    continue;
                if (bounded && (n.Cached.ControlType == ControlType.Group || n.Cached.ControlType == ControlType.DataItem ||
                                n.Cached.ControlType == ControlType.ListItem))
                {
                    // Many apps expose a settings field as a row with two sibling text controls.
                    // Never pair adjacent strings from separate rows or different snapshots.
                    var children = n.CachedChildren;
                    if (children != null && children.Count == 2 && children[0].Cached.ControlType == ControlType.Text &&
                        children[1].Cached.ControlType == ControlType.Text && !children[0].Cached.IsOffscreen &&
                        !children[1].Cached.IsOffscreen)
                        AddField(fields, children[0].Cached.Name, children[1].Cached.Name);
                }
                string name = n.Cached.Name;
                if (!String.IsNullOrWhiteSpace(name) && name.Length < 450)
                {
                    if (result.Count == 0 || result[result.Count - 1] != name)
                        result.Add(name);
                    if (bounded)
                    {
                        fields.Add(name);
                        var label = n.Cached.LabeledBy;
                        if (label != null && !label.Current.IsOffscreen)
                            AddField(fields, label.Current.Name, name);
                    }
                }
                if (n.Cached.ControlType == ControlType.ProgressBar && (name ?? "").IndexOf('%') < 0)
                {
                    var max = n.GetCachedPropertyValue(RangeValuePattern.MaximumProperty, true);
                    var min = n.GetCachedPropertyValue(RangeValuePattern.MinimumProperty, true);
                    var val = n.GetCachedPropertyValue(RangeValuePattern.ValueProperty, true);
                    if (val is double && max is double && min is double && (double)max == 100 &&
                        (double)min == 0)
                        result.Add(Fmt.Percent((double)val));
                }
            }
        }
        var reading = UsagePanel.Parse(id, source, result);
        Identify(reading, bounded, fields);
        return reading;
    }

    internal static void Identify(Reading reading, bool bounded, IEnumerable<string> fields)
    {
        if (bounded)
            AccountPanel.Bind(reading, fields);
        if (reading.Windows.Count > 0 && AccountScope.From(reading) == null)
            reading.Notice = IdentityNotice(reading.Provider);
    }
    static void AddField(List<string> fields, string label, string value)
    {
        // UIA can label a control with the previous static text even if that text is
        // already a complete field. Only a standalone label can introduce a value.
        if (label != null && Regex.IsMatch(label, @"^[\p{L} ]{1,32}:?$"))
            fields.Add(label.TrimEnd(' ', ':') + ": " + value);
    }
}
}
