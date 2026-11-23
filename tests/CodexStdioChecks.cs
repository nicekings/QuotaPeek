using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NhipPC
{
internal static partial class Checks
{
    const string FixtureExit = "QUOTAPEEK_TEST_CODEX_EXIT_EVENT";
    const string FixtureSession = "QUOTAPEEK_TEST_CODEX_SESSION";

    static int TestCodexServer(string[] args)
    {
        string session = Environment.GetEnvironmentVariable(FixtureSession);
        if (args.SequenceEqual(new[] { "login" }) && session != null &&
            File.Exists(Path.Combine(session, "login-test")))
        {
            if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(session, "waiting")), 5000))
                return 4;
            File.WriteAllText(Path.Combine(session, "signed-in"), "");
            return 0;
        }
        if (!args.SequenceEqual(new[] { "app-server", "--listen", "stdio://" }))
            return 2;
        string eventName = Environment.GetEnvironmentVariable(FixtureExit);
        if (String.IsNullOrWhiteSpace(eventName))
            return 2;
        // Each process captures a login snapshot at startup, as a stale app-server can.
        bool stale = session != null && !File.Exists(Path.Combine(session, "signed-in"));
        using (var exited = EventWaitHandle.OpenExisting(eventName))
        try
        {
            // No preamble in the encoding and detection disabled: expose every BOM on the wire as U+FEFF.
            using (var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true), false))
            using (var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true })
            {
                int stage = 0;
                bool switched = false;
                string scenario = session == null ? "" : Path.GetFileName(session);
                bool served = false, notified = false;
                string line;
                while ((line = input.ReadLine()) != null)
                {
                    // JSON-RPC framing begins at byte zero, including the initial initialize request.
                    if (line.Length == 0 || line[0] != '{')
                        return 3;
                    var request = Json.Parse(line);
                    string method = Json.Str(request, "method");
                    var id = Json.Get(request, "id");
                    if (stage == 0 && method == "initialize" && id != null)
                    {
                        output.WriteLine(Json.Encode(new { id, result = new { userAgent = "test-codex" } }));
                        stage++;
                    }
                    else if (stage == 1 && method == "initialized" && id == null)
                        stage++;
                    else if (stage == 2 && method == "account/read" && id != null)
                    {
                        if (!Object.Equals(Json.Get(Json.Obj(request, "params"), "refreshToken"), false))
                            return 3;
                        string person = switched ? "b" : "a";
                        output.WriteLine(Json.Encode(new { id, result = new {
                            account = new { type = "chatgpt", email = person + "@example.test", planType = "pro" },
                            workspaceRouting = new { chatgptAccountId = "ws-" + person }, requiresOpenaiAuth = true
                        } }));
                        if (served && !notified && scenario == "codex-event")
                        {
                            notified = true;
                            output.WriteLine(Json.Encode(new { method = "account/rateLimits/updated", @params = new {
                                rateLimits = new { secondary = new { windowDurationMins = 10080, usedPercent = 42 } } } }));
                        }
                    }
                    else if (stage == 2 && method == "account/rateLimits/read" && id != null)
                    {
                        if (stale)
                        {
                            if (File.Exists(Path.Combine(session, "hold-request")))
                                File.WriteAllText(Path.Combine(session, "waiting"), "");
                            else
                            {
                                output.WriteLine(Json.Encode(new { id, error = new { code = -32000, message = "401 unauthorized" } }));
                                File.WriteAllText(Path.Combine(session, "account-inspected"), "");
                            }
                            continue;
                        }
                        bool oldSession = scenario == "codex-switch" && !switched;
                        if (oldSession)
                        {
                            switched = true;
                            output.WriteLine(Json.Encode(new { method = "account/updated", @params = new { authMode = "chatgpt" } }));
                            output.WriteLine(Json.Encode(new { method = "account/rateLimits/updated", @params = new {
                                rateLimits = new { primary = new { windowDurationMins = 300, usedPercent = 97 } } } }));
                        }
                        if (scenario == "codex-throttle")
                        {
                            File.AppendAllText(Path.Combine(session, "attempts"), "x");
                            output.WriteLine(Json.Encode(new { id, error = new { code = 429, message = "too many requests",
                                data = new { retryAfterSeconds = 372 } } }));
                            continue;
                        }
                        string workspace = switched && !oldSession ? "ws-b" : "ws-a";
                        var limits = new { limitId = "codex", primary = (object)null,
                                           planType = scenario == "codex-wrong-plan" ? "plus" : "pro",
                                           secondary = new { windowDurationMins = 10080,
                                                             usedPercent = oldSession ? 91 : notified ? 42 : 29 } };
                        output.WriteLine(Json.Encode(new { id, result = new {
                            accountId = scenario == "codex-wrong-workspace" ? "ws-other" : workspace, rateLimits = limits } }));
                        served = true;
                        if (scenario == "codex-switch-at-confirm")
                            switched = true;
                    }
                    else
                        return 3;
                }
                return served ? 0 : 4;
            }
        }
        finally
        {
            if (session != null && !stale) File.WriteAllText(Path.Combine(session, "fresh-session-closed"), "");
            exited.Set();
        }
    }

    static void CodexStdio(string temp)
    {
        string fixture = Path.Combine(temp, "codex.exe");
        File.Copy(Assembly.GetExecutingAssembly().Location, fixture);
        string previousPath = Environment.GetEnvironmentVariable("PATH");
        string previousExit = Environment.GetEnvironmentVariable(FixtureExit);
        string previousSession = Environment.GetEnvironmentVariable(FixtureSession);
        Encoding previousEncoding = Console.InputEncoding;
        string eventName = @"Local\QuotaPeek.Tests.Codex." + Guid.NewGuid().ToString("N");
        using (var exited = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
        try
        {
            string npm = Path.Combine(temp, "npm prefix");
            string npmCodex = Path.Combine(npm, @"node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe");
            string npmClaude = Path.Combine(npm, @"node_modules\@anthropic-ai\claude-code\bin\claude.exe");
            foreach (string file in new[] { npmCodex, npmClaude })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.Copy(fixture, file);
                File.WriteAllText(Path.Combine(npm, Path.GetFileNameWithoutExtension(file) + ".cmd"), "");
            }
            Environment.SetEnvironmentVariable("PATH", npm);
            Check(Tools.Find("codex") == npmCodex && Tools.Client("claude") == npmClaude,
                  "A CLI-only npm setup resolves native Codex and Claude without executing shell shims");
            Environment.SetEnvironmentVariable("PATH", temp);
            Environment.SetEnvironmentVariable(FixtureExit, eventName);
            Environment.SetEnvironmentVariable(FixtureSession, null);
            Console.InputEncoding = new UTF8Encoding(true);
            Check(Console.InputEncoding.GetPreamble().Length == 3 && String.Equals(Tools.Find("codex"), fixture,
                  StringComparison.OrdinalIgnoreCase), "The Codex regression uses only its temporary CLI and a BOM-emitting console");

            var reading = new TaskCompletionSource<Reading>();
            using (var feed = new CodexFeed((r, delta) => { if (!delta) reading.TrySetResult(r); }, delegate {}))
            {
                bool received = reading.Task.Wait(6000);
                var windows = received ? reading.Task.Result.Windows : null;
                Check(received && windows.Count == 1 && windows[0].Label == "Tuần" && windows[0].Used == 29,
                      "Codex stdio completes a BOM-sensitive handshake and receives the weekly-only account quota");
            }
            Check(exited.WaitOne(3500), "Disposing the feed closes its isolated Codex app-server");
            CodexReconnect(temp, false);
            CodexReconnect(temp, true);
            CodexOwnership(temp);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Environment.SetEnvironmentVariable(FixtureExit, previousExit);
            Environment.SetEnvironmentVariable(FixtureSession, previousSession);
            Console.InputEncoding = previousEncoding;
        }
    }

    static void CodexOwnership(string temp)
    {
        foreach (string scenario in new[] { "codex-event", "codex-switch", "codex-switch-at-confirm",
                                            "codex-wrong-workspace", "codex-wrong-plan", "codex-throttle" })
        {
            string dir = Path.Combine(temp, scenario);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "signed-in"), "");
            Environment.SetEnvironmentVariable(FixtureSession, dir);
            var result = new TaskCompletionSource<Reading>();
            var stopped = new TaskCompletionSource<bool>();
            var updated = new TaskCompletionSource<Reading>();
            var owner = new TaskCompletionSource<Reading>();
            using (var feed = new CodexFeed((r, d) => {
                                                result.TrySetResult(r);
                                                if (r.Windows.Any(w => w.Used == 42)) updated.TrySetResult(r);
                                            }, (s, clear) => stopped.TrySetResult(true), delegate {},
                                            r => owner.TrySetResult(r)))
            {
                Check(owner.Task.Wait(6000) && owner.Task.Result.AccountKey ==
                    AccountScope.Key("codex", "email", "a@example.test", "ws-a") &&
                    owner.Task.Result.Plan == "pro", "Verified owner is available before quota on " + scenario);
                if (scenario == "codex-event")
                {
                    Check(result.Task.Wait(6000) && result.Task.Result.Windows.Single().Used == 29 &&
                          updated.Task.Wait(8000) && updated.Task.Result.AccountKey == owner.Task.Result.AccountKey,
                          "A sparse quota event triggers a verified fresh read before the periodic poll");
                }
                else if (scenario == "codex-switch")
                {
                    Check(result.Task.Wait(6000) && result.Task.Result.Windows.Single().Used == 29 &&
                          result.Task.Result.AccountKey == AccountScope.Key("codex", "email", "b@example.test", "ws-b") &&
                          result.Task.Result.Plan == "pro",
                          "Codex discards pending A responses and unidentified events when the authenticated account switches to B");
                }
                else
                {
                    Check(stopped.Task.Wait(6000) && !result.Task.IsCompleted,
                          "Codex suppresses unverified quota on " + scenario);
                    if (scenario == "codex-throttle")
                    {
                        Check(!feed.Refresh() && ReadGuard.Until("codex") >= Fmt.Now + 365 &&
                              File.ReadAllText(Path.Combine(dir, "attempts")) == "x",
                              "Manual Codex refresh respects the server's persisted retry interval");
                    }
                }
            }
            Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(dir, "fresh-session-closed")), 3500),
                  "The isolated " + scenario + " process closes");
        }
    }

    static void CodexReconnect(string temp, bool pending)
    {
        string dir = Path.Combine(temp, pending ? "codex-request-pending" : "codex-stale-auth");
        Directory.CreateDirectory(dir);
        if (pending) File.WriteAllText(Path.Combine(dir, "hold-request"), "");
        Environment.SetEnvironmentVariable(FixtureSession, dir);
        var reading = new TaskCompletionSource<Reading>();
        using (var feed = new CodexFeed((r, delta) => { if (!delta) reading.TrySetResult(r); }, delegate {}))
        {
            string marker = Path.Combine(dir, pending ? "waiting" : "account-inspected");
            Check(SpinWait.SpinUntil(() => File.Exists(marker), 6000), pending
                ? "A request is pending in the old Codex session"
                : "A rejected limits request checks whether the account is still signed in");
            File.WriteAllText(Path.Combine(dir, "signed-in"), "");
            feed.Refresh();
            Check(reading.Task.Wait(6000) && reading.Task.Result.Windows.Single().Used == 29, pending
                ? "Manual refresh recovers a stalled quota request before its deadline"
                : "Manual refresh reloads the current Codex login after an authorization error");
        }
        Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(dir, "fresh-session-closed")), 3500),
              "The replacement Codex process closes after recovery");
    }
}
}
