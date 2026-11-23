using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NhipPC
{
internal static class Tools
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string family, ref uint count, IntPtr names, ref uint length,
                                                 IntPtr buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullName(string name, ref uint length, StringBuilder path);
    public static string Find(string name)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
               local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
                       .Split(';')
                       .Concat(new[] { Path.Combine(home, ".local", "bin"),
                                       Path.Combine(home, ".grok", "bin"), Path.Combine(local, "agy", "bin"),
                                       Path.Combine(local, "Programs", "grok", "bin"),
                                       Path.Combine(local, "Microsoft", "WindowsApps") })
                       .Concat(RegisteredPath(EnvironmentVariableTarget.User))
                       .Concat(RegisteredPath(EnvironmentVariableTarget.Machine))
                       .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs)
        {
            if (String.IsNullOrWhiteSpace(dir))
                continue;
            try
            {
                var folder = dir.Trim().Trim('"');
                if (!Path.IsPathRooted(folder))
                    continue;
                var path = Path.Combine(folder, name + ".exe");
                if (File.Exists(path))
                    return path;
                if ((name == "claude" || name == "codex") && File.Exists(Path.Combine(folder, name + ".cmd")))
                {
                    var native = NpmExecutable(folder, name);
                    if (native != null)
                        return native;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        if (name == "codex")
            return PackageCodex();
        return null;
    }
    static string NpmExecutable(string folder, string name)
    {
        // A global npm install may put only a command shim on PATH. Launch the packaged native
        // client directly so the interactive login and the background reader use the same binary.
        string modules = Path.Combine(folder, "node_modules");
        if (name == "claude")
        {
            string cli = Path.Combine(modules, @"@anthropic-ai\claude-code\bin\claude.exe");
            return File.Exists(cli) ? cli : null;
        }
        string codex = Path.Combine(modules, @"@openai\codex");
        foreach (var package in new[] { Path.Combine(codex, @"node_modules\@openai\codex-win32-x64"),
                                        Path.Combine(modules, @"@openai\codex-win32-x64"), codex })
            foreach (var bin in new[] { "bin", "codex" })
            {
                string exe = Path.Combine(package, @"vendor\x86_64-pc-windows-msvc", bin, "codex.exe");
                if (File.Exists(exe))
                    return exe;
            }
        return null;
    }
    static IEnumerable<string> RegisteredPath(EnvironmentVariableTarget scope)
    {
        // An installer can update PATH while the tray application is still running.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(';'))
            yield return Environment.ExpandEnvironmentVariables(dir);
    }
    static string PackageCodex()
    {
        uint n = 0, len = 0;
        GetPackagesByPackageFamily("OpenAI.Codex_2p2nqsd0c76g0", ref n, IntPtr.Zero, ref len, IntPtr.Zero);
        if (n == 0 || n > 64 || len > 65536)
            return null;
        var names = Marshal.AllocHGlobal((int)n * IntPtr.Size);
        var buffer = Marshal.AllocHGlobal((int)len * 2);
        try
        {
            if (GetPackagesByPackageFamily("OpenAI.Codex_2p2nqsd0c76g0", ref n, names, ref len, buffer) != 0)
                return null;
            for (int i = 0; i < n; i++)
            {
                string full = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, i * IntPtr.Size));
                uint size = 0;
                GetPackagePathByFullName(full, ref size, null);
                if (size == 0 || size > 32768)
                    continue;
                var b = new StringBuilder((int)size);
                if (GetPackagePathByFullName(full, ref size, b) == 0)
                {
                    string file = Path.Combine(b.ToString(), "app", "resources", "codex.exe");
                    if (File.Exists(file))
                        return file;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(names);
        }
        return null;
    }
    public static ProcessStartInfo Redirected(string file, string args)
    {
        return new ProcessStartInfo(file, args) { UseShellExecute = false,
                                                  CreateNoWindow = true,
                                                  RedirectStandardInput = true,
                                                  RedirectStandardOutput = true,
                                                  RedirectStandardError = true,
                                                  StandardOutputEncoding = new UTF8Encoding(false),
                                                  StandardErrorEncoding = new UTF8Encoding(false),
                                                  WorkingDirectory = Store.Root };
    }
    public static string Self
    {
        get {
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }
    }
    public static string Client(string id)
    {
        return id == "claude"        ? Find("claude")
               : id == "antigravity" ? Find("agy")
               : id == "grok"        ? Find("grok")
                                     : null;
    }
    public static string ClientName(string id)
    {
        return id == "claude" ? "Claude Code" : id == "antigravity" ? "Antigravity CLI" : "Grok Build";
    }
    public static void Kill(Process p)
    {
        try
        {
            if (p != null && !p.HasExited)
                p.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

internal sealed class CodexFeed : IDisposable
{
    [DllImport("kernel32.dll")]
    static extern uint GetConsoleCP();
    readonly Action<Reading, bool> publish;
    readonly Action<Phase, string, bool> status;
    readonly Action<bool, bool> accountChanged;
    readonly Action<Reading> knownOwner;
    readonly CancellationTokenSource stop = new CancellationTokenSource();
    int reconnect;
    long lastManualRefresh;
    string lastAuthenticatedScope;
    enum Request { Initialize, Account, Limits, ConfirmAccount }
    public CodexFeed(Action<Reading, bool> publish, Action<string, bool> report)
        : this(publish, report, delegate {})
    {
    }
    public CodexFeed(Action<Reading, bool> publish, Action<string, bool> report, Action<bool, bool> accountChanged,
                     Action<Reading> knownOwner = null)
        : this(publish, (phase, message, clear) => report(message, clear), accountChanged, knownOwner)
    {
    }
    // Every status carries its phase so the hub never has to interpret the message text.
    public CodexFeed(Action<Reading, bool> publish, Action<Phase, string, bool> status, Action<bool, bool> accountChanged,
                     Action<Reading> knownOwner)
    {
        this.publish = publish;
        this.status = status;
        this.accountChanged = accountChanged;
        this.knownOwner = knownOwner;
        Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning,
                              TaskScheduler.Default);
    }
    void Report(Phase phase, string message, bool clear)
    {
        status(phase, message, clear);
    }
    public bool Refresh()
    {
        if (ReadGuard.Until("codex") > Fmt.Now)
        {
            Report(Phase.Cooldown, "Codex đang tạm dừng theo yêu cầu của dịch vụ.", false);
            return false;
        }
        long now = Fmt.Now;
        if (now - Interlocked.Read(ref lastManualRefresh) < 5)
        {
            return false;
        }
        Interlocked.Exchange(ref lastManualRefresh, now);
        Interlocked.Exchange(ref reconnect, 1);
        return true;
    }
    void Run()
    {
        var token = stop.Token;
        int backoff = 15;
        while (!token.IsCancellationRequested)
        {
            long hold = ReadGuard.Until("codex");
            if (hold > Fmt.Now)
            {
                Report(Phase.Cooldown, "Codex đang tạm dừng theo yêu cầu của dịch vụ.", false);
                while (Fmt.Now < hold && !token.IsCancellationRequested)
                    token.WaitHandle.WaitOne(1000);
                continue;
            }
            Interlocked.Exchange(ref reconnect, 0);
            try
            {
                var file = Tools.Find("codex");
                if (file == null)
                    Report(Phase.NeedsInstall, "Chưa tìm thấy Codex trên máy.", false);
                else
                    Session(file, token, ref backoff);
            }
            catch (Exception)
            {
                if (!token.IsCancellationRequested)
                    Report(Phase.Error, "Mất kết nối Codex. Đang thử lại.", false);
            }
            for (int i = 0; i < backoff && !token.IsCancellationRequested; i++)
            {
                if (Volatile.Read(ref reconnect) != 0)
                    break;
                token.WaitHandle.WaitOne(1000);
            }
            backoff = Math.Min(180, backoff * 2);
        }
    }
    static bool AccountError(string error)
    {
        return error.Contains("401") || error.Contains("403") || error.Contains("sign in") ||
               error.Contains("log in") || error.Contains("login") || error.Contains("unauthoriz") ||
               error.Contains("authenticat") || error.Contains("api key");
    }
    static bool IsChatGpt(string mode)
    {
        return String.Equals(mode, "chatgpt", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(mode, "chatgptAuthTokens", StringComparison.OrdinalIgnoreCase);
    }
    sealed class Owner
    {
        public string Kind, User, Workspace, Plan;
        public bool Personal
        {
            get { return new[] { "free", "go", "plus", "pro", "prolite", "promax" }.Contains(Plan); }
        }

        public string Key(string workspace)
        {
            return AccountScope.Key("codex", Kind, User, workspace);
        }

        public bool Matches(Owner other)
        {
            return other != null && Key(Workspace) == other.Key(other.Workspace) && Plan == other.Plan;
        }

        public bool Bind(Reading r, Dictionary<string, object> result)
        {
            string snapshotWorkspace = Json.Str(result, "accountId");
            if (!String.IsNullOrWhiteSpace(Workspace) && !String.IsNullOrWhiteSpace(snapshotWorkspace) &&
                !String.Equals(snapshotWorkspace, Workspace, StringComparison.Ordinal))
                return false;
            string workspace = !String.IsNullOrWhiteSpace(Workspace) ? Workspace : snapshotWorkspace;
            if (!Personal && String.IsNullOrWhiteSpace(workspace))
                return false;
            var groups = Json.Obj(result, "rateLimitsByLimitId");
            var snapshots = groups == null ? Enumerable.Empty<Dictionary<string, object>>() :
                groups.Values.OfType<Dictionary<string, object>>();
            foreach (var bucket in snapshots.Concat(new[] { Json.Obj(result, "rateLimits") }))
            {
                string plan = Json.Str(bucket, "planType");
                if (plan != null && !String.Equals(Plan, plan, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            r.AccountKey = Key(workspace);
            r.Plan = Plan;
            return AccountScope.From(r) != null;
        }
    }
    Owner Identify(Dictionary<string, object> result)
    {
        var account = Json.Obj(result, "account");
        string mode = Json.Str(account, "type");
        if (!IsChatGpt(mode))
        {
            accountChanged(false, true);
            Report(Phase.NeedsLogin, String.Equals(mode, "apiKey", StringComparison.OrdinalIgnoreCase)
                ? "Codex đang dùng API key. Đăng nhập bằng ChatGPT để đọc hạn mức của gói."
                : "Cần đăng nhập Codex bằng tài khoản ChatGPT.", true);
            lastAuthenticatedScope = null;
            return null;
        }
        string email = Json.Str(account, "email");
        var owner = new Owner { Kind = String.IsNullOrWhiteSpace(email) ? "user" : "email",
                                User = String.IsNullOrWhiteSpace(email) ? Json.Str(result, "userId") : email,
                                Plan = (Json.Str(account, "planType") ?? "").Trim().ToLowerInvariant(),
                                Workspace = Json.Str(Json.Obj(result, "workspaceRouting"), "chatgptAccountId") };
        var scope = new AccountScope { Provider = "codex", AccountKey = owner.Key(owner.Workspace), Plan = owner.Plan };
        if (!scope.Valid("codex"))
        {
            accountChanged(true, true);
            Report(Phase.Error, "Codex chưa xác nhận được tài khoản và gói.", true);
            lastAuthenticatedScope = null;
            return null;
        }
        string name = scope.Name("");
        if (name != lastAuthenticatedScope)
        {
            bool usable = owner.Personal || !String.IsNullOrWhiteSpace(owner.Workspace);
            accountChanged(true, lastAuthenticatedScope != null || !usable);
            lastAuthenticatedScope = name;
            if (knownOwner != null && usable)
                knownOwner(new Reading { Provider = "codex", AccountKey = scope.AccountKey, Plan = scope.Plan });
        }
        return owner;
    }
    static long RetryDeadline(Dictionary<string, object> error, int backoff, long now)
    {
        var data = Json.Obj(error, "data");
        var headers = Json.Obj(data, "headers");
        string message = Json.Str(error, "message") ?? "";
        double? seconds = Json.Number(data, "retryAfterSeconds") ?? Json.Number(data, "retry_after_seconds") ??
                          Json.Number(data, "retry_after") ?? Json.Number(data, "retryAfter") ??
                          (Json.Number(data, "retryAfterMs") ?? Json.Number(data, "retry_after_ms")) / 1000.0 ??
                          Json.Number(headers, "Retry-After") ?? Json.Number(headers, "retry-after");
        string retry = Json.Str(headers, "Retry-After") ?? Json.Str(headers, "retry-after") ??
                       Json.Str(data, "retry_after") ?? Json.Str(data, "retryAfter");
        if (retry == null && message.IndexOf("retry-after", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var dateText = System.Text.RegularExpressions.Regex.Match(message,
                @"retry-after\s*:\s*([A-Za-z]{3},\s*\d{1,2}\s+[A-Za-z]{3}\s+\d{4}\s+[\d:]+\s+GMT)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (dateText.Success)
                retry = dateText.Groups[1].Value;
        }
        DateTimeOffset date;
        long until = Json.Stamp(data, "retryAt");
        if (retry != null && !seconds.HasValue && DateTimeOffset.TryParse(retry,
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out date))
            until = Math.Max(until, (long)(date.UtcDateTime - Fmt.Epoch).TotalSeconds);
        if (!seconds.HasValue)
        {
            var m = System.Text.RegularExpressions.Regex.Match(message,
                @"(?:retry-after\s*:\s*|retry (?:after|in)\s+)([0-9]+(?:\.[0-9]+)?)\s*(milliseconds?|ms|seconds?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d)?\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            double n;
            if (m.Success && Double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out n))
            {
                string unit = m.Groups[2].Value.ToLowerInvariant();
                if (unit == "ms" || unit.StartsWith("millisecond")) seconds = n / 1000.0;
                else if (unit.StartsWith("m")) seconds = n * 60.0;
                else if (unit.StartsWith("h")) seconds = n * 3600.0;
                else if (unit.StartsWith("d")) seconds = n * 86400.0;
                else seconds = n;
            }
        }
        if (!seconds.HasValue && until <= now && Json.Number(error, "code") != 429 &&
            Json.Number(data, "status") != 429 && message.IndexOf("429", StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf("too many requests", StringComparison.OrdinalIgnoreCase) < 0 &&
            message.IndexOf("rate limit", StringComparison.OrdinalIgnoreCase) < 0)
            return 0;
        return Math.Max(until, now + (long)Math.Min(253402300799L - now,
                            Math.Max(backoff, Math.Ceiling(seconds.GetValueOrDefault()))));
    }
    void Session(string file, CancellationToken token, ref int backoff)
    {
        using (var p = new Process {
            StartInfo = Tools.Redirected(file, "app-server --listen stdio://")
        }) using (var messages = new BlockingCollection<string>(256))
        {
            int overflow = 0;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
            {
                if (e.Data != null && e.Data.Length < 1048576)
                    try
                    {
                        if (!messages.TryAdd(e.Data))
                            Interlocked.Exchange(ref overflow, 1);
                    }
                    catch (ObjectDisposedException)
                    {
                    }
            };
            p.ErrorDataReceived += delegate {};
            // Framework creates its stdin writer and flushes its preamble inside Process.Start.
            // Keep the attached terminal's code page and send JSON from the very first byte.
            if (GetConsoleCP() == 65001 && Console.InputEncoding.CodePage == 65001 &&
                Console.InputEncoding.GetPreamble().Length > 0)
                Console.InputEncoding = new UTF8Encoding(false);
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                var input = new StreamWriter(p.StandardInput.BaseStream,
                                             new UTF8Encoding(false)) { AutoFlush = true };
                var clientInfo = new { name = "quotapeek", title = "QuotaPeek", version = "2.0" };
                input.WriteLine(Json.Encode(new { method = "initialize", id = 1, @params = new { clientInfo } }));
                long deadline = Fmt.Now + 25, next = 0, lastFetch = 0, lastIdentityRead = 0;
                int pending = 1, sequence = 1, errors = 0;
                Request request = Request.Initialize;
                Owner owner = null;
                Dictionary<string, object> snapshot = null;
                Reading last = null;
                bool ready = false, identifiedInSession = false;
                while (!token.IsCancellationRequested && !p.HasExited)
                {
                    if (Volatile.Read(ref reconnect) != 0)
                        return;
                    if (Volatile.Read(ref overflow) != 0)
                    {
                        Report(Phase.Connecting, "Luồng cập nhật Codex bị gián đoạn. Đang xác minh lại tài khoản.", true);
                        lastAuthenticatedScope = null;
                        return;
                    }
                    if (pending != 0 && Fmt.Now > deadline)
                    {
                        Report(Phase.Error, "Codex chưa phản hồi. Đang thử lại.", false);
                        return;
                    }
                    string line;
                    if (messages.TryTake(out line, 250))
                    {
                        Dictionary<string, object> msg;
                        try { msg = Json.Parse(line); }
                        catch (ArgumentException) { continue; }
                        if (msg == null)
                            continue;
                        string method = Json.Str(msg, "method");
                        var par = Json.Obj(msg, "params");
                        if (method == "account/updated")
                        {
                            // Supersede any in-flight snapshot and its confirmation. Late replies have an old id.
                            bool chat = IsChatGpt(Json.Str(par, "authMode"));
                            bool sameRouting = Json.Str(par, "reason") == "workspaceRoutingReady";
                            if (!chat || (!sameRouting && identifiedInSession))
                            {
                                accountChanged(chat, !chat || lastAuthenticatedScope != null);
                                lastAuthenticatedScope = null;
                                identifiedInSession = false;
                            }
                            owner = null;
                            last = null;
                            snapshot = null;
                            if (ready)
                                pending = 0;
                            next = Math.Max(lastFetch + 2, lastIdentityRead + 1);
                        }
                        else if (method == "account/rateLimits/updated" && last != null && pending == 0 &&
                                 errors == 0 && next != Int64.MaxValue)
                        {
                            // Sparse notifications need not identify their owner. Re-check the authenticated
                            // snapshot on a change, coalescing bursts and ignoring echoes of our own query.
                            var update = QuotaReaders.Codex(par);
                            if (update.Windows.Any(w => !last.Windows.Any(v => v.Key == w.Key && v.Used == w.Used &&
                                                                             v.Reset == w.Reset && v.Balance == w.Balance)))
                                next = Math.Min(next, Math.Max(Fmt.Now, lastFetch + 5));
                        }
                        double? id = Json.Number(msg, "id");
                        if (pending != 0 && pending == id)
                        {
                            pending = 0;
                            var error = Json.Obj(msg, "error");
                            var result = Json.Obj(msg, "result");
                            if (error != null)
                            {
                                errors = Math.Min(errors + 1, 4);
                                int wait = Math.Min(600, 30 * (1 << errors));
                                long until = RetryDeadline(error, wait, Fmt.Now);
                                string err = (Json.Str(error, "message") ?? "").ToLowerInvariant();
                                if (until > 0)
                                {
                                    ReadGuard.Delay("codex", until);
                                    Report(Phase.Cooldown, "Codex đang giới hạn lượt đọc.", false);
                                    return;
                                }
                                double? status = Json.Number(Json.Obj(error, "data"), "status") ?? Json.Number(error, "code");
                                if (err.Contains("403") || err.Contains("forbidden") || status == 403)
                                {
                                    ReadGuard.Delay("codex", Fmt.Now + 300);
                                    Report(Phase.NeedsLogin, "Codex từ chối quyền đọc hạn mức. Kiểm tra tài khoản đang đăng nhập.", true);
                                    return;
                                }
                                if (AccountError(err) || status == 401)
                                {
                                    Report(Phase.NeedsLogin, "Codex chưa cho phép đọc hạn mức. Đăng nhập lại bằng ChatGPT.", true);
                                    next = Int64.MaxValue;
                                }
                                else
                                {
                                    Report(Phase.Error, "Codex chưa cung cấp hạn mức.", false);
                                    next = Fmt.Now + wait;
                                }
                                owner = null;
                                snapshot = null;
                                last = null;
                                if (request == Request.Initialize)
                                    return;
                            }
                            else if (request == Request.Initialize)
                            {
                                input.WriteLine("{\"method\":\"initialized\"}");
                                ready = true;
                            }
                            else if (request == Request.Account || request == Request.ConfirmAccount)
                            {
                                if (result == null || !result.ContainsKey("account") && Json.Get(result, "requiresOpenaiAuth") == null)
                                {
                                    Report(Phase.Connecting, "Codex chưa xác nhận được tài khoản. Đang chờ đọc lại.", true);
                                    lastAuthenticatedScope = null;
                                    owner = null;
                                    snapshot = null;
                                    last = null;
                                    next = Fmt.Now + 30;
                                    continue;
                                }
                                Owner verified = Identify(result);
                                identifiedInSession = verified != null;
                                if (verified == null)
                                {
                                    owner = null;
                                    snapshot = null;
                                    last = null;
                                    next = Int64.MaxValue;
                                }
                                else if (snapshot == null)
                                    owner = verified;
                                else
                                {
                                    var r = QuotaReaders.Codex(snapshot);
                                    if (verified.Matches(owner) && verified.Bind(r, snapshot))
                                    {
                                        if (r.Windows.Count > 0)
                                        {
                                            publish(r, false);
                                            last = r;
                                        }
                                        else
                                        {
                                            Report(Phase.Error, "Tài khoản Codex chưa có số liệu hạn mức.", true);
                                            last = null;
                                        }
                                    }
                                    else
                                    {
                                        Report(Phase.Connecting, "Tài khoản, workspace hoặc gói Codex đã thay đổi. Đang đọc lại.", true);
                                        last = null;
                                    }
                                    owner = null;
                                    snapshot = null;
                                    errors = 0;
                                    backoff = 15;
                                    next = Fmt.Now + 30;
                                }
                            }
                            else
                            {
                                snapshot = result;
                                if (snapshot == null)
                                {
                                    Report(Phase.Error, "Codex chưa cung cấp hạn mức.", false);
                                    owner = null;
                                    next = Fmt.Now + 30;
                                }
                            }
                        }
                    }
                    if (ready && pending == 0 && Fmt.Now >= next)
                    {
                        pending = ++sequence;
                        deadline = Fmt.Now + 25;
                        if (owner == null || snapshot != null)
                        {
                            request = snapshot == null ? Request.Account : Request.ConfirmAccount;
                            lastIdentityRead = Fmt.Now;
                            input.WriteLine(Json.Encode(new { method = "account/read", id = pending,
                                                              @params = new { refreshToken = false } }));
                        }
                        else
                        {
                            request = Request.Limits;
                            lastFetch = Fmt.Now;
                            input.WriteLine(Json.Encode(new { method = "account/rateLimits/read", id = pending,
                                                              @params = new { excludeResetCreditDetails = true } }));
                        }
                    }
                }
                if (!token.IsCancellationRequested)
                    Report(Phase.Error, "Mất kết nối Codex. Đang thử lại.", false);
            }
            finally
            {
                try
                {
                    p.StandardInput.Close();
                    if (!p.WaitForExit(800))
                        Tools.Kill(p);
                    p.WaitForExit(1500);
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
    }
    public void Dispose()
    {
        stop.Cancel();
    }
}
}
