using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace NhipPC
{
internal static class Theme
{
    public static Color Base, Surface, Edge, Text, Soft, Muted, Accent, Warn, Danger;
    public static bool Dark, TrayDark;
    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
    static string fontName;
    static float fontPoints;
    static FontStyle fontStyle;
    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr handle);
    static Theme()
    {
        Refresh();
    }
    static bool IsDark(string key)
    {
        return Convert.ToInt32(Registry.GetValue(
                   @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", key,
                   1)) == 0;
    }
    public static void Refresh()
    {
        using (var normal = SystemFonts.MessageBoxFont)
        {
            fontName = normal.Name;
            fontPoints = normal.SizeInPoints;
            fontStyle = normal.Style;
        }
        Dark = IsDark("AppsUseLightTheme");
        TrayDark = IsDark("SystemUsesLightTheme");
        Base = Dark ? Color.FromArgb(31, 32, 34) : Color.FromArgb(251, 250, 248);
        Surface = Dark ? Color.FromArgb(42, 43, 46) : Color.White;
        Edge = Dark ? Color.FromArgb(69, 70, 73) : Color.FromArgb(219, 218, 214);
        Text = Dark ? Color.WhiteSmoke : Color.FromArgb(35, 35, 34);
        Soft = Dark ? Color.FromArgb(210, 211, 211) : Color.FromArgb(70, 70, 68);
        Muted = Dark ? Color.Silver : Color.FromArgb(92, 92, 90);
        Accent = Dark ? Color.FromArgb(157, 207, 245) : Color.FromArgb(12, 88, 142);
        Warn = Dark ? Color.FromArgb(240, 190, 90) : Color.FromArgb(150, 92, 0);
        Danger = Dark ? Color.FromArgb(255, 128, 118) : Color.FromArgb(178, 34, 34);
        if (SystemInformation.HighContrast)
        {
            Base = Surface = SystemColors.Window;
            Edge = Muted = Soft = Text = SystemColors.WindowText;
            Accent = SystemColors.HotTrack;
            Warn = Danger = SystemColors.WindowText;
            TrayDark = SystemColors.Window.GetBrightness() < .5;
        }
    }
    public static Font Font(float size, bool bold)
    {
        var style = bold ? fontStyle | FontStyle.Bold : fontStyle;
        string key = fontName + "|" + fontPoints + "|" + size + "|" + style;
        Font f;
        if (!fonts.TryGetValue(key, out f))
            fonts[key] = f = new Font(fontName, size * fontPoints / 9f, style, GraphicsUnit.Pixel);
        return f;
    }
    public static float Scale(Control c)
    {
        uint d = c.IsHandleCreated ? GetDpiForWindow(c.Handle) : 96;
        return d == 0 ? 1 : d / 96f;
    }
    // Level 1 = 20% or less remaining, level 2 = 5% or less.
    public static Color ForLevel(int level, Color normal)
    {
        return level >= 2 ? Danger : level == 1 ? Warn : normal;
    }
    public static void Line(Graphics g, Color c, float x, float y, float x2, float y2)
    {
        using (var pen = new Pen(c)) g.DrawLine(pen, x, y, x2, y2);
    }
    public static void Words(Graphics g, string text, float x, float y, float w, float h, float size,
                             Color color, bool bold, bool right)
    {
        if (String.IsNullOrEmpty(text) || w <= 0 || h <= 0)
            return;
        using (var fmt =
                   new StringFormat {
                       Alignment = right ? StringAlignment.Far : StringAlignment.Near,
                       LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap,
                       Trimming = StringTrimming.EllipsisCharacter
                   }) using (var brush = new SolidBrush(color))
            g.DrawString(text, Font(size, bold), brush, new RectangleF(x, y, w, h), fmt);
    }
}

internal enum RowKind
{
    Section,
    Metric,
    Provider,
    Quota
}
internal class ViewRow
{
    public RowKind Kind;
    public string Label, Value, Note, ActionId, ProviderId;
    public double? Percent;
    public int Level;
    public ViewRow(RowKind kind, string name, string value, string note = null, double? pct = null,
                   string action = null)
    {
        Kind = kind;
        Label = name;
        Value = value;
        Note = note;
        Percent = pct;
        ActionId = action;
    }
}
internal class PinChoice
{
    public string Key, Name, Label, Number, Value, Tip, Note, LastValue;
    public double? Percent;
    public int Level;
    public bool Stale, IsBalance;
    public string Pace;
    // Set only on a combined 2-bar tray icon: the periods it draws, shortest period first.
    public List<PinChoice> Bars;
    public bool Ring;
    public RingSize RingSize;
    public string Parent
    {
        get {
            return Preferences.ParentOf(Key);
        }
    }
    public override string ToString()
    {
        return Name;
    }
}
internal static class DisplayData
{
    const string Dash = "—";
    // Every service shows the share of its limit that is left, like the Codex app.
    // Snapshots keep the provider's "used" value; only this layer turns it around.
    static double? Remaining(Quota quota)
    {
        return quota.Used.HasValue ? (double?)Math.Max(0, Math.Min(100, 100d - quota.Used.Value)) : null;
    }
    public static int Level(Quota quota)
    {
        return !quota.Used.HasValue ? 0 : quota.Used.Value >= 95 ? 2 : quota.Used.Value >= 80 ? 1 : 0;
    }
    static string QuotaValue(Quota quota, long now)
    {
        if (quota.Used.HasValue && !quota.Expired(now))
            return UiText.Pick("Còn lại ", "Remaining ") + Fmt.Percent(Remaining(quota).Value);
        double amount;
        if (!quota.Expired(now) && RemainingAmount(quota.Balance, out amount))
            return UiText.Pick("Còn ", "Remaining ") + amount.ToString(UiText.NumberCulture);
        return UiText.FromReading(quota.Value(now));
    }
    public static string QuotaSummary(string provider, Quota quota, long now)
    {
        return UiText.FromReading(quota.Label) + (quota.Used.HasValue ? " · " : " ") + QuotaValue(quota, now);
    }
    // Length of a quota period, from its key. 0 when unknown (credits, custom buckets).
    public static long Duration(string key)
    {
        string k = (key ?? "").ToLowerInvariant();
        int slash = k.LastIndexOf('/');
        string tail = slash >= 0 ? k.Substring(slash + 1) : k;
        long minutes;
        if (Int64.TryParse(tail, out minutes) && minutes > 0)
            return minutes * 60;                       // Codex: windowDurationMins
        if (tail == "five_hour" || tail.EndsWith("-5h") || tail.Contains("five"))
            return 5 * 3600;
        if (tail.StartsWith("seven_day") || tail.EndsWith("-weekly") || tail.Contains("week"))
            return 7 * 86400;
        return 0;
    }
    public static string Span(long seconds)
    {
        seconds = Math.Max(0, seconds);
        long d = seconds / 86400, h = seconds % 86400 / 3600, m = seconds % 3600 / 60;
        if (d > 0)
            return d + UiText.Pick(" ngày", " d") + (h > 0 ? " " + h + UiText.Pick(" giờ", " h") : "");
        if (h > 0)
            return h + UiText.Pick(" giờ", " h") + (m > 0 ? " " + m + UiText.Pick(" phút", " min") : "");
        return Math.Max(1, m) + UiText.Pick(" phút", " min");
    }
    public static string ResetText(long reset, long now, bool clock)
    {
        if (reset <= 0)
            return null;
        return clock || reset <= now ? UiText.Pick("Đặt lại ", "Resets ") + Fmt.Local(reset, "dd/MM HH:mm")
                                     : UiText.Pick("Đặt lại sau ", "Resets in ") + Span(reset - now);
    }
    // Compare actual use with an even pace across the period, like a fuel gauge's range estimate.
    // Returns null early in a period (under 3% elapsed) or when the period length is unknown.
    public static string Pace(string key, Quota q, long now)
    {
        long length = Duration(key);
        if (length <= 0 || q.Reset <= now || !q.Used.HasValue)
            return null;
        long start = q.Reset - length, elapsed = now - start;
        if (elapsed < length * 0.03 || elapsed > length)
            return null;
        double used = q.Used.Value, expected = 100d * elapsed / length;
        if (used >= 100)
            return UiText.Pick("Đã hết", "Depleted");
        if (used <= expected + 2)
            return UiText.Pick("Đủ đến kỳ đặt lại", "On track until reset");
        double rate = used / elapsed;                  // percent per second at the current pace
        long runout = now + (long)((100 - used) / rate);
        return runout >= q.Reset ? UiText.Pick("Đủ đến kỳ đặt lại", "On track until reset") : UiText.Pick("Dùng nhanh · hết lúc ", "At this pace, runs out ") + Fmt.Local(runout, "dd/MM HH:mm");
    }
    static string Short(string id)
    {
        return id == "codex" ? "Codex" : id == "antigravity" ? "Antigravity" : Catalog.Name(id);
    }
    static PinChoice Pc(string key, string name, string label, double? value, bool rate, string note = null)
    {
        string full = value.HasValue ? (rate ? Fmt.Speed(value.Value) : Fmt.Percent(value.Value)) : Dash;
        string number = Dash;
        if (value.HasValue)
        {
            double n = rate ? value.Value / 1024 : value.Value;
            number = rate && n >= 1024 ? (n >= 1048576 ? (n / 1048576).ToString("0.#", UiText.NumberCulture) + "G"
                                                       : (n / 1024).ToString("0.#", UiText.NumberCulture) + "M")
                                       : Math.Round(n).ToString(UiText.NumberCulture);
        }
        return new PinChoice { Key = key,       Name = name,
                               Label = label,   Value = full,
                               Number = number, Tip = name + " · " + full,
                               Note = note,     Percent = rate ? null : value };
    }
    public static List<PinChoice> Choices(HardwareFrame h, Func<string, ProviderState> state, bool clock = false)
    {
        var a = new List<PinChoice>();
        a.Add(Pc("cpu", "CPU", "CPU", h.Cpu, false));
        a.Add(Pc("ram", "RAM", "RAM", h.Ram, false,
                 h.TotalMemory == 0 ? null : Fmt.Size(h.UsedMemory) + " / " + Fmt.Size(h.TotalMemory)));
        foreach (var v in h.Volumes)
            a.Add(
                Pc("disk/" + v.Name, UiText.Pick("Ổ ", "Drive ") + v.Name, UiText.Pick("Ổ ", "Drive ") + v.Name, v.Used, false, UiText.Pick("Còn ", "Free ") + Fmt.Size(v.Free)));
        a.Add(Pc("io/read", UiText.Pick("Ổ đĩa · tốc độ đọc", "Disk · read speed"), UiText.Pick("Đọc", "Read"), h.Read, true));
        a.Add(Pc("io/write", UiText.Pick("Ổ đĩa · tốc độ ghi", "Disk · write speed"), UiText.Pick("Ghi", "Write"), h.Write, true));
        a.Add(Pc("net/rx", UiText.Pick("Mạng · tải xuống", "Network · download"), UiText.Pick("Tải xuống", "Download"), h.Receive, true, h.Adapter));
        a.Add(Pc("net/tx", UiText.Pick("Mạng · tải lên", "Network · upload"), UiText.Pick("Tải lên", "Upload"), h.Send, true, h.Adapter));
        long now = Fmt.Now;
        foreach (string id in Catalog.Providers)
        {
            var s = state(id);
            if (s == null || s.Snapshot == null)
                continue;
            var r = s.Snapshot;
            bool shown = s.ShowsNumbers, fresh = s.Fresh;
            string provider = Short(id);
            foreach (var q in r.Windows)
            {
                bool expired = q.Expired(now);
                string name = provider + " · " + UiText.FromReading(q.Label), val = shown ? QuotaValue(q, now) : Dash;
                double? percent = expired || !shown ? null : Remaining(q);
                string number = expired || !shown ? Dash
                                : percent.HasValue ? Math.Round(percent.Value).ToString(UiText.NumberCulture)
                                                   : ShortBalance(q.Balance);
                string key = "ai/" + id + "/" + q.Key;
                string pace = shown && !expired ? Pace(key, q, now) : null;
                string reset = ResetText(q.Reset, now, clock) ?? UiText.FromReading(q.ResetNote);
                a.Add(new PinChoice { Key = key, Pace = pace,
                                      Name = q.Used.HasValue ? name + UiText.Pick(" (còn lại)", " (remaining)") : name, Label = UiText.FromReading(q.Label),
                                      Value = val, Number = number, Percent = percent,
                                      Level = shown && !expired ? Level(q) : 0, Stale = !fresh,
                                      IsBalance = !q.Used.HasValue, LastValue = !shown && !expired ? QuotaValue(q, now) : null,
                                      Note = pace == null ? reset : reset == null ? pace : reset + " · " + pace,
                                      Tip = name + " · " + (shown ? val : QuotaValue(q, now) + UiText.Pick(" (chưa xác nhận lại)", " (unconfirmed)")) +
                                            " · " + (fresh ? UiText.Pick("Đọc ", "Checked ") : UiText.Pick("Lần cuối ", "Last seen ")) + Fmt.Local(r.Time, "dd/MM HH:mm") +
                                            " · " + r.Source +
                                            (reset == null ? "" : " · " + reset) + (pace == null ? "" : " · " + pace) });
            }
        }
        return a;
    }
    // The right-hand word of a service row in the overview. "›" marks rows that open the settings card.
    public static string StatusWord(ProviderState s, long now)
    {
        switch (s.Phase)
        {
        case Phase.Live:
            return s.Busy ? UiText.Pick("Đang đọc…", "Refreshing…") : Fmt.Local(s.Snapshot.Time, "HH:mm:ss");
        case Phase.Connecting:
            return UiText.Pick("Đang kết nối…", "Connecting…");
        case Phase.Stale:
            return UiText.Pick("Chưa cập nhật ›", "Out of date ›");
        case Phase.Cooldown:
            return UiText.Pick("Đang chờ ›", "Waiting ›");
        case Phase.NeedsInstall:
            return UiText.Pick("Cài đặt ›", "Settings ›");
        case Phase.NeedsLogin:
            return UiText.Pick("Đăng nhập ›", "Sign in ›");
        case Phase.NeedsSetup:
            return UiText.Pick("Thiết lập ›", "Set up ›");
        case Phase.Error:
            return UiText.Pick("Kết nối ›", "Connect ›");
        default:
            return UiText.Pick("Tắt", "Off");
        }
    }
    // Antigravity stores quantities as "Còn 12,5", including in older snapshots.
    static bool RemainingAmount(string balance, out double amount)
    {
        amount = 0;
        return balance != null && balance.StartsWith("Còn ", StringComparison.Ordinal) &&
               Double.TryParse(balance.Substring(4), System.Globalization.NumberStyles.Float, Fmt.Vi, out amount);
    }
    static string ShortBalance(string balance)
    {
        if (String.IsNullOrWhiteSpace(balance))
            return Dash;
        var amount = balance.Split(' ')[0];
        double n;
        if (!RemainingAmount(balance, out n) && !Double.TryParse(amount, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out n) &&
            !Double.TryParse(amount, out n))
            return Dash;
        return Math.Abs(n) >= 1000000 ? (n / 1000000).ToString("0.#", UiText.NumberCulture) + "M"
               : Math.Abs(n) >= 1000  ? (n / 1000).ToString("0.#", UiText.NumberCulture) + "k"
                                      : n.ToString(UiText.NumberCulture);
    }
    public static List<PinChoice> VisibleTray(Preferences p, List<PinChoice> choices)
    {
        var active = choices.Where(c => p.On(c.Key)).ToList();
        if (p.TrayItems != null)
            return p.TrayItems.Distinct()
                .Select(k => active.FirstOrDefault(c => c.Key == k))
                .Where(c => c != null)
                .ToList();
        var single = active.FirstOrDefault(c => c.Key == p.TrayKey);
        if (single == null && p.TrayKey == "cpu")
            single = active.FirstOrDefault(c => c.Percent.HasValue) ?? active.FirstOrDefault();
        return single == null ? new List<PinChoice>() : new List<PinChoice> { single };
    }
    internal static bool RingPeriod(PinChoice choice)
    {
        if (choice.IsBalance || !choice.Key.StartsWith("ai/", StringComparison.Ordinal)) return false;
        long duration = Duration(choice.Key);
        return duration == 18000 || duration == 604800;
    }
    internal static string RingLabel(PinChoice period)
    {
        return period.Key == "ai/claude/seven_day" ? UiText.Pick("Tuần chung", "Overall weekly") : period.Label;
    }
    static List<PinChoice> PeriodsByDuration(IEnumerable<PinChoice> pinned)
    {
        return pinned.GroupBy(v => Duration(v.Key)).OrderBy(g => g.Key)
                     .Select(g => g.OrderBy(v => v.Percent ?? Double.MaxValue).First()).ToList();
    }
    internal static List<PinChoice> RingParts(Preferences p, string id, List<PinChoice> pinned)
    {
        var defaults = PeriodsByDuration(pinned);
        if (defaults.Count == 0) return defaults;
        RingFields fields;
        if (!p.RingFieldsByProvider.TryGetValue(id, out fields) || fields == null) return defaults;
        var center = pinned.FirstOrDefault(v => v.Key == fields.Center) ?? defaults.First();
        var outer = pinned.FirstOrDefault(v => v.Key == fields.Outer) ?? defaults.Last();
        return center == outer ? new List<PinChoice> { center } : new List<PinChoice> { center, outer };
    }
    internal static string RingReading(PinChoice period)
    {
        if (period.LastValue != null)
            return RingLabel(period) + UiText.Pick(" lần cuối ", " last ") +
                   period.LastValue.Replace(UiText.Pick("Còn lại ", "Remaining "), "");
        return RingLabel(period) + " " + (period.Stale ? "~" : "") + period.Value.Replace(UiText.Pick("Còn lại ", "Remaining "), "");
    }
    static string RingTip(string service, List<PinChoice> displayed, IEnumerable<PinChoice> pinned)
    {
        var lines = new List<string> { service + UiText.Pick(" · Hạn mức còn lại", " · Remaining quota") };
        var generalWeek = pinned.FirstOrDefault(v => v.Key == "ai/claude/seven_day");
        if (generalWeek != null && !displayed.Contains(generalWeek)) lines.Add(RingReading(generalWeek));
        if (displayed.Count == 1)
            lines.Add(UiText.Pick("Số và vòng · ", "Number and ring · ") + RingReading(displayed[0]));
        else
        {
            lines.Add(UiText.Pick("Số giữa · ", "Center · ") + RingReading(displayed[0]));
            lines.Add(UiText.Pick("Vòng ngoài · ", "Outer ring · ") + RingReading(displayed[1]));
        }
        foreach (var other in pinned.Where(v => RingPeriod(v) && v != generalWeek && !displayed.Contains(v)))
        {
            string line = RingReading(other);
            if (("QuotaPeek · " + String.Join("\n", lines) + "\n" + line).Length <= 127) lines.Add(line);
        }
        return String.Join("\n", lines);
    }
    // The tray: one icon per chosen number, or in "bars" style one 2-bar icon per AI service.
    public static List<PinChoice> TrayIcons(Preferences p, List<PinChoice> choices)
    {
        var visible = VisibleTray(p, choices);
        bool ring = p.TrayStyle == "ring";
        if (!ring && p.TrayStyle != "bars")
            return visible;
        var result = new List<PinChoice>();
        foreach (var c in visible)
        {
            if (!c.Key.StartsWith("ai/", StringComparison.Ordinal))
            {
                result.Add(c);
                continue;
            }
            string id = c.Parent;
            long duration = Duration(c.Key);
            if (c.IsBalance || duration == 0 || (ring && duration != 18000 && duration != 604800))
            {
                result.Add(c);                     // e.g. a credit balance keeps its number
                continue;
            }
            if (result.Any(r => r.Bars != null && r.Parent == id))
                continue;
            var periods = visible.Where(v => v.Parent == id && !v.IsBalance && v.Key.StartsWith("ai/", StringComparison.Ordinal) &&
                                             Duration(v.Key) > 0);
            var group = ring
                ? RingParts(p, id, periods.Where(RingPeriod).ToList())
                : PeriodsByDuration(periods);
            if (group.Count > 2) group.RemoveRange(1, group.Count - 2);
            string service = group[0].Name.Substring(0, Math.Max(0, group[0].Name.IndexOf(" · ")));
            result.Add(new PinChoice {
                Key = "ai/" + id + "/bars", Name = service, Bars = group, Ring = ring, RingSize = p.GetRingSize(), Stale = group.Any(g => g.Stale),
                Level = group.Max(g => g.Level), Number = String.Join("/", group.Select(g => g.Number)),
                Tip = ring ? RingTip(service, group, periods) : service + " · " + String.Join(" · ", group.Select(g =>
                          g.LastValue != null ? RingReading(g) : g.Label + " " + (g.Stale ? "~ " : "") +
                          g.Value.Replace(UiText.Pick("Còn lại ", "Remaining "), UiText.Pick("còn ", "remaining "))))
            });
        }
        return result;
    }
    static void Traffic(List<ViewRow> rows, List<PinChoice> choices, bool compact, string key)
    {
        var a = choices.Where(c => c.Parent == key).ToList();
        if (a.Count == 0)
            return;
        if (a.Count == 1)
        {
            var c = a[0];
            rows.Add(new ViewRow(RowKind.Metric, c.Label, c.Value, compact ? null : c.Note));
            return;
        }
        string val =
            key == "net" ? "↓ " + a[0].Value + "   ↑ " + a[1].Value : a[0].Value + " / " + a[1].Value;
        rows.Add(new ViewRow(RowKind.Metric, key == "net" ? UiText.Pick("Mạng", "Network") : UiText.Pick("Đọc / ghi", "Read / write"), val,
                             compact ? null : a[0].Note));
    }
    public static List<ViewRow> Rows(Preferences p, List<PinChoice> all, Func<string, ProviderState> state,
                                     bool compact)
    {
        var rows = new List<ViewRow>();
        long now = Fmt.Now;
        var choices = all.Where(c => p.On(c.Key)).ToList();
        foreach (var c in choices.Where(c => c.Key == "cpu" || c.Key == "ram" || c.Parent == "disk"))
            rows.Add(new ViewRow(RowKind.Metric, c.Label, c.Value, compact ? null : c.Note, c.Percent));
        Traffic(rows, choices, compact, "io");
        Traffic(rows, choices, compact, "net");
        if (!compact && rows.Count > 0)
            rows.Insert(0, new ViewRow(RowKind.Section, UiText.Pick("Máy tính", "Computer"), UiText.Pick("Mỗi giây", "Every second")));
        bool firstProvider = true;
        foreach (string id in Catalog.Providers.Where(p.On))
        {
            var s = state(id);
            var r = s.Snapshot;
            var windows = choices.Where(c => c.Parent == id).ToList();
            if (r != null && windows.Count == 0)
                continue;
            if (!compact)
            {
                if (firstProvider)
                {
                    rows.Add(new ViewRow(RowKind.Section, UiText.Pick("Hạn mức AI", "AI quota"), UiText.Pick("Phần còn lại", "Remaining")));
                    firstProvider = false;
                }
                string note = r == null ? UiText.FromReading(s.Message)
                              : (s.Fresh ? r.Source : UiText.Pick("Lần cuối ", "Last seen ") + Fmt.Local(r.Time, "dd/MM HH:mm")) +
                                    (String.IsNullOrWhiteSpace(r.Plan) ? "" : " · " + r.Plan);
                rows.Add(new ViewRow(RowKind.Provider, Catalog.Name(id), StatusWord(s, now), note, null,
                                     s.Fresh && id != "codex" ? null : id) { ProviderId = id });
            }
            foreach (var c in windows)
            {
                string label = compact ? c.Name.Replace(UiText.Pick(" (còn lại)", " (remaining)"), "") : c.Label;
                rows.Add(new ViewRow(RowKind.Quota, label,
                                     (c.Stale && c.Number != Dash ? "~ " : "") + c.Value,
                                     compact ? null : c.Note, c.Percent) { Level = c.Level, ProviderId = id });
            }
        }
        if (rows.Count == 0)
        {
            string waiting = Catalog.Providers.FirstOrDefault(id => p.On(id) && state(id).Snapshot == null);
            rows.Add(new ViewRow(RowKind.Metric, waiting == null ? UiText.Pick("Nội dung", "Content") : UiText.Pick("Hạn mức AI", "AI quota"),
                                 waiting == null ? UiText.Pick("Chọn chỉ số ›", "Select metrics ›") : UiText.Pick("Kết nối ›", "Connect ›"), null, null,
                                 waiting ?? "content"));
        }
        return rows;
    }
}
}
