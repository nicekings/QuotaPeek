using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NhipPC
{
internal static partial class Checks
{
    // Shape captured from Claude Code 2.1.330 `claude -p /usage --output-format stream-json --verbose`.
    const string UsageReport =
        @"{""type"":""assistant"",""message"":{""model"":""<synthetic>"",""content"":[{""type"":""text"",""text"":""Current session: 37% used""}]}," +
        @"""usage_report"":{""session"":{""total_cost_usd"":0},""rate_limits"":{""limits"":[" +
        @"{""kind"":""session"",""group"":""session"",""percent"":37,""resets_at"":""2030-01-01T00:00:00.504964+00:00"",""scope"":null}," +
        @"{""kind"":""weekly_all"",""group"":""weekly"",""percent"":44,""resets_at"":""2030-01-05T00:00:00+00:00"",""scope"":null}," +
        @"{""kind"":""weekly_scoped"",""group"":""weekly"",""percent"":56,""resets_at"":""2030-01-05T00:00:00+00:00""," +
        @"""scope"":{""model"":{""display_name"":""Fable""},""surface"":null}}],""extra_usage"":{""is_enabled"":false}}}}";
    const string LocalResult =
        @"{""type"":""result"",""subtype"":""success"",""num_turns"":0,""total_cost_usd"":0,""local_command"":""usage"",""result"":""Current session: 37% used""}";

    static void ClaudeStream()
    {
        var r = Reading.Start("claude", ClaudeUsage.Source);
        ClaudeUsage.Parse(r, "{\"type\":\"system\",\"subtype\":\"init\"}\n" + UsageReport + "\n" + LocalResult + "\n");
        Check(r.Windows.Select(w => w.Key).SequenceEqual(new[] { "five_hour", "seven_day", "seven_day_fable" }) &&
              r.Windows[0].Label == "5 giờ" && r.Windows[1].Label == "Tuần" && r.Windows[2].Label == "Tuần · Fable" &&
              r.Windows[0].Used == 37 && r.Windows[0].Reset == 1893456000 && !r.NeedsSetup,
              "Claude usage_report maps session, weekly and the Fable weekly limit with UTC resets");
        var spent = Reading.Start("claude", ClaudeUsage.Source);
        ClaudeUsage.Parse(spent, UsageReport + "\n" +
            @"{""type"":""result"",""num_turns"":1,""total_cost_usd"":0.14,""result"":""It looks like you typed /usage""}");
        Check(spent.NeedsSetup && spent.Windows.Count == 0,
              "A /usage that reached the model is never accepted, even with a usage report");
        var empty = Reading.Start("claude", ClaudeUsage.Source);
        ClaudeUsage.Parse(empty, @"{""usage_report"":{""rate_limits"":null}}" + "\n" + LocalResult);
        Check(empty.Windows.Count == 0 && !empty.NeedsSetup && empty.Notice != null,
              "A report without rate limits waits instead of inventing numbers");
        var limited = Reading.Start("claude", ClaudeUsage.Source);
        ClaudeUsage.Parse(limited, @"{""type"":""result"",""num_turns"":0,""local_command"":""usage"",""result"":""Too many requests. Retry-After: 120""}");
        Check(limited.RateLimited && limited.RetryAfterSeconds == 120, "A rate-limited /usage keeps the server's wait");
    }

    static void RingSpacing()
    {
        int nativeSize = Math.Max(16, System.Windows.Forms.SystemInformation.SmallIconSize.Height);
        RingSpacingAtSize(16);
        if (nativeSize != 16) RingSpacingAtSize(nativeSize);
    }

    static void RingSpacingAtSize(int pixels)
    {
        var center = new PinChoice { Number = "" };
        var periods = new List<PinChoice> { center, new PinChoice { Percent = 100 } };
        int previousWidth = 0;
        foreach (RingSize size in Enum.GetValues(typeof(RingSize)))
        {
            center.Number = "";
            using (var ringIcon = TrayIndicators.MakeRingAtSize(periods, null, size, pixels))
            using (var ring = ringIcon.ToBitmap())
            {
                // The empty center isolates the painted arc, independent of the taskbar theme.
                int left = Enumerable.Range(0, ring.Width / 2).First(x => ring.GetPixel(x, ring.Height / 2).A >= 128);
                int right = Enumerable.Range(ring.Width / 2, ring.Width - ring.Width / 2).Last(x => ring.GetPixel(x, ring.Height / 2).A >= 128);
                int width = right - left + 1;
                Check(width > previousWidth && (size != RingSize.Large || width == ring.Width),
                      size + " has a visibly wider ring at tray resolution");
                previousWidth = width;
                foreach (string number in new[] { "0", "8", "38", "100", "\u2014" })
                {
                    center.Number = number;
                    bool hasInk = false, clear = true;
                    int top = ring.Height, bottom = -1;
                    using (var filledIcon = TrayIndicators.MakeRingAtSize(periods, null, size, pixels))
                    using (var filled = filledIcon.ToBitmap())
                        for (int y = 1; y < ring.Height - 1; y++)
                            for (int x = 1; x < ring.Width - 1; x++)
                            {
                                if (filled.GetPixel(x, y).A - ring.GetPixel(x, y).A < 100) continue;
                                hasInk = true;
                                top = Math.Min(top, y);
                                bottom = Math.Max(bottom, y);
                                clear &= ring.GetPixel(x, y).A < 32 && ring.GetPixel(x + 1, y).A < 180 &&
                                         ring.GetPixel(x - 1, y).A < 180 && ring.GetPixel(x, y + 1).A < 180 &&
                                         ring.GetPixel(x, y - 1).A < 180;
                            }
                    Check(hasInk && clear, size + " keeps " + number + " visible and clear of the full arc");
                    if (size == RingSize.Medium && ring.Width == 16 && (number == "38" || number == "100"))
                        Check(bottom - top + 1 >= (number == "38" ? 7 : 5),
                              "Medium preserves digit height for " + number + " at native tray size");
                }
            }
        }
    }

    static void PaceAndTray()
    {
        RingSpacing();
        long now = 2000000000, week = 7 * 86400;
        // Two days into the week (29% elapsed).
        var slow = new Quota { Key = "seven_day", Label = "Tuần", Used = 20, Reset = now + week - 2 * 86400 };
        var fast = new Quota { Key = "seven_day", Label = "Tuần", Used = 60, Reset = now + week - 2 * 86400 };
        Check(DisplayData.Pace("ai/claude/seven_day", slow, now) == "Đủ đến kỳ đặt lại",
              "Pace: using less than an even share lasts until the reset");
        string runout = DisplayData.Pace("ai/claude/seven_day", fast, now);
        Check(runout != null && runout.StartsWith("Dùng nhanh · hết lúc ") &&
              runout.EndsWith(Fmt.Local(now + 2 * 86400 * 40 / 60, "dd/MM HH:mm")),
              "Pace: 60% used after two days runs out after about 1.3 more days");
        Check(DisplayData.Pace("ai/claude/seven_day", new Quota { Used = 50, Reset = now + week - 600 }, now) == null &&
              DisplayData.Pace("ai/codex/codex/credits", fast, now) == null,
              "Pace stays silent early in a period and for credits");
        Check(DisplayData.Duration("ai/codex/codex/300") == 18000 && DisplayData.Duration("ai/claude/five_hour") == 18000 &&
              DisplayData.Duration("ai/antigravity/gemini-weekly") == week, "Period lengths come from each source's keys");
        Check(DisplayData.ResetText(now + 3 * 3600 + 24 * 60, now, false) == "Đặt lại sau 3 giờ 24 phút" &&
              DisplayData.ResetText(now + 86400 + 7200, now, false) == "Đặt lại sau 1 ngày 2 giờ" &&
              DisplayData.ResetText(now + 60, now, true).StartsWith("Đặt lại ") &&
              !DisplayData.ResetText(now + 60, now, true).Contains("sau"),
              "Reset time reads as a countdown or as a clock time");
        Check(TrayIndicators.Mark("ai/claude/five_hour") == TrayIndicators.Stripe.Short &&
              TrayIndicators.Mark("ai/claude/seven_day") == TrayIndicators.Stripe.Full &&
              TrayIndicators.Mark("ai/claude/seven_day_fable") == TrayIndicators.Stripe.Dashed &&
              TrayIndicators.Mark("ai/codex/codex/300") == TrayIndicators.Stripe.Short &&
              TrayIndicators.Mark("ai/codex/codex/credits") == TrayIndicators.Stripe.Dot,
              "Stripe shape tells 5-hour, weekly, per-model and credit numbers apart");
        var choices = new List<PinChoice> {
            new PinChoice { Key = "ram", Name = "RAM", Number = "35" },
            new PinChoice { Key = "ai/claude/seven_day", Name = "Claude · Tuần (còn lại)", Label = "Tuần", Value = "Còn lại 78%", Number = "78", Percent = 78 },
            new PinChoice { Key = "ai/claude/five_hour", Name = "Claude · 5 giờ (còn lại)", Label = "5 giờ", Value = "Còn lại 12%", Number = "12", Percent = 12, Level = 1 },
            new PinChoice { Key = "ai/codex/codex/10080", Name = "Codex · Tuần (còn lại)", Label = "Tuần", Value = "Còn lại 37%", Number = "37", Percent = 37 },
            new PinChoice { Key = "ai/codex/codex/credits", Name = "Codex · Số dư credit", Label = "Số dư credit", Value = "0 credit", Number = "0" } };
        var p = new Preferences { TrayItems = choices.Select(c => c.Key).ToList(), TrayStyle = "bars" };
        var icons = DisplayData.TrayIcons(p, choices);
        Check(icons.Select(i => i.Key).SequenceEqual(new[] { "ram", "ai/claude/bars", "ai/codex/bars", "ai/codex/codex/credits" }) &&
              icons[1].Bars.Select(b => b.Label).SequenceEqual(new[] { "5 giờ", "Tuần" }) && icons[1].Level == 1 &&
              icons[1].Tip == "Claude · 5 giờ còn 12% · Tuần còn 78%",
              "2-bar style draws one icon per service with the 5-hour bar first");
        p.TrayStyle = "ring";
        var extra = new PinChoice { Key = "ai/claude/seven_day_fable", Name = "Claude · Fable", Label = "Fable",
                                    Value = "Còn lại 14%", Number = "14", Percent = 14, Level = 1 };
        choices.Add(extra);
        p.TrayItems.Add(extra.Key);
        var rings = DisplayData.TrayIcons(p, choices);
        Check(rings.Count == 4 && rings[1].Ring && rings[1].Bars.First().Number == "12" &&
              rings[1].Bars.Last() == extra && rings[2].Bars.Count == 1 && !rings[3].Ring,
              "Ring pairs the selected short and most limited weekly quota, keeping credit separate");
        Check(rings[1].Tip.Contains("Tuần chung 78%") && rings[1].Tip.Contains("Vòng ngoài · Fable 14%") &&
              rings[1].Tip.Contains("Số giữa · 5 giờ 12%") && ("QuotaPeek · " + rings[1].Tip).Length <= 127,
              "A pinned general week stays visible in the tooltip when a model controls the ring");
        using (var icon = TrayIndicators.MakeRing(rings[1].Bars, TrayIndicators.Accent("claude")))
            Check(icon.Width >= 16, "The combined quota ring renders as a native notification icon");
        p.LargeTrayRing = false;
        var mediumRing = DisplayData.TrayIcons(p, choices)[1];
        Check(rings[1].RingSize == RingSize.Large && mediumRing.RingSize == RingSize.Medium && rings[1].Key == mediumRing.Key,
              "Changing ring size preserves the registered notification icon identity");
        p.TrayRingSize = RingSize.Large;
        var fields = p.RingFieldsByProvider["claude"] = new RingFields {
            Center = extra.Key, Outer = "ai/claude/seven_day"
        };
        var custom = DisplayData.TrayIcons(p, choices)[1];
        Check(custom.RingSize == RingSize.Large && custom.Bars.First() == extra &&
              custom.Bars.Last().Number == "78" && custom.Tip.Contains("Số giữa · Fable 14%"),
              "A large ring uses the independent center and outer assignments");
        fields.Center = "ai/claude/five_hour";
        custom = DisplayData.TrayIcons(p, choices)[1];
        Check(custom.Tip.Contains("Vòng ngoài · Tuần chung 78%") && custom.Tip.Contains("Fable 14%"),
              "The general week can drive the ring while Fable stays pinned in the tooltip");
        fields.Center = fields.Outer;
        Check(DisplayData.TrayIcons(p, choices)[1].Bars.Count == 1, "Both roles may explicitly use the same period");
        fields.Center = extra.Key;
        p.TrayItems.Remove(extra.Key);
        Check(DisplayData.TrayIcons(p, choices)[1].Bars.First().Number == "12" && fields.Center == extra.Key,
              "An unavailable assignment uses the automatic period without losing the saved choice");
        p.TrayItems.Add(extra.Key);
        Check(DisplayData.TrayIcons(p, choices)[1].Bars.First() == extra &&
              DisplayData.TrayIcons(p, choices)[2].Bars.Single().Parent == "codex",
              "Repinning restores the assignment and other providers keep their own choices");
        choices.Remove(extra);
        p.TrayItems.Remove(extra.Key);
        p.TrayStyle = "numbers";
        Check(DisplayData.TrayIcons(p, choices).Count == 5, "Number style keeps one icon per chosen number");
        using (var icon = TrayIndicators.MakeBars(icons[1].Bars, false, TrayIndicators.Accent("claude")))
            Check(icon.Width >= 16, "The 2-bar icon renders");
    }

    static void AntigravityReport()
    {
        // The documented /usage panel prints a text report ("Models & Quota") with every model group.
        const string report = "└ Models & Quota\n  Account: agy@example.org\n" +
            "GEMINI MODELS\n  Models within this group: Gemini Flash, Gemini Pro\n" +
            "  Weekly Limit Remaining\n    [##########] 100.00%\n    Quota available\n" +
            "  Five Hour Limit Remaining\n    [######    ] 62.50%\n" +
            "CLAUDE AND GPT MODELS\n  Models within this group: Claude Opus, Claude Sonnet, GPT-OSS\n" +
            "  Weekly Limit Remaining\n    [##        ] 20.00%\n";
        var r = QuotaReaders.AntigravityText(report, "Antigravity CLI");
        Check(r.Windows.Select(w => w.Label).SequenceEqual(new[] { "Gemini · Tuần", "Gemini · 5 giờ", "Claude & GPT · Tuần" }) &&
              r.Windows[0].Used == 0 && r.Windows[1].Used == 37.5 && r.Windows[2].Used == 80 &&
              r.AccountKey == AccountScope.Key("antigravity", "email", "agy@example.org") &&
              ReadResult.From("antigravity", r, Fmt.Now).Phase == Phase.Live,
              "The Antigravity text report yields Gemini and Claude & GPT limits for the signed-in account");
        Check(QuotaReaders.AntigravityText(report.Replace("  Account: agy@example.org\n", ""), "Antigravity CLI").AccountKey == null,
              "An Antigravity report without an account is never attributed");
    }

    // Fake `claude -p /usage ...` for the Terminal checks.
    static int ClaudeStreamFixture(string args, string scenario)
    {
        if (args != ClaudeUsage.Arguments)
            return 2;
        if (Environment.GetEnvironmentVariable("CLAUDECODE") != null)
            return 3;
        File.WriteAllText(Store.PathFor("test-usage-ran"), "{}");
        if (scenario == "switch")
            File.WriteAllText(Store.PathFor("test-switched"), "{}");
        if (scenario == "model-turn")
        {
            Console.WriteLine(@"{""type"":""result"",""num_turns"":1,""result"":""It looks like you typed /usage""}");
            return 0;
        }
        if (scenario == "limited")
        {
            Console.WriteLine(@"{""type"":""result"",""num_turns"":0,""local_command"":""usage"",""result"":""Too many requests""}");
            return 0;
        }
        Console.WriteLine(@"{""type"":""system"",""subtype"":""init""}");
        Console.WriteLine(@"{""type"":""assistant"",""usage_report"":{""rate_limits"":{""limits"":[" +
            @"{""kind"":""session"",""percent"":0,""resets_at"":""2030-01-01T00:00:00+00:00""}," +
            @"{""kind"":""weekly_all"",""percent"":11,""resets_at"":null}]}}}");
        Console.WriteLine(LocalResult);
        return 0;
    }

    static Reading Sample(string email, string plan, double used, long time)
    {
        var r = Reading.Start("claude", ClaudeUsage.Source);
        r.Time = time;
        r.AccountKey = AccountScope.Key("claude", "email", email);
        r.Plan = plan;
        r.Add(new Quota { Key = "seven_day", Label = "Tuần", Used = used });
        return r;
    }

    static void Reducers()
    {
        const int interval = 120;
        long now = 2000000000;
        var start = Reducer.Start(null, null, now + 600, now);
        Check(start.Phase == Phase.Cooldown && start.NextRead == now + 600,
              "A saved service cooldown survives a restart");
        var s = Reducer.Start(null, null, 0, now);
        Check(s.Phase == Phase.Connecting && s.NextRead == now && !s.ShowsNumbers, "A new source reads immediately");
        var a = Sample("a@example.test", "max", 40, now);
        s = Reducer.Complete(Reducer.Begin(s, now), ReadResult.Ok(a), now, interval);
        Check(s.Phase == Phase.Live && s.Fresh && !s.Busy && s.NextRead == now + interval && s.Snapshot == a &&
              s.Account.Matches(a), "A verified reading goes live and schedules the next read");
        var older = Sample("a@example.test", "max", 90, now - 50);
        Check(Reducer.Complete(s, ReadResult.Ok(older), now, interval).Snapshot == a,
              "An older reading of the same account never replaces a newer one");
        var failed = Reducer.Complete(s, ReadResult.Fail(Phase.Error, "x"), now + 10, interval);
        Check(failed.Phase == Phase.Stale && failed.ShowsNumbers && !failed.Fresh && failed.Failures == 1 &&
              failed.NextRead == now + 10 + interval, "A failed read keeps recent numbers marked as stale");
        var again = failed;
        for (int i = 0; i < 6; i++)
            again = Reducer.Complete(again, ReadResult.Fail(Phase.Error, "x"), now + 20, interval);
        Check(again.NextRead - (now + 20) == Reducer.MaxBackoffSeconds, "Repeated failures back off up to 15 minutes");
        var old = Reducer.Complete(s, ReadResult.Fail(Phase.Error, "x"), now + Reducer.KeepNumbersSeconds + 1, interval);
        Check(old.Phase == Phase.Error && !old.ShowsNumbers,
              "Numbers older than 10 minutes are hidden once reads fail");
        var b = Sample("b@example.test", "pro", 5, now + 30);
        var switched = Reducer.Complete(s, ReadResult.Ok(b), now + 30, interval);
        Check(switched.Snapshot == b && switched.Account.Matches(b) && !switched.Account.Matches(a),
              "Another verified account replaces the numbers instead of mixing plans");
        var login = Reducer.Complete(s, ReadResult.Fail(Phase.NeedsLogin, "login"), now, interval);
        Check(login.Phase == Phase.NeedsLogin && login.Snapshot == null && login.Account == null &&
              login.NextRead == now + 300, "Signing out drops numbers and checks again in five minutes");
        var setup = Reducer.Complete(s, ReadResult.Fail(Phase.NeedsSetup, "setup"), now, interval);
        Check(setup.NextRead == Int64.MaxValue && setup.ShowsNumbers && Reducer.Retry(setup, now + 5).NextRead == now + 5,
              "A paused source waits for the user, and Đọc lại resumes it");
        var cool = Reducer.Complete(s, ReadResult.Fail(Phase.Cooldown, "wait", now + 900), now, interval);
        Check(cool.Phase == Phase.Cooldown && cool.RetryAt == now + 900 && Reducer.Retry(cool, now).NextRead == now + 900,
              "Đọc lại never skips a cooldown requested by the service");
        Check(Reducer.Age(s, now + 2 * interval + 16, interval).Phase == Phase.Stale &&
              Reducer.Age(s, now + interval, interval).Phase == Phase.Live,
              "Live numbers turn stale after two missed reads");
        Check(Reducer.Complete(Reducer.Stop(), ReadResult.Ok(a), now, interval).Phase == Phase.Off,
              "A late result cannot re-enable a source that was turned off");
    }

    static void HubScheduling(string temp)
    {
        var p = new Preferences();
        foreach (string id in Catalog.Providers)
            p.Enabled[id] = id == "claude";
        long now = Fmt.Now;
        var a = Sample("hub@example.test", "max", 30, now);
        using (var hub = new UsageHub(p, action => action()))
        {
            Check(hub["claude"].Phase == Phase.Connecting && hub["codex"].Phase == Phase.Off,
                  "Only enabled services start");
            hub.Complete("claude", ReadResult.Ok(a));
            Check(hub["claude"].Fresh && AccountScope.Load("claude").Matches(a) &&
                  AccountScope.From(a).Last().Windows[0].Used == 30,
                  "A live reading is saved for its own account");
            hub.CheckBells(now);
            long planned = hub["claude"].NextRead;
            Store.Write(Store.PathFor("bell-claude"), "{}");
            File.SetLastWriteTimeUtc(Store.PathFor("bell-claude"), DateTime.UtcNow.AddSeconds(5));
            hub.CheckBells(now);
            Check(planned > now + 60 && hub["claude"].NextRead <= now,
                  "A status line event from an open Claude Code session pulls the next read forward");
        }
        using (var hub = new UsageHub(p, action => action()))
        {
            var s = hub["claude"];
            Check(s.Phase == Phase.Connecting && s.Snapshot != null && s.Snapshot.Windows[0].Used == 30 && !s.Fresh,
                  "After a restart the last numbers return marked as unconfirmed");
            hub.Complete("claude", ReadResult.Fail(Phase.Cooldown, "wait", Fmt.Now + 400));
        }
        using (var hub = new UsageHub(p, action => action()))
            Check(hub["claude"].Phase == Phase.Cooldown && hub["claude"].NextRead >= Fmt.Now + 390,
                  "A service cooldown is persisted across restarts");
        File.Delete(Store.PathFor("cooldown-claude"));
    }
}
}
