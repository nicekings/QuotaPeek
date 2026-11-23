using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NhipPC
{
internal static class QuotaReaders
{
    public static Reading Codex(Dictionary<string, object> data)
    {
        var r = Reading.Start("codex", "Codex App Server");
        var all = Json.Obj(data, "rateLimitsByLimitId");
        var fallback = Json.Obj(data, "rateLimits");
        if (all != null)
            foreach (var e in all)
                CodexBucket(r, e.Key, e.Value as Dictionary<string, object>);
        string defaultId = Json.Str(fallback, "limitId") ?? "codex";
        if (fallback != null && (all == null || !all.ContainsKey(defaultId)))
            CodexBucket(r, defaultId, fallback);
        return r;
    }
    static void CodexBucket(Reading r, string id, Dictionary<string, object> b)
    {
        if (b == null)
            return;
        r.Plan = r.Plan ?? Json.Str(b, "planType");
        string name = Json.Str(b, "limitName");
        if (String.IsNullOrWhiteSpace(name))
            name = id == "codex" ? "Codex" : id;
        foreach (string slot in new[] { "primary", "secondary" })
        {
            var w = Json.Obj(b, slot);
            var used = Json.Pct(Json.Number(w, "usedPercent"));
            if (!used.HasValue)
                continue;
            long mins = (long)(Json.Number(w, "windowDurationMins") ?? 0);
            string label = mins > 0 ? Fmt.Period(mins) : (slot == "primary" ? "Kỳ chính" : "Kỳ phụ");
            r.Add(new Quota { Key = id + "/" + (mins > 0 ? mins.ToString(CultureInfo.InvariantCulture) : slot),
                              Label = (id == "codex" ? "" : name + " · ") + label, Used = used,
                              Reset = Json.Stamp(w, "resetsAt") });
        }
        var credit = Json.Obj(b, "credits");
        string balance = Json.Str(credit, "balance");
        if (!String.IsNullOrWhiteSpace(balance))
            r.Add(new Quota { Key = id + "/credits", Label = "Số dư credit", Balance = balance + " credit" });
    }
    public static Reading StatusLine(string provider, Dictionary<string, object> data)
    {
        if (provider == "antigravity")
            return Antigravity(data);
        var r = Reading.Start("claude", "Claude Code");
        var limits = Json.Obj(data, "rate_limits");
        if (limits == null)
            return r;
        foreach (var e in limits.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var w = e.Value as Dictionary<string, object>;
            var used = Json.Pct(Json.Number(w, "used_percentage"));
            if (!used.HasValue)
                continue;
            string label;
            if (e.Key == "five_hour")
                label = "5 giờ";
            else if (e.Key == "seven_day")
                label = "Tuần";
            else if (e.Key.StartsWith("seven_day_", StringComparison.Ordinal))
                label = "Tuần · " + e.Key.Substring(10).Replace('_', ' ');
            else if (e.Key == "spend_limit")
                label = "Chi tiêu";
            else
                continue;
            r.Add(new Quota { Key = e.Key, Label = label, Used = used, Reset = Json.Stamp(w, "resets_at") });
        }
        return r;
    }
    // Every model group: Gemini models, and Claude and GPT models. Each has a weekly and a five-hour limit.
    static Reading Antigravity(Dictionary<string, object> data)
    {
        var r = Reading.Start("antigravity", "Antigravity CLI");
        CliIdentity.Bind(r, Json.Get(data, "email") as string, Json.Get(data, "plan_tier") as string,
                         Json.Get(data, "workspace_id") as string);
        var q = Json.Obj(data, "quota");
        if (q == null)
            return r;
        foreach (var e in q)
        {
            string id = e.Key;
            var w = e.Value as Dictionary<string, object>;
            if (w == null || Object.Equals(Json.Get(w, "disabled"), true))
                continue;
            var left = Json.Number(w, "remaining_fraction");
            var amount = Json.Number(w, "remaining_amount");
            string described = String.Join(" ", new[] { id, Json.Str(w, "name"), Json.Str(w, "label"),
                                                        Json.Str(w, "window"), Json.Str(w, "period") }
                                                    .Where(x => !String.IsNullOrWhiteSpace(x)));
            string period = AntigravityPeriod(described);
            string group = GroupLabel(Json.Str(w, "group_name"));
            string label;
            if (group != null)
                label = group + (period == null ? "" : " · " + period);
            else
            {
                label = Regex.Replace(id, @"(?i)[-_]?(weekly|seven[-_]day|7d)\b", " · Tuần");
                label = Regex.Replace(label, @"(?i)[-_]?(five[-_]hour|5h)\b", " · 5 giờ")
                            .Replace('-', ' ')
                            .Replace('_', ' ');
                label = Regex.Replace(label, @"(?i)^3p\b", "Claude & GPT");
                label = CultureInfo.InvariantCulture.TextInfo.ToUpper(label.Substring(0, 1)) + label.Substring(1);
            }
            long reset = Json.Stamp(w, "reset_time");
            var after = Json.Number(w, "reset_in_seconds");
            if (reset == 0 && after >= 0 && after <= 31536000)
                reset = r.Time + (long)after.Value;
            r.Add(new Quota { Key = id, Label = label,
                              Used = left >= 0 && left <= 1 ? (double?)(100 * (1 - left.Value)) : null,
                              Balance = amount.HasValue ? "Còn " + amount.Value.ToString("0.##", Fmt.Vi) : null,
                              Reset = reset });
        }
        return r;
    }
    static string AntigravityPeriod(string text)
    {
        string t = Fmt.Fold(text ?? "");
        if (Regex.IsMatch(t, @"weekly|seven[-_ ]day|\b7d\b|\bweek\b"))
            return "Tuần";
        if (Regex.IsMatch(t, @"five[-_ ]hour|\b5[-_ ]?h(our)?\b"))
            return "5 giờ";
        return null;
    }
    // "Gemini models" -> "Gemini", "Claude and GPT models" -> "Claude & GPT".
    static string GroupLabel(string name)
    {
        if (String.IsNullOrWhiteSpace(name))
            return null;
        string t = Regex.Replace(name.Trim(), @"(?i)\s*models?$", "");
        t = Regex.Replace(t, @"(?i)\s+and\s+", " & ");
        if (t.Length == 0)
            return null;
        if (t == t.ToUpperInvariant())
            t = Regex.Replace(t.ToLowerInvariant(), @"\b[a-z]", m => m.Value.ToUpperInvariant())
                    .Replace("Gpt", "GPT");
        return t;
    }

    // The /usage text report ("Models & Quota"), used when the CLI prints text instead of JSON:
    //   GEMINI MODELS / Weekly Limit Remaining / [####] 100.00% / CLAUDE AND GPT MODELS / ...
    internal static Reading AntigravityText(string text, string source)
    {
        var r = Reading.Start("antigravity", source);
        string group = null, period = null, email = null, plan = null;
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string line = raw.Trim().TrimStart('└', '│', ' ');
            string t = Fmt.Fold(line);
            if (t.Length == 0)
                continue;
            var field = Regex.Match(line, @"^(Account|Email|Plan|Tier|Plan tier)\s*:\s*(\S.*)$", RegexOptions.IgnoreCase);
            if (field.Success)
            {
                if (field.Groups[1].Value.ToLowerInvariant() == "account" || field.Groups[1].Value.ToLowerInvariant() == "email")
                    email = field.Groups[2].Value.Trim();
                else
                    plan = field.Groups[2].Value.Trim();
                continue;
            }
            if (Regex.IsMatch(t, @"^[a-z0-9 &+.-]+ models$") && !t.StartsWith("models within"))
            {
                group = GroupLabel(line);
                period = null;
                continue;
            }
            if (group != null && t.Contains("limit"))
            {
                period = AntigravityPeriod(t);
                continue;
            }
            var pct = Regex.Match(t, @"(\d{1,3}(?:[.,]\d+)?)\s*%");
            if (group != null && period != null && pct.Success)
            {
                double left;
                if (Double.TryParse(pct.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out left) && left >= 0 && left <= 100)
                {
                    string key = Regex.Replace(Fmt.Fold(group), @"[^a-z0-9]+", "-").Trim('-') +
                                 (period == "Tuần" ? "-weekly" : "-5h");
                    r.Add(new Quota { Key = key, Label = group + " · " + period, Used = 100 - left });
                }
                period = null;
            }
        }
        // The report may name only the account. The email alone then keys the snapshot.
        CliIdentity.Bind(r, email, String.IsNullOrWhiteSpace(plan) ? "Antigravity" : plan, null);
        return r;
    }
}

// Parse only quota sections. Missing periods stay absent, including on weekly-only plans.
internal static class UsagePanel
{
    static readonly Regex Percent =
        new Regex(@"(?<![\d.,-])(100(?:[.,]0+)?|\d{1,2}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);
    static readonly Regex Weekly =
        new Regex(@"\b(weekly|week|7[- ]?days?|seven[- ]day|7d|tuan|7 ngay)\b", RegexOptions.Compiled);
    static readonly Regex Five =
        new Regex(@"\b(5[- ]?(?:hours?|hrs?|h|gio)|five[- ]hour)\b", RegexOptions.Compiled);
    static readonly Regex ResetWord = new Regex(
        @"\b(resets?|refresh(?:es)?|renews?|dat lai|lam moi|khoi phuc|khoi tao lai)\b", RegexOptions.Compiled);
    static string Kind(string text, string provider)
    {
        // The text after "resets in" is a countdown, never evidence of a limit period.
        var cut = ResetWord.Match(text);
        if (cut.Success)
            text = text.Substring(0, cut.Index);
        if (Weekly.IsMatch(text))
            return "seven_day";
        if (Five.IsMatch(text) ||
            (provider == "claude" && (text.Contains("current session") || text.Contains("phien hien tai"))))
            return "five_hour";
        return null;
    }
    public static bool Heading(string raw, string provider)
    {
        string t = Fmt.Fold(raw).Trim(' ', ':');
        if (t.Length > 72)
            return false;
        if (provider == "antigravity")
            return Regex.IsMatch(t, @"^(models|mo hinh|model quotas?|models (&|and) quota|models & " +
                                    @"quota|quota (&|and) credits|han muc mo hinh)$");
        return Regex.IsMatch(
            t, @"^(usage|plan usage( limits)?|usage( and| &)? billing|usage limits?|account usage|muc su " +
               @"dung|gioi han su dung|han muc( su dung)?|gioi han cua goi|muc su dung cua goi)$");
    }
    static string Variant(string folded)
    {
        var m = Regex.Match(folded, @"\b(sonnet|opus|fable|haiku|flash|pro)\b");
        return m.Success ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Value) : "";
    }
    public static Reading Parse(string provider, string source, IEnumerable<string> tokens)
    {
        var r = Reading.Start(provider, source);
        var part = new List<string>();
        string key = null, label = null, model = "";
        Action flush = delegate
        {
            if (key != null)
                ReadSection(r, key, label, part);
            part.Clear();
        };
        foreach (var raw in tokens)
        {
            if (String.IsNullOrWhiteSpace(raw))
                continue;
            foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string clean = line.Trim().Trim('│', '┃', '║', '┊', '|', '•', '▸', '▹', ' ');
                string t = Fmt.Fold(clean);
                if (t.Length > 420)
                    continue;
                if (provider == "antigravity" &&
                    Regex.IsMatch(t, @"^(?:claude|gpt|other|third.party)\b.*(?:models|mo hinh)$"))
                {
                    flush();
                    key = null;
                    model = "";
                    continue;
                }
                if (Regex.IsMatch(t, @"^(extra usage|additional usage|usage credits|context window|context " +
                                     @"usage|su dung bo sung|ngan sach bo sung|credit balance)\b"))
                {
                    flush();
                    key = null;
                    model = "";
                    continue;
                }
                bool isModel = provider == "antigravity" && Regex.IsMatch(t, @"^gemini\b") && line.Length < 100;
                string period = Kind(t, provider);
                string variant = Variant(t);
                if (isModel)
                {
                    flush();
                    model = Regex
                                .Split(clean, @"\s*[·|:]\s*|\s+\(?\d+[.,]?\d*%|\s+\b(?:weekly|week|5.hour)\b",
                                       RegexOptions.IgnoreCase) [0]
                                .Trim();
                    if (Fmt.Fold(model) == "gemini models")
                        model = "Gemini";
                    key = Fmt.Fold(model);
                    label = model;
                }
                if (period != null)
                {
                    flush();
                    if (provider == "antigravity" && model.Length == 0)
                    {
                        key = null;
                        continue;
                    }
                    key = period;
                    label = period == "seven_day" ? "Tuần" : "5 giờ";
                    if (provider == "antigravity")
                    {
                        key = Regex.Replace(Fmt.Fold(model), @"[^a-z0-9]+", "-").Trim('-') +
                              (period == "seven_day" ? "-weekly" : "-5h");
                        label = model + " · " + label;
                    }
                    else if (variant.Length > 0)
                    {
                        key += "_" + variant.ToLowerInvariant();
                        label += " · " + variant;
                    }
                }
                else if (provider == "claude" && key == "seven_day" && variant.Length > 0 &&
                         !Percent.IsMatch(t) && !ResetWord.IsMatch(t))
                {
                    flush();
                    key += "_" + variant.ToLowerInvariant();
                    label = "Tuần · " + variant;
                }
                if (key != null && part.Count < 24)
                    part.Add(line.Trim());
            }
        }
        flush();
        return r;
    }
    static void ReadSection(Reading r, string key, string label, List<string> lines)
    {
        string all = String.Join(" ", lines);
        string text = Fmt.Fold(all);
        var p = Percent.Match(text);
        if (!p.Success)
            return;
        double value;
        if (!Double.TryParse(p.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out value))
            return;
        string before = text.Substring(0, p.Index), after = text.Substring(p.Index + p.Length);
        var cut = ResetWord.Match(after);
        if (cut.Success)
            after = after.Substring(0, cut.Index);
        bool remaining =
            Regex.IsMatch(after, @"^\s*(?:of (?:quota|limit) )?(left|remaining|available|con lai)\b") ||
            Regex.IsMatch(before, @"\b(left|remaining|available|con lai|con)\s*[:·|]?\s*$");
        bool used =
            Regex.IsMatch(before + " " + after, @"\b(used|consumed|da dung|da (?:su )?dung|da tieu thu)\b");
        if (!remaining && !used && r.Provider == "antigravity")
            remaining = true;
        if (!remaining && !used && r.Provider == "grok" && !Regex.IsMatch(text, @"weekly (?:credit )?usage"))
            return;
        if (!remaining && !used && r.Provider == "gemini")
            return;
        string note = null;
        long reset = 0;
        foreach (string row in lines)
        {
            var folded = Fmt.Fold(row);
            var hit = ResetWord.Match(folded);
            if (!hit.Success)
                continue;
            string segment = row.Substring(Math.Min(hit.Index, row.Length)).Trim();
            note = segment.Length > 85 ? segment.Substring(0, 85) : segment;
            reset = ResetTime(Fmt.Fold(segment), r.Time);
            break;
        }
        r.Add(new Quota { Key = key, Label = label, Used = remaining ? 100 - value : value, Reset = reset,
                          ResetNote = note });
    }
    static long ResetTime(string s, long now)
    {
        if (Regex.IsMatch(s, @"\b(in|after|sau|trong)\b"))
        {
            long seconds = 0;
            foreach (Match m in Regex.Matches(s, @"(\d+)\s*(days?|d|ngay|hours?|hrs?|h|gio|minutes?|mins?|m|" +
                                                 @"phut|seconds?|secs?|s|giay)\b"))
            {
                long n;
                if (!Int64.TryParse(m.Groups[1].Value, out n) || n > 36600)
                    continue;
                string u = m.Groups[2].Value;
                seconds += n * (u.StartsWith("d") || u == "ngay"   ? 86400
                                : u.StartsWith("h") || u == "gio"  ? 3600
                                : u.StartsWith("m") || u == "phut" ? 60
                                                                   : 1);
            }
            if (seconds > 0 && seconds < 31536000)
                return now + seconds;
        }
        var iso = Regex.Match(s, @"\d{4}-\d{2}-\d{2}t\d{2}:\d{2}(?::\d{2})?(?:z|[+-]\d{2}:\d{2})");
        DateTimeOffset dt;
        if (iso.Success && DateTimeOffset.TryParse(iso.Value, CultureInfo.InvariantCulture,
                                                   DateTimeStyles.AssumeUniversal, out dt))
            return (long)(dt.UtcDateTime - Fmt.Epoch).TotalSeconds;
        // Terminal clients commonly show a local weekday/time (e.g. "Resets Mon 4:30pm").
        var zone = TimeZoneInfo.Local;
        var named = Regex.Match(s, @"\(([^)]+)\)");
        if (named.Success)
        {
            string z = named.Groups[1].Value.Trim();
            try
            {
                if (z == "utc" || z == "gmt")
                    zone = TimeZoneInfo.Utc;
                else if (z == "asia/ho_chi_minh" || z == "asia/saigon" || z == "asia/bangkok")
                    zone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");
                else
                    zone = TimeZoneInfo.FindSystemTimeZoneById(z);
            }
            catch (TimeZoneNotFoundException)
            {
                return 0;
            }
            s = s.Substring(0, named.Index);
        }
        var time = Regex.Match(s, @"(?<![\d:/])(\d{1,2})(?::(\d{2})\s*(am|pm)?|\s*(am|pm)\b)");
        if (!time.Success)
            return 0;
        int hour = Int32.Parse(time.Groups[1].Value),
            minute = time.Groups[2].Success ? Int32.Parse(time.Groups[2].Value) : 0;
        string ampm = time.Groups[3].Value + time.Groups[4].Value;
        if (minute > 59 || hour > 23 || (ampm.Length > 0 && (hour < 1 || hour > 12)))
            return 0;
        if (ampm.Length > 0)
            hour = hour % 12 + (ampm == "pm" ? 12 : 0);
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(Fmt.Epoch.AddSeconds(now), zone);
        DateTime day = local.Date;
        string before = s.Substring(0, time.Index);
        var calendar = Regex.Match(before, @"\b(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\s+" +
                                           @"\d{1,2}(?:,?\s+\d{4})?\b|\b\d{1,2}/\d{1,2}(?:/\d{4})?\b");
        bool anchored = false;
        int step = 1;
        if (calendar.Success)
        {
            string date = calendar.Value;
            bool year = Regex.IsMatch(date, @"\b\d{4}\b");
            if (!year)
                date += date.Contains("/") ? "/" + local.Year : ", " + local.Year;
            DateTime parsed;
            if (!DateTime.TryParse(date, date.Contains("/") ? Fmt.Vi : CultureInfo.InvariantCulture,
                                   DateTimeStyles.AllowWhiteSpaces, out parsed))
                return 0;
            day = parsed.Date;
            if (!year && day < local.Date.AddDays(-180))
                day = day.AddYears(1);
            anchored = true;
        }
        else
        {
            var week = Regex.Match(before, @"\b(sun|mon|tue|wed|thu|fri|sat)[a-z]*\b");
            if (week.Success)
            {
                string[] days = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
                int n = Array.IndexOf(days, week.Groups[1].Value);
                day = day.AddDays((n - (int)day.DayOfWeek + 7) % 7);
                step = 7;
            }
            if (before.Contains("tomorrow") || before.Contains("ngay mai"))
            {
                day = local.Date.AddDays(1);
                anchored = true;
            }
        }
        var target = day.AddHours(hour).AddMinutes(minute);
        if (!anchored && target <= local)
            target = target.AddDays(step);
        target = DateTime.SpecifyKind(target, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(target))
            return 0;
        return (long)(TimeZoneInfo.ConvertTimeToUtc(target, zone) - Fmt.Epoch).TotalSeconds;
    }
}
}
