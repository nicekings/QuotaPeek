using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NhipPC
{
internal static class CliIdentity
{
    internal static void Bind(Reading reading, string email, string plan, string workspace)
    {
        if (String.IsNullOrWhiteSpace(plan) || plan.Length > 100 || plan.Any(Char.IsControl) ||
            String.IsNullOrWhiteSpace(email) || !Regex.IsMatch(email.Trim(), @"^[^\s<>@]+@[^\s<>@]+\.[^\s<>@]+$"))
            return;
        if (Regex.IsMatch(plan, @"\b(team|business|enterprise|workspace|organization)\b", RegexOptions.IgnoreCase) &&
            String.IsNullOrWhiteSpace(workspace))
            return;
        reading.Plan = plan.Trim();
        reading.AccountKey = AccountScope.Key(reading.Provider, "email", email.Trim(), workspace);
    }

    internal static bool Failure(Reading r, string message)
    {
        string text = message ?? "";
        if (Regex.IsMatch(text, @"(?i)too many requests|rate[ -]?limit(?:ed| exceeded)|(?:http|status|error)\s*[:= ]\s*429\b|retry-after\s*:"))
        {
            r.Notice = "Dịch vụ yêu cầu chờ trước khi đọc lại.";
            r.RateLimited = true;
            var delay = Regex.Match(text, @"(?im)\bretry-after:\s*([^\r\n]+)");
            if (delay.Success)
            {
                int seconds;
                DateTimeOffset date;
                if (Int32.TryParse(delay.Groups[1].Value.Trim(), out seconds) && seconds >= 0)
                    r.RetryAfterSeconds = seconds;
                else if (DateTimeOffset.TryParseExact(delay.Groups[1].Value.Trim(), "r",
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out date))
                    r.RetryAfterSeconds = (int)Math.Max(0, Math.Min(Int32.MaxValue,
                                                        Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds)));
            }
            return true;
        }
        if (r.Provider == "claude" && Regex.IsMatch(text, @"(?i)\bcaptcha\b|\b(?:human |identity )verification required|verify (?:that )?(?:you are|you['’]re) (?:a )?human|verify your identity"))
        {
            r.ScopeUnverified = true;
            r.NeedsLogin = true;
            r.Notice = "Claude yêu cầu xác minh tài khoản. Kiểm tra thông báo trong cửa sổ đăng nhập chính thức của Claude.";
            return true;
        }
        if ((r.Provider == "claude" && Regex.IsMatch(text, @"(?i)\baccount (?:has been |is )?(?:locked|suspended)\b")) ||
            Regex.IsMatch(text, @"(?i)\bsign in to|\blog in to|please (?:sign|log) in|authentication required|not (?:logged|signed) in|device code|choose.*login method|session (?:has )?expired|captcha|verify (?:you are human|your identity)|access denied|forbidden|unauthorized|(?:http|status|error)\s*[:= ]\s*(?:401|403)\b"))
        {
            r.NeedsLogin = true;
            if (r.Provider == "claude")
            {
                r.ScopeUnverified = true;
                r.Notice = "Claude chưa cho phép đọc hạn mức. Kiểm tra thông báo tài khoản trong Claude trước khi thử lại.";
            }
            else
                r.Notice = "Mở CLI để kiểm tra phiên đăng nhập hoặc hoàn tất xác minh.";
            return true;
        }
        return false;
    }

    // Only the official client sees its credentials. Never persist command output or diagnostic text.
    static string Run(string exe, string args, Reading result, int timeout)
    {
        int exit;
        string raw = Run(exe, args, result, timeout, out exit);
        return exit == 0 ? raw : null;
    }
    static string Run(string exe, string args, Reading result, int timeout, out int exit)
    {
        exit = -1;
        using (var p = Process.Start(Tools.Redirected(exe, args)))
        {
            try
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.StandardInput.Close();
                if (!p.WaitForExit(timeout) || !Task.WaitAll(new Task[] { stdout, stderr }, 800))
                    return null;
                exit = p.ExitCode;
                if (exit != 0)
                    Failure(result, stderr.Result + "\n" + stdout.Result);
                return stdout.Result.Length <= 4194304 ? stdout.Result : null;
            }
            finally { Tools.Kill(p); }
        }
    }

    public static Reading Claude(string exe, string source)
    {
        var r = Reading.Start("claude", source);
        r.Notice = "Claude Code chưa xác nhận được tài khoản và gói.";
        int exit;
        string raw = Run(exe, "auth status", r, 6500, out exit);
        ReadClaudeStatus(r, raw, exit);
        return r;
    }
    internal static void ReadClaudeStatus(Reading r, string raw, int exit)
    {
        if (String.IsNullOrWhiteSpace(raw) || r.NeedsLogin || r.RateLimited)
            return;
        Dictionary<string, object> data = null;
        try { data = Json.Parse(raw); }
        catch (ArgumentException) {}
        catch (InvalidOperationException) {}
        string diagnostic = Json.Get(data, "error") as string ?? Json.Str(Json.Obj(data, "error"), "message");
        if (!String.IsNullOrWhiteSpace(diagnostic) && Failure(r, diagnostic))
            return;
        if (data == null || !(Json.Get(data, "loggedIn") is bool))
        {
            if (data != null || !Failure(r, raw))
            {
                r.ScopeUnverified = true;
                r.Notice = "Claude Code chưa xác nhận được trạng thái đăng nhập. Kiểm tra lại sau.";
            }
            return;
        }
        // Status may deliberately exit with code 1 for a signed-out account. A failed
        // command can never establish a signed-in identity, even if it printed one.
        if (Object.Equals(Json.Get(data, "loggedIn"), false))
        {
            r.NeedsLogin = true;
            r.ScopeUnverified = true;
            r.Notice = "Claude Code chưa đăng nhập. Đăng nhập bằng tài khoản có gói Claude của bạn.";
            return;
        }
        r.ScopeUnverified = true;
        if (exit != 0)
            return;
        string method = (Json.Get(data, "authMethod") as string ?? "").Trim().ToLowerInvariant();
        if (method != "claude.ai")
        {
            if (method.Length == 0)
                r.Notice = "Claude Code đã đăng nhập nhưng chưa báo loại tài khoản. Kiểm tra lại tài khoản đang dùng trong Claude Code.";
            else
            {
                r.NeedsLogin = true;
                bool api = method == "api_key" || method == "api_key_helper" || method == "console";
                r.Notice = api
                    ? "Claude Code đang kết nối qua API. Đăng nhập bằng tài khoản có gói Claude để đọc hạn mức thuê bao."
                    : "Claude Code đang dùng cách đăng nhập khác. Đăng nhập bằng tài khoản có gói Claude để đọc hạn mức thuê bao.";
            }
            return;
        }
        string plan = Json.Get(data, "subscriptionType") as string;
        Bind(r, Json.Get(data, "email") as string, plan,
             Json.Get(data, "orgId") as string);
        if (AccountScope.From(r) == null)
        {
            r.AccountKey = r.Plan = null;
            r.Notice = String.IsNullOrWhiteSpace(plan)
                ? "Bạn đã đăng nhập Claude. Claude Code chưa báo tên gói. Kiểm tra gói đang dùng trong Claude rồi đọc lại."
                : "Bạn đã đăng nhập Claude. Claude Code chưa cung cấp đủ thông tin để xác minh tài khoản và gói đang dùng.";
            return;
        }
        r.ScopeUnverified = false;
        r.Notice = null;
    }

    public static Reading Antigravity(string exe, string source)
    {
        var r = Reading.Start("antigravity", source);
        r.Notice = "Cần Antigravity CLI từ 1.1.11 để đọc lệnh /usage ở chế độ nền.";
        var version = Run(exe, "--version", r, 4000);
        var match = Regex.Match(version ?? "", @"(?m)^(?:[^\r\n]*?\s)?v?(\d+\.\d+\.\d+)\b");
        Version parsed;
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out parsed) || parsed < new Version(1, 1, 11))
            return r;
        r.Notice = "Antigravity CLI chưa trả được hạn mức tài khoản.";
        // This built-in command is supported from v1.1.11. Force it to remain a command
        // even when the user's settings normally send slash-prefixed input to a model.
        var raw = Run(exe, "--disable-slash-commands=false -p /usage --output-format json", r, 14500);
        if (String.IsNullOrWhiteSpace(raw))
            return r;
        Dictionary<string, object> doc = null;
        try { doc = Json.Parse(raw); }
        catch (ArgumentException) {}
        catch (InvalidOperationException) {}
        var report = Json.Obj(doc, "result") ?? doc;
        var command = Json.Obj(report, "command");
        if (Json.Number(report, "num_turns") > 0)
        {
            Failure(r, Json.Str(report, "error"));
            return r;
        }
        if (Json.Str(command, "name") != "usage")
        {
            // The documented /usage output is a text report; it may also arrive inside the JSON envelope.
            string text = doc == null ? raw : Json.Str(doc, "response") ?? Json.Str(report, "result") ?? Json.Str(doc, "result");
            if (Failure(r, Json.Str(report, "error")) || String.IsNullOrWhiteSpace(text))
                return r;
            var textReport = QuotaReaders.AntigravityText(text, source);
            if (textReport.Windows.Count == 0)
                return r;
            if (textReport.AccountKey == null)
                textReport.Notice = "Bảng /usage của Antigravity CLI chưa ghi tài khoản. Đăng nhập lại trong Antigravity CLI.";
            return textReport;
        }
        var data = Json.Obj(command, "data");
        var buckets = new Dictionary<string, object>();
        var groups = Json.Get(data, "groups") as IEnumerable;
        if (groups != null)
            foreach (var group in groups.OfType<Dictionary<string, object>>())
            {
                var rows = Json.Get(group, "buckets") as IEnumerable;
                if (rows == null)
                    continue;
                string groupName = Json.Str(group, "name") ?? Json.Str(group, "title") ??
                                   Json.Str(group, "display_name") ?? Json.Str(group, "label");
                foreach (var bucket in rows.OfType<Dictionary<string, object>>())
                {
                    string id = Json.Str(bucket, "id");
                    if (String.IsNullOrWhiteSpace(id))
                        continue;
                    if (groupName != null && !bucket.ContainsKey("group_name"))
                        bucket["group_name"] = groupName;
                    buckets[id] = bucket;
                }
            }
        if (data == null || buckets.Count == 0)
            return r;
        data["quota"] = buckets;
        var reading = QuotaReaders.StatusLine("antigravity", data);
        reading.Source = source;
        if (reading.AccountKey == null)
            reading.Notice = "Bản /usage thiếu tài khoản hoặc gói. Bật nhận sự kiện status line của Antigravity.";
        return reading;
    }

    // Grok's ACP host supports explicitly headless authentication with a previously
    // cached login. Never request one of the interactive methods from the welcome screen.
    public static bool GrokSignedIn(string exe, Reading r)
    {
        using (var p = Process.Start(Tools.Redirected(exe, "agent stdio")))
        {
            try
            {
                p.BeginErrorReadLine();
                var clock = Stopwatch.StartNew();
                var init = GrokReply(p, 1, "initialize",
                    new { protocolVersion = 1, clientCapabilities = new { } }, r, clock);
                if (init == null)
                    return false;
                var methods = Json.Get(init, "authMethods") as IEnumerable;
                if (methods == null || !methods.OfType<Dictionary<string, object>>()
                        .Any(x => Json.Str(x, "id") == "cached_token"))
                {
                    r.NeedsLogin = true;
                    r.Notice = "Mở Grok Build và đăng nhập trước khi bật đọc nền.";
                    return false;
                }
                return GrokReply(p, 2, "authenticate",
                    new { methodId = "cached_token", _meta = new { headless = true } }, r, clock) != null;
            }
            finally { Tools.Kill(p); }
        }
    }

    static Dictionary<string, object> GrokReply(Process process, int id, string method, object args,
                                                Reading reading, Stopwatch clock)
    {
        process.StandardInput.WriteLine(Json.Encode(new { jsonrpc = "2.0", id, method, @params = args }));
        process.StandardInput.Flush();
        while (clock.ElapsedMilliseconds < 6500)
        {
            var line = process.StandardOutput.ReadLineAsync();
            if (!line.Wait(Math.Max(1, 6500 - (int)clock.ElapsedMilliseconds)) ||
                line.Result == null || line.Result.Length > 262144)
                break;
            var msg = Json.Parse(line.Result);
            if (Json.Number(msg, "id") != id || msg.ContainsKey("method"))
                continue;
            var err = Json.Obj(msg, "error");
            if (err != null)
            {
                Failure(reading, Json.Str(err, "message"));
                if (Json.Number(err, "code") == -32000 && !reading.RateLimited && reading.RetryAfterSeconds <= 0)
                {
                    reading.NeedsLogin = true;
                    reading.Notice = "Cần đăng nhập Grok Build.";
                }
                return null;
            }
            return Json.Obj(msg, "result");
        }
        return null;
    }
}
}
