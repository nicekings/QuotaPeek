using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NhipPC
{
// Claude Code runs /usage as a local command in print mode: no model turn, no quota spent,
// and a structured usage_report in the stream. The interactive TUI is never started.
internal static class ClaudeUsage
{
    public const string Source = "Claude Code";
    public const string Arguments =
        "-p /usage --output-format stream-json --verbose --no-session-persistence --strict-mcp-config --max-turns 1";

    public static Reading Read()
    {
        var blank = Reading.Start("claude", Source);
        string exe = Tools.Client("claude");
        if (exe == null)
        {
            blank.NeedsInstall = true;
            blank.Notice = "Chưa tìm thấy Claude Code trên máy.";
            return blank;
        }
        var before = CliIdentity.Claude(exe, Source);
        if (AccountScope.From(before) == null || before.ScopeUnverified)
            return before;
        var usage = Reading.Start("claude", Source);
        int exit;
        string raw = Run(exe, usage, out exit);
        if (raw == null && !usage.NeedsLogin && !usage.RateLimited)
        {
            usage.Notice = "Claude Code chưa trả kết quả /usage. QuotaPeek sẽ tự thử lại.";
            return usage;
        }
        if (raw != null)
            Parse(usage, raw);
        if (usage.Windows.Count == 0 || usage.NeedsSetup)
            return usage;
        var after = CliIdentity.Claude(exe, Source);
        if (after.AccountKey != before.AccountKey ||
            !String.Equals(after.Plan, before.Plan, StringComparison.OrdinalIgnoreCase))
        {
            after.ScopeUnverified = true;
            after.Notice = "Claude Code vừa đổi tài khoản hoặc gói. Chờ lần đọc tiếp theo.";
            return after;
        }
        usage.AccountKey = before.AccountKey;
        usage.Plan = before.Plan;
        return usage;
    }

    static string Run(string exe, Reading result, out int exit)
    {
        exit = -1;
        var info = Tools.Redirected(exe, Arguments);
        // A QuotaPeek started from a Claude Code terminal must not look like a nested session.
        foreach (string name in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" })
            info.EnvironmentVariables.Remove(name);
        using (var p = System.Diagnostics.Process.Start(info))
        {
            try
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.StandardInput.Close();
                if (!p.WaitForExit(25000) || !Task.WaitAll(new Task[] { stdout, stderr }, 800))
                    return null;
                exit = p.ExitCode;
                if (exit != 0 && CliIdentity.Failure(result, stderr.Result + "\n" + stdout.Result))
                    return null;
                return stdout.Result.Length <= 4194304 ? stdout.Result : null;
            }
            finally { Tools.Kill(p); }
        }
    }

    // Read the stream-json lines. Never keep the raw text: it may contain account details.
    internal static void Parse(Reading r, string stream)
    {
        ArrayList limits = null;
        bool reportSeen = false, resultSeen = false, local = false;
        double turns = 0;
        string text = null;
        foreach (var line in (stream ?? "").Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("{"))
                continue;
            Dictionary<string, object> msg;
            try { msg = Json.Parse(trimmed); }
            catch (ArgumentException) { continue; }
            catch (InvalidOperationException) { continue; }
            var report = Json.Obj(msg, "usage_report");
            if (report != null)
            {
                reportSeen = true;
                limits = Json.Get(Json.Obj(report, "rate_limits"), "limits") as ArrayList;
            }
            if (Json.Str(msg, "type") == "result")
            {
                resultSeen = true;
                local = Json.Str(msg, "local_command") == "usage";
                turns = Json.Number(msg, "num_turns") ?? 0;
                text = Json.Str(msg, "result");
            }
        }
        if (resultSeen && (!local || turns > 0))
        {
            // The text reached the model. Stop so a changed client cannot keep spending quota.
            r.NeedsSetup = true;
            r.Notice = "Claude Code không chạy /usage như lệnh nội bộ nên QuotaPeek đã dừng đọc tự động để " +
                       "không tốn hạn mức. Cập nhật Claude Code rồi bấm “Đọc lại”.";
            return;
        }
        if (limits == null)
        {
            if (!CliIdentity.Failure(r, text))
                r.Notice = reportSeen ? "Claude Code chưa lấy được hạn mức của gói. QuotaPeek sẽ tự thử lại."
                                      : "Claude Code chưa trả hạn mức. QuotaPeek sẽ tự thử lại.";
            return;
        }
        foreach (var limit in limits.OfType<Dictionary<string, object>>())
        {
            var used = Json.Pct(Json.Number(limit, "percent"));
            if (!used.HasValue)
                continue;
            string kind = Json.Str(limit, "kind") ?? "";
            var scope = Json.Obj(limit, "scope");
            string scoped = Json.Str(Json.Obj(scope, "model"), "display_name") ??
                            Json.Str(Json.Obj(scope, "surface"), "display_name");
            string key, label;
            if (kind == "session")
            {
                key = "five_hour";
                label = "5 giờ";
            }
            else if (kind == "weekly_all")
            {
                key = "seven_day";
                label = "Tuần";
            }
            else if (kind.StartsWith("weekly", StringComparison.Ordinal) && !String.IsNullOrWhiteSpace(scoped))
            {
                key = "seven_day_" + Slug(scoped);
                label = "Tuần · " + scoped.Trim();
            }
            else if (kind.Length > 0)
            {
                key = Slug(kind) + (String.IsNullOrWhiteSpace(scoped) ? "" : "_" + Slug(scoped));
                label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.Replace('_', ' ')) +
                        (String.IsNullOrWhiteSpace(scoped) ? "" : " · " + scoped.Trim());
            }
            else
                continue;
            r.Add(new Quota { Key = key, Label = label, Used = used, Reset = Json.Stamp(limit, "resets_at") });
        }
        if (r.Windows.Count == 0)
            r.Notice = "Gói Claude hiện chưa có kỳ hạn mức nào để hiển thị.";
    }

    static string Slug(string value)
    {
        return Regex.Replace(Fmt.Fold(value), @"[^a-z0-9]+", "_").Trim('_');
    }
}
}
