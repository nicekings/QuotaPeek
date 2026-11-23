using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace NhipPC
{
internal static partial class Checks
{
    static int count;
    static void Check(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
        count++;
        Console.WriteLine("PASS  " + message);
    }
    [STAThread]
    public static int Main(string[] args)
    {
        string name = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();
        if (name == "codex")
            return TestCodexServer(args);
        if (args.Length == 2 && (args[0] == "--read-window" || args[0] == "--read-cli"))
            return Program.Main(args);
        if (new[] { "grok", "claude", "agy" }.Contains(name))
            return TestTerminal(name);
        if (args.Length==2 && args[0]=="--preview") {SavePreview(args[1]);return 0;}
        if (args.Length == 2 && args[0] == "--tray-preview") { SaveTrayPreview(args[1]); return 0; }
        string temp = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                                   "test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("QUOTAPEEK_DATA_DIR", temp);
        try
        {
            Structured();
            ClaudeAuthStatus();
            ClaudeStream();
            AntigravityReport();
            PaceAndTray();
            Panels();
            Settings(temp);
            Storage();
            Reducers();
            HubScheduling(temp);
            Hardware();
            DesktopOptions();
            NativeControls();
            CodexStdio(temp);
            Terminal(temp);
            Console.WriteLine(count + " checks passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            if (Path.GetDirectoryName(temp) ==
                    Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) &&
                Path.GetFileName(temp).StartsWith("test-"))
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (IOException)
                {
                }
        }
    }
    static void Structured()
    {
        var c =
            QuotaReaders.Codex(Json.Parse(@"{""rateLimits"":{""limitId"":""codex"",""primary"":null,""" +
                                          @"secondary"":{""usedPercent"":0,""windowDurationMins"":10080}}}"));
        Check(c.Valid("codex") && c.Windows.Count == 1 && c.Windows[0].Label == "Tuần" &&
                  c.Windows[0].Used == 0,
              "Codex weekly only, secondary slot, real zero");
        var primary = QuotaReaders.Codex(
            Json.Parse(@"{""rateLimitsByLimitId"":{""codex"":{""primary"":{""usedPercent"":5,""" +
                       @"windowDurationMins"":10080}}},""rateLimits"":{""limitId"":""codex"",""primary"":{""" +
                       @"usedPercent"":70,""windowDurationMins"":300}}}"));
        Check(primary.Windows.Count == 1 && primary.Windows[0].Label == "Tuần",
              "A compatibility view cannot reintroduce a missing 5-hour window");
        c = QuotaReaders.Codex(Json.Parse(
            @"{""rateLimitsByLimitId"":{""codex"":{""primary"":{""usedPercent"":100,""windowDurationMins"":" +
            @"300,""resetsAt"":1700000000},""secondary"":{""usedPercent"":8,""windowDurationMins"":10080},""" +
            @"credits"":{""balance"":""42""}},""spark"":{""primary"":{""usedPercent"":15,""" +
            @"windowDurationMins"":60}}},""rateLimits"":{""limitId"":""codex"",""primary"":{""usedPercent"":" +
            @"100,""windowDurationMins"":300,""resetsAt"":1700000000}}}"));
        Check(c.Windows.Count == 4 && c.Windows.Count(w => w.Label == "5 giờ") == 1 &&
                  c.Windows.Any(w => w.Label == "Tuần"),
              "Codex dual windows and multiple buckets deduplicate");
        Check(c.Windows.Single(w => w.Key == "codex/300").Value(Fmt.Now) == "Chờ cập nhật",
              "Expired period waits for provider instead of resetting to zero");
        Check(c.Windows.Single(w => w.Key == "codex/credits").Used == null &&
                  c.Windows.Single(w => w.Key == "codex/credits").Balance == "42 credit",
              "Credit balance has a unit and no invented percentage");
        var claude = QuotaReaders.StatusLine(
            "claude",
            Json.Parse(@"{""rate_limits"":{""five_hour"":null,""seven_day"":{""used_percentage"":67},""seven_" +
                       @"day_sonnet"":{""used_percentage"":0}},""context_window"":{""used_percentage"":99}}"));
        Check(claude.Windows.Count == 2 && claude.Windows[0].Label == "Tuần" &&
                  claude.Windows[1].Label.Contains("sonnet"),
              "Claude weekly-only plan preserves model sublimit");
        claude = QuotaReaders.StatusLine("claude",
                                         Json.Parse(@"{""rate_limits"":{""five_hour"":{""used_percentage"":0}" +
                                                    @",""seven_day"":{""used_percentage"":100}}}"));
        Check(claude.Windows.Count == 2 && claude.Windows[0].Used == 0 && claude.Windows[1].Used == 100,
              "Claude both real periods include 0% and 100%");
        Check(QuotaReaders.StatusLine("claude", Json.Parse(@"{""context_window"":{""used_percentage"":79}}"))
                      .Windows.Count == 0,
              "Context tokens never become subscription usage");
        var ag = QuotaReaders.StatusLine(
            "antigravity",
            Json.Parse(@"{""quota"":{""gemini-weekly"":{""remaining_fraction"":0.25,""reset_time"":""2030-01-" +
                       @"01T00:00:00Z""},""gemini-flash-5h"":{""remaining_fraction"":1},""claude-weekly"":{""" +
                       @"remaining_fraction"":0.2},""gemini-hidden"":{""remaining_fraction"":0.4,""disabled""" +
                       @":true},""gemini-tokens"":{""remaining_amount"":123}}}"));
        Check(ag.Windows.Count == 4 && ag.Windows.Single(w => w.Key == "claude-weekly").Used == 80 &&
                  ag.Windows.Single(w => w.Key == "gemini-weekly").Used == 75 &&
                  ag.Windows.Single(w => w.Key == "gemini-weekly").Reset == 1893456000,
              "Antigravity keeps every model group, converts remaining fraction and retains reset");
        Check(ag.Windows.Single(w => w.Key == "gemini-flash-5h").Used == 0 &&
                  ag.Windows.Single(w => w.Key == "gemini-tokens").Used == null,
              "Antigravity keeps models and quantities distinct");
    }
    static void Panels()
    {
        var week = UsagePanel.Parse("claude", "Desktop",
                                    new[] { "Weekly limits", "All models", "67% used", "Resets in 5 hours" });
        Check(week.Windows.Count == 1 && week.Windows[0].Label == "Tuần" &&
                  week.Windows[0].Reset - week.Time == 18000,
              "Five-hour countdown is not a five-hour quota");
        var both = UsagePanel.Parse(
            "claude", "Desktop",
            new[] { "Current session", "0% used", "Resets in 2h 7m", "Weekly limits", "100% used" });
        Check(both.Windows.Count == 2 && both.Windows[0].Reset - both.Time == 7620,
              "Desktop panel associates each reset with its own window");
        var ge = UsagePanel.Parse(
            "gemini", "Desktop",
            new[] { "Hạn mức 5 giờ", "Còn lại 75%", "Đặt lại sau 20 phút", "Hạn mức tuần", "Đã dùng 14,5%" });
        Check(ge.Windows.Count == 2 && ge.Windows[0].Used == 25 && ge.Windows[1].Used == 14.5,
              "Vietnamese Gemini labels, comma decimal, remaining direction");
        Check(UsagePanel.Parse("gemini", "Desktop", new[] { "Weekly limits", "53%" }).Windows.Count == 0,
              "Ambiguous Gemini percentages stay unavailable");
        var grok = UsagePanel.Parse(
            "grok", "/usage", new[] { "Weekly usage", "65% left", "Resets in 2 days", "Context window 96%" });
        Check(grok.Windows.Count == 1 && grok.Windows[0].Used == 35, "Grok weekly remaining is converted once");
        Check(UsagePanel.Parse("grok", "/usage", new[] { "Context window", "72% used" }).Windows.Count == 0,
              "Ignore context-only terminal");
        var ai = UsagePanel.Parse("antigravity", "Desktop",
                                  new[] { "Gemini Flash", "74% remaining", "Gemini Pro", "30% remaining" });
        Check(ai.Windows.Count == 2 && ai.Windows[0].Used == 26 && ai.Windows[1].Used == 70,
              "Model groups remain distinct in desktop panel");
        var stamp = UsagePanel.Parse(
            "claude", "Desktop",
            new[] { "Current session", "12% used", "Resets Jan 1, 2030 at 7:00am (Asia/Saigon)" });
        Check(stamp.Windows[0].Reset == 1893456000, "Local calendar reset keeps the source timezone");
    }
    static void Settings(string temp)
    {
        string input = @"{ ""something"" : {""text"":""hello, }""}, ""statusLine"" : {""command"":""old""}, " +
                       @"""other"":42 }";
        string changed = StatusLine.Patch(input, @"{""command"":""new""}");
        Check(changed == input.Replace("old", "new"), "Editing CLI config preserves surrounding content");
        foreach (string item in new[] { "{}", @"{""a"":1}", @"{""statusLine"":{},""a"":1}",
                                        @"{""a"":1,""statusLine"":{}}" })
        {
            var doc = Json.Parse(StatusLine.Patch(StatusLine.Patch(item, "{}"), null));
            Check(!doc.ContainsKey("statusLine") && (!item.Contains("\"a\"") || Json.Number(doc, "a") == 1),
                  "Clean hook removal from " + item);
        }
        string config = Path.Combine(temp, "client");
        Directory.CreateDirectory(config);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", config);
        string path = Path.Combine(config, "settings.json");
        File.WriteAllText(path, input);
        StatusLine.Toggle("claude");
        Check(StatusLine.Installed("claude") && File.ReadAllText(path).Contains("--tap"),
              "Existing status line receives the original input");
        StatusLine.Toggle("claude");
        Check(File.ReadAllText(path) == input, "Disconnect restores original custom status line");
        Store.Write(Store.PathFor("preferences"), @"{""TrayRingSize"":2,""LargeTrayRing"":false,""OffsetX"":19}");
        var migrated = Store.Load();
        Check(migrated.TrayRingSize == RingSize.Large && migrated.LargeTrayRing && migrated.OffsetX == 19,
              "Previously saved extra-large rings migrate to large with other settings intact");
        var p = new Preferences { Widget = true, Corner = "free", OffsetX = 18, OffsetY = 72,
                                  TrayKey = "ai/codex/codex/10080" };
        p.Enabled["ram"] = false;
        Store.Save(p);
        var again = Store.Load();
        Check(again.Widget && !again.On("ram") && again.OffsetX == 18 && again.TrayKey == p.TrayKey,
              "Pinned period and widget placement persist");
    }
    static void Storage()
    {
        var r = Reading.Start("claude", "Claude Code");
        r.AccountKey = AccountScope.Key("claude", "email", "reader@example.test");
        r.Plan = "pro";
        r.Add(new Quota { Key = "seven_day", Label = "Tuần", Used = 22 });
        Store.SaveReading(r);
        r.Time -= 10;
        r.Windows[0].Used = 99;
        Store.SaveReading(r);
        Check(AccountScope.From(r).Last().Windows[0].Used == 22, "An older reading cannot overwrite a newer value");
        Store.Write(Store.PathFor("last-claude"), "{}");
        Store.Write(Store.PathFor("event-claude-hint"), "{}");
        Store.RemoveLegacy();
        Check(!File.Exists(Store.PathFor("last-claude")) && !File.Exists(Store.PathFor("event-claude-hint")) &&
              AccountScope.From(r).Last() != null, "Upgrading removes 1.x files that had no account and keeps scoped ones");
    }
    static void Hardware()
    {
        var p = new Preferences();
        p.Enabled["io"] = true;
        using (var sampler = new HardwareSampler())
        {
            var a = sampler.Sample(p);
            Thread.Sleep(1100);
            var b = sampler.Sample(p);
            Check(!a.Cpu.HasValue && b.Cpu >= 0 && b.Cpu <= 100, "Real CPU delta after warm-up");
            Check(b.TotalMemory > 0 && b.Ram > 0 && b.Ram <= 100 && b.UsedMemory <= b.TotalMemory,
                  "Real memory sample");
            Check(b.Volumes.Any(v => v.Total > 0 && v.Used >= 0 && v.Used <= 100), "Real fixed-drive capacity");
            Check(b.Read >= 0 && b.Write >= 0, "Physical disk read/write counters");
            if (b.Adapter != null)
                Check(b.Receive >= 0 && b.Send >= 0, "Real network counters on active adapter");
            p.Enabled["ram"] = false;
            Check(!sampler.Sample(p).Ram.HasValue, "Disabled RAM collection stops");
        }
    }
    static int TestTerminal(string name)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string args = String.Join(" ", Environment.GetCommandLineArgs().Skip(1));
        string scenario = Environment.GetEnvironmentVariable("QUOTAPEEK_TEST_AUTH");
        if (name == "claude" && args == "auth status")
        {
            string email = "one@example.org";
            if (scenario == "switch" && File.Exists(Store.PathFor("test-switched")))
                email = "two@example.org";
            if (scenario == "signed-out")
            {
                Console.Write(Json.Encode(new { loggedIn = false }));
                return 1;
            }
            Console.Write(Json.Encode(new { loggedIn = true, authMethod = "claude.ai", email,
                                            subscriptionType = "pro", orgId = "work-1" }));
            return 0;
        }
        if (name == "grok" && args == "agent stdio")
        {
            if (Json.Str(Json.Parse(Console.ReadLine()), "method") != "initialize")
                return 1;
            Console.WriteLine(Json.Encode(new { jsonrpc = "2.0", id = 1, result = new {
                authMethods = new[] { new { id = scenario == "signed-out" ? "browser_login" : "cached_token" } }
            } }));
            Console.Out.Flush();
            if (scenario == "signed-out")
                return 0;
            var request = Json.Parse(Console.ReadLine());
            var p = Json.Obj(request, "params");
            if (Json.Str(request, "method") != "authenticate" || Json.Str(p, "methodId") != "cached_token" ||
                !Object.Equals(Json.Get(Json.Obj(p, "_meta"), "headless"), true))
                return 2;
            Console.WriteLine(Json.Encode(new { jsonrpc = "2.0", id = 2, result = new { } }));
            Console.Out.Flush();
            return 0;
        }
        if (name == "agy")
        {
            if (args == "--version")
            {
                Console.Write("Antigravity CLI v1.4.5");
                return 0;
            }
            if (args != "--disable-slash-commands=false -p /usage --output-format json")
                return 2;
            var buckets = new[] {
                new { id = "gemini-weekly", remaining_fraction = 1.0 },
                new { id = "gemini-5h", remaining_fraction = 0.855 },
                new { id = "3p-weekly", remaining_fraction = 0.4 } };
            Console.Write(Json.Encode(new { type = "result", result = new { num_turns = 0,
                command = new { name = "usage", data = new {
                    email = scenario == "unverified" ? null : "agy@example.org",
                    plan_tier = "Google AI Pro", groups = new[] { new { buckets = (object)buckets } }
                } } } }));
            return 0;
        }
        if (name == "claude")
            return ClaudeStreamFixture(args, scenario);
        Console.Write(name + "\r\n" + (scenario == "placeholder" ? "❯ Try \"choose a theme\"" : "❯ "));
        string cmd = Console.ReadLine();
        if (cmd != "/usage")
            return 2;
        Console.Write("\u001b[2J\u001b[H");
        if (name == "grok")
            Console.Write("Account usage\r\nEmail: grok@example.org\r\nPlan: SuperGrok\r\n" +
                          "Weekly usage\r\n65% left\r\nResets in 2 days\r\n");
        Console.Out.Flush();
        Thread.Sleep(16000);
        return 0;
    }
    static void Terminal(string temp)
    {
        var s = new TerminalScreen(30, 6);
        var text = "\u001b[2J\u001b[Hweekly\u001b[2;1H70% left\u001b[2;1H\u001b[2K60% left".ToCharArray();
        s.Feed(text, text.Length);
        Check(s.Text().Contains("60% left") && !s.Text().Contains("70%"),
              "Terminal redraw replaces old percentages");
        foreach (string client in new[] { "grok", "claude", "agy" })
            File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(temp, client + ".exe"));
        Environment.SetEnvironmentVariable("PATH", temp + ";" + Environment.GetEnvironmentVariable("PATH"));
        var r = CliUsage.Read("grok");
        Check(r.Windows.Count == 1 && r.Windows[0].Used == 35 && r.Plan == "SuperGrok" &&
              r.AccountKey == AccountScope.Key("grok", "email", "grok@example.org"),
              "Grok checks existing headless login and binds /usage to its panel's account");
        r = CliUsage.Read("claude");
        Check(r.Windows.Count == 2 && r.Windows.Single(w => w.Key == "seven_day").Used == 11 &&
              r.Windows.Single(w => w.Key == "five_hour").Reset == 1893456000 &&
              r.AccountKey == AccountScope.Key("claude", "email", "one@example.org", "work-1") && r.Plan == "pro" &&
              ReadResult.From("claude", r, Fmt.Now).Phase == Phase.Live,
              "Claude print-mode /usage carries both periods, UTC resets and the verified account");
        r = CliUsage.Read("antigravity");
        Check(r.Windows.Count == 3 && r.Windows.Single(w => w.Key == "3p-weekly").Label == "Claude & GPT · Tuần" && r.Windows.Single(w => w.Key == "gemini-weekly").Used == 0 &&
                  Math.Abs(r.Windows.Single(w => w.Key == "gemini-5h").Used.Value - 14.5) < 0.001 &&
                  r.AccountKey == AccountScope.Key("antigravity", "email", "agy@example.org"),
              "Antigravity read-only JSON carries model group quotas and account in a single reply");
        try
        {
            Environment.SetEnvironmentVariable("QUOTAPEEK_TEST_AUTH", "switch");
            Check(CliUsage.Read("claude").Windows.Count == 0, "A switch during /usage discards the sampled windows");
            Environment.SetEnvironmentVariable("QUOTAPEEK_TEST_AUTH", "signed-out");
            File.Delete(Store.PathFor("test-usage-ran"));
            var signedOut = CliUsage.Read("claude");
            Check(signedOut.NeedsLogin && CliUsage.Read("grok").NeedsLogin &&
                  ReadResult.From("claude", signedOut, Fmt.Now).Phase == Phase.NeedsLogin &&
                  !File.Exists(Store.PathFor("test-usage-ran")),
                  "A signed-out CLI stops before /usage and asks for login");
            Environment.SetEnvironmentVariable("QUOTAPEEK_TEST_AUTH", "model-turn");
            var spent = CliUsage.Read("claude");
            Check(spent.NeedsSetup && spent.Windows.Count == 0 &&
                  ReadResult.From("claude", spent, Fmt.Now).Phase == Phase.NeedsSetup,
                  "If /usage ever reaches the model, automatic Claude reads stop");
            Environment.SetEnvironmentVariable("QUOTAPEEK_TEST_AUTH", "limited");
            var waiting = CliUsage.Read("claude");
            Check(ReadResult.From("claude", waiting, Fmt.Now).Phase == Phase.Cooldown,
                  "A rate-limited /usage becomes a cooldown");
            Environment.SetEnvironmentVariable("QUOTAPEEK_TEST_AUTH", "unverified");
            Check(CliUsage.Read("antigravity").AccountKey == null,
                  "A quota-only Antigravity reply cannot identify an account");
            var limited = Reading.Start("claude", "CLI");
            Check(CliIdentity.Failure(limited, "Too many requests\nRetry-After: 123") &&
                  limited.RateLimited && limited.RetryAfterSeconds == 123, "CLI preserves server cooldown");
        }
        finally { Environment.SetEnvironmentVariable("QUOTAPEEK_TEST_AUTH", null); }
    }
}
}
