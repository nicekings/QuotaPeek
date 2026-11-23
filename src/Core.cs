using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

[assembly:AssemblyTitle("QuotaPeek")]
[assembly:AssemblyProduct("QuotaPeek")]
[assembly:AssemblyVersion("2.1.0.0")]
namespace NhipPC
{
internal static class Catalog
{
    public static readonly string[] Providers = { "codex", "claude", "gemini", "antigravity", "grok" };
    public static readonly string[] Sensors = { "cpu", "ram", "disk", "io", "net" };
    public static string Name(string key)
    {
        switch (key)
        {
        case "cpu":
            return "CPU";
        case "ram":
            return "RAM";
        case "disk":
            return "Ổ đĩa";
        case "io":
            return "Đọc / ghi";
        case "net":
            return "Mạng";
        case "codex":
            return "ChatGPT / Codex";
        case "claude":
            return "Claude";
        case "gemini":
            return "Gemini Chat";
        case "antigravity":
            return "Antigravity";
        case "grok":
            return "Grok";
        default:
            return key;
        }
    }
}

public enum RingSize { Medium, Large }

public class RingFields
{
    public string Center { get; set; }
    public string Outer { get; set; }
}

public class Preferences
{
    public Dictionary<string, bool> Enabled { get; set; }
    public bool ShowPc { get; set; }
    public bool ShowAi { get; set; }
    public string Language { get; set; }
    public string WidgetStyle { get; set; }
    public string WidgetTextColor { get; set; }
    // Earlier builds stored just a dark-ink override.
    public bool DarkWidgetText { get; set; }
    public int GlassOpacity { get; set; }
    public bool TrayOnly { get; set; }
    // Null preserves the original single-icon preference. An empty list means launcher only.
    public List<string> TrayItems { get; set; }
    public string TrayKey { get; set; }
    public string TrayName { get; set; }
    public bool Widget { get; set; }
    public bool AlwaysOnTop { get; set; }
    public string Corner { get; set; }
    public string Display { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public string NetworkId { get; set; }
    // "numbers": one icon per chosen number. "bars": one 2-bar icon per AI service.
    public string TrayStyle { get; set; }
    // Older preferences stored the size as a boolean.
    public bool LargeTrayRing { get; set; }
    public RingSize? TrayRingSize { get; set; }
    public Dictionary<string, RingFields> RingFieldsByProvider { get; set; }
    public RingSize GetRingSize()
    {
        if (TrayRingSize.HasValue && Enum.IsDefined(typeof(RingSize), TrayRingSize.Value)) return TrayRingSize.Value;
        return LargeTrayRing ? RingSize.Large : RingSize.Medium;
    }
    // false: "Đặt lại sau 3 giờ 24 phút"; true: "Đặt lại 23/11 13:29".
    public bool ResetClock { get; set; }
    public Preferences()
    {
        Language = "vi";
        Enabled = new Dictionary<string, bool>();
        ShowPc = ShowAi = true;
        WidgetStyle = "system";
        GlassOpacity = 72;
        TrayKey = "cpu";
        LargeTrayRing = true;
        RingFieldsByProvider = new Dictionary<string, RingFields>();
        Corner = "br";
        AlwaysOnTop = true;
    }
    public bool On(string key)
    {
        string parent = ParentOf(key);
        bool group = Catalog.Providers.Contains(parent) ? ShowAi : ShowPc;
        return group && Checked(parent) && (key == parent || Checked(key));
    }
    public bool Checked(string key)
    {
        bool on;
        return Enabled != null && Enabled.TryGetValue(key, out on) ? on : key != "io";
    }
    public static string ParentOf(string key)
    {
        var parts = (key ?? "").Split('/');
        return parts[0] == "ai" && parts.Length > 1 ? parts[1] : parts[0];
    }
}

public class Quota
{
    public string Key { get; set; }
    public string Label { get; set; }
    public double? Used { get; set; }
    public string Balance { get; set; }
    public long Reset { get; set; }
    public string ResetNote { get; set; }
    public bool Expired(long now)
    {
        return Reset > 0 && now >= Reset;
    }
    public string Value(long now)
    {
        return Expired(now) ? "Chờ cập nhật" : Used.HasValue ? Fmt.Percent(Used.Value) : Balance;
    }
}
public class Reading
{
    public string Provider { get; set; }
    public string AccountKey { get; set; }
    public int RetryAfterSeconds { get; set; }
    public bool NeedsLogin { get; set; }
    public bool NeedsInstall { get; set; }
    public bool ScopeUnverified { get; set; }
    public bool NeedsSetup { get; set; }
    public bool RateLimited { get; set; }
    public string Source { get; set; }
    public string Plan { get; set; }
    public string Notice { get; set; }
    public long Time { get; set; }
    public List<Quota> Windows { get; set; }
    public Reading()
    {
        Windows = new List<Quota>();
    }
    public static Reading Start(string provider, string source)
    {
        return new Reading { Provider = provider, Source = source, Time = Fmt.Now };
    }
    public void Add(Quota q)
    {
        if (q != null && !String.IsNullOrWhiteSpace(q.Key) &&
            (q.Used.HasValue || !String.IsNullOrWhiteSpace(q.Balance)) && !Windows.Any(w => w.Key == q.Key))
            Windows.Add(q);
    }
    public bool Valid(string id)
    {
        return Provider == id && !String.IsNullOrWhiteSpace(Source) && Time > 0 && Windows != null &&
               Windows.Count > 0 &&
               Windows.All(w => w != null && !String.IsNullOrWhiteSpace(w.Key) &&
                                !String.IsNullOrWhiteSpace(w.Label) &&
                                (w.Used.HasValue ? w.Used >= 0 && w.Used <= 100
                                                 : !String.IsNullOrWhiteSpace(w.Balance)));
    }
}

internal static class Fmt
{
    public static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    public static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public static long Now
    {
        get {
            return (long)(DateTime.UtcNow - Epoch).TotalSeconds;
        }
    }
    public static string Percent(double v)
    {
        return v.ToString("0.#", UiText.NumberCulture) + "%";
    }
    public static string Size(double bytes)
    {
        return (bytes / 1073741824d).ToString("0.#", UiText.NumberCulture) + " GB";
    }
    public static string Speed(double bytes)
    {
        if (bytes >= 1073741824)
            return (bytes / 1073741824).ToString("0.#", UiText.NumberCulture) + " GB/s";
        if (bytes >= 1048576)
            return (bytes / 1048576).ToString("0.#", UiText.NumberCulture) + " MB/s";
        return (bytes / 1024).ToString("0", UiText.NumberCulture) + " KB/s";
    }
    public static string Local(long stamp, string format)
    {
        return stamp > 0 && stamp < 253402300800L ? Epoch.AddSeconds(stamp).ToLocalTime().ToString(format, Vi)
                                                  : "";
    }
    public static string Period(long minutes)
    {
        if (minutes == 10080)
            return "Tuần";
        if (minutes <= 0)
            return "Kỳ sử dụng";
        if (minutes % 1440 == 0)
            return (minutes / 1440) + " ngày";
        if (minutes % 60 == 0)
            return (minutes / 60) + " giờ";
        return minutes + " phút";
    }
    public static string Age(long time)
    {
        long s = Math.Max(0, Now - time);
        return s < 5       ? "vừa xong"
               : s < 60    ? s + " giây trước"
               : s < 3600  ? (s / 60) + " phút trước"
               : s < 86400 ? (s / 3600) + " giờ trước"
                           : Local(time, "dd/MM HH:mm");
    }
    public static string Fold(string value)
    {
        if (String.IsNullOrEmpty(value))
            return "";
        var b = new StringBuilder();
        foreach (char c in value.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                b.Append(c == 'đ' ? 'd' : c);
        return b.ToString().Trim();
    }
}

internal static class Json
{
    static JavaScriptSerializer Codec()
    {
        return new JavaScriptSerializer { MaxJsonLength = 4194304, RecursionLimit = 128 };
    }
    public static Dictionary<string, object> Parse(string value)
    {
        return Codec().Deserialize<Dictionary<string, object>>(value);
    }
    public static T Decode<T>(string value)
    {
        return Codec().Deserialize<T>(value);
    }
    public static string Encode(object value)
    {
        return Codec().Serialize(value);
    }
    public static object Get(IDictionary<string, object> d, string key)
    {
        object v;
        return d != null && d.TryGetValue(key, out v) ? v : null;
    }
    public static Dictionary<string, object> Obj(IDictionary<string, object> d, string key)
    {
        return Get(d, key) as Dictionary<string, object>;
    }
    public static string Str(IDictionary<string, object> d, string key)
    {
        var v = Get(d, key);
        return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
    }
    public static double? Number(IDictionary<string, object> d, string key)
    {
        double n;
        return Double.TryParse(Str(d, key), NumberStyles.Float, CultureInfo.InvariantCulture, out n) &&
                       !Double.IsNaN(n) && !Double.IsInfinity(n)
                   ? (double?)n
                   : null;
    }
    public static long Stamp(IDictionary<string, object> d, string key)
    {
        var v = Number(d, key);
        if (v.HasValue && v.Value > 0 && v.Value < 253402300800L)
            return (long)v.Value;
        DateTimeOffset dt;
        return DateTimeOffset.TryParse(Str(d, key), CultureInfo.InvariantCulture,
                                       DateTimeStyles.AssumeUniversal, out dt)
                   ? (long)(dt.UtcDateTime - Fmt.Epoch).TotalSeconds
                   : 0;
    }
    public static double? Pct(double? v)
    {
        return v.HasValue && v >= 0 && v <= 100 ? v : null;
    }
}

internal static class Store
{
    static readonly Lazy<string> defaultRoot = new Lazy<string>(delegate {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string current = Path.Combine(local, "QuotaPeek"), legacy = Path.Combine(local, "NhipPC");
        return Directory.Exists(legacy) && !Directory.Exists(current) ? legacy : current;
    });
    public static string Root
    {
        get {
            return Environment.GetEnvironmentVariable("QUOTAPEEK_DATA_DIR") ??
                   Environment.GetEnvironmentVariable("NHIPPC_DATA_DIR") ?? defaultRoot.Value;
        }
    }
    public static string PathFor(string name)
    {
        return Path.Combine(Root, name + ".json");
    }
    public static T Read<T>(string path)
        where T : class
    {
        try
        {
            using (var s = new FileStream(
                       path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite |
                           FileShare.Delete)) using (var r = new StreamReader(s, Encoding.UTF8, true))
            {
                if (s.Length > 4194304)
                    return null;
                return Json.Decode<T>(r.ReadToEnd());
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        return null;
    }
    public static Preferences Load()
    {
        var p = Read<Preferences>(PathFor("preferences")) ?? new Preferences();
        // Previous releases serialized ExtraLarge as 2.
        if (p.TrayRingSize == (RingSize)2)
        {
            p.TrayRingSize = RingSize.Large;
            p.LargeTrayRing = true;
        }
        if (p.RingFieldsByProvider == null) p.RingFieldsByProvider = new Dictionary<string, RingFields>();
        if (p.Enabled == null)
            p.Enabled = new Dictionary<string, bool>();
        if (String.IsNullOrEmpty(p.TrayKey))
            p.TrayKey = "cpu";
        if (p.TrayStyle != "bars" && p.TrayStyle != "ring")
            p.TrayStyle = "numbers";
        if (Array.IndexOf(WidgetPainter.Styles, p.WidgetStyle) < 0)
            p.WidgetStyle = "system";
        if (p.WidgetTextColor != "dark" && p.WidgetTextColor != "light" && p.WidgetTextColor != "auto")
            p.WidgetTextColor = p.DarkWidgetText ? "dark" : "auto";
        p.GlassOpacity = Math.Max(15, Math.Min(95, p.GlassOpacity));
        if (p.TrayItems != null)
            p.TrayItems = p.TrayItems.Where(k => !String.IsNullOrWhiteSpace(k)).Distinct().ToList();
        return p;
    }
    public static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents, new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
    public static void Save(Preferences p)
    {
        Write(PathFor("preferences"), Json.Encode(p));
    }
    // One snapshot file per provider, account and plan. Older readings never overwrite newer ones.
    public static void SaveReading(Reading r)
    {
        var scope = AccountScope.From(r);
        if (scope == null || !Catalog.Providers.Contains(r.Provider))
            return;
        string name = scope.Name("last-");
        using (var gate = new Mutex(false, @"Local\NhipPC." + name))
        {
            bool held = false;
            try
            {
                try
                {
                    held = gate.WaitOne(2000);
                }
                catch (AbandonedMutexException)
                {
                    held = true;
                }
                if (!held)
                    return;
                string path = PathFor(name);
                var old = Read<Reading>(path);
                if (old != null && old.Time > r.Time)
                    return;
                Write(path, Json.Encode(r));
            }
            finally
            {
                if (held)
                    gate.ReleaseMutex();
            }
        }
    }
    // Remove 1.x files that were not tied to an account or are no longer read.
    public static void RemoveLegacy()
    {
        try
        {
            if (!Directory.Exists(Root))
                return;
            foreach (var file in Directory.GetFiles(Root, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                bool unscoped = name.StartsWith("last-", StringComparison.Ordinal) && name.IndexOf('-', 5) < 0;
                if (unscoped || name.StartsWith("event-", StringComparison.Ordinal))
                    File.Delete(file);
            }
        }
        catch (IOException) {}
        catch (UnauthorizedAccessException) {}
    }
}
}
