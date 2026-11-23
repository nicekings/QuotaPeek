using System;

namespace NhipPC
{
// One state per AI service. Every change goes through Reducer, so the scheduler, the
// dashboard, the tray and the settings card all read the same answer.
internal enum Phase
{
    Off,          // The service is hidden in Content settings.
    Connecting,   // Enabled, waiting for the first verified reading.
    Live,         // The last read succeeded and is recent.
    Stale,        // A read failed or is overdue; the last numbers are still shown with "~".
    NeedsInstall, // The client application or CLI is missing.
    NeedsLogin,   // The client is signed out or uses a login without plan limits.
    NeedsSetup,   // The client waits for the user; automatic reads stop until "Đọc lại".
    Cooldown,     // The service asked QuotaPeek to wait until RetryAt.
    Error         // Reads fail and no recent numbers remain.
}

// The single answer of one read attempt.
internal sealed class ReadResult
{
    public Phase Phase;
    public Reading Reading;
    public AccountScope VerifiedAccount;
    public string Message;
    public long RetryAt;
    // Seconds until the next automatic attempt. 0 lets the reducer choose a backoff.
    public int NextIn;

    public static ReadResult Ok(Reading r)
    {
        return new ReadResult { Phase = Phase.Live, Reading = r };
    }
    public static ReadResult Fail(Phase phase, string message, long retryAt = 0)
    {
        return new ReadResult { Phase = phase, Message = message, RetryAt = retryAt };
    }

    // Collectors report through Reading flags. Translate them in exactly one place.
    public static ReadResult From(string id, Reading r, long now)
    {
        var result = Classify(id, r, now);
        if (r != null && r.Provider == id && !r.ScopeUnverified &&
            (result.Phase == Phase.Error || result.Phase == Phase.Cooldown || result.Phase == Phase.NeedsSetup))
            result.VerifiedAccount = AccountScope.From(r);
        return result;
    }

    static ReadResult Classify(string id, Reading r, long now)
    {
        if (r == null)
            return Fail(Phase.Error, "Nguồn chưa phản hồi. QuotaPeek sẽ tự thử lại.");
        if (r.RateLimited || r.RetryAfterSeconds > 0)
        {
            long wait = Math.Max(r.RetryAfterSeconds, r.RateLimited ? 300 : 0);
            return Fail(Phase.Cooldown, r.Notice ?? "Dịch vụ yêu cầu chờ trước khi đọc lại.", now + wait);
        }
        if (r.NeedsInstall)
            return Fail(Phase.NeedsInstall, r.Notice ?? "Cần cài " + Tools.ClientName(id) + ".");
        if (r.NeedsLogin)
            return Fail(Phase.NeedsLogin, r.Notice ?? "Cần đăng nhập.");
        if (r.NeedsSetup)
            return Fail(Phase.NeedsSetup, r.Notice ?? "Cần hoàn tất thiết lập trong " + Tools.ClientName(id) + ".");
        if (r.Provider == id && !r.ScopeUnverified && r.Valid(id) && AccountScope.From(r) != null &&
            r.Time <= now + 5)
            return Ok(r);
        return Fail(Phase.Error, r.Notice ?? "Nguồn chưa trả hạn mức của một tài khoản đã xác minh.");
    }
}

internal sealed class ProviderState
{
    public Phase Phase = Phase.Off;
    public AccountScope Account;
    public Reading Snapshot;
    public string Message;
    public bool Busy;
    public int Failures;
    public long NextRead, RetryAt, LastAttempt;

    public ProviderState Copy()
    {
        return (ProviderState)MemberwiseClone();
    }
    // The tray and widget mark every number that is not from a recent successful read.
    public bool Fresh
    {
        get { return Phase == Phase.Live && ShowsNumbers; }
    }
    // Numbers stay visible while they can still be trusted to describe the current account.
    public bool ShowsNumbers
    {
        get { return Snapshot != null && Snapshot.Time >= Fmt.Now - Reducer.KeepNumbersSeconds &&
                     (Phase == Phase.Live || Phase == Phase.Stale || Phase == Phase.Connecting ||
                      Phase == Phase.Cooldown || Phase == Phase.NeedsSetup); }
    }
}

internal static class Reducer
{
    // A failed read keeps the last numbers (marked "~") while they are younger than this.
    public const int KeepNumbersSeconds = 600;
    public const int MaxBackoffSeconds = 900;

    public static ProviderState Start(AccountScope account, Reading last, long cooldownUntil, long now)
    {
        var s = new ProviderState { Phase = Phase.Connecting, Account = account, Snapshot = last,
                                    NextRead = Math.Max(now, cooldownUntil), RetryAt = cooldownUntil };
        if (cooldownUntil > now)
        {
            s.Phase = Phase.Cooldown;
            s.Message = "Dịch vụ yêu cầu chờ. Đọc lại từ " + Fmt.Local(cooldownUntil, "dd/MM HH:mm:ss") + ".";
        }
        return s;
    }

    public static ProviderState Stop()
    {
        return new ProviderState();
    }

    public static ProviderState Begin(ProviderState state, long now)
    {
        var s = state.Copy();
        s.Busy = true;
        s.LastAttempt = now;
        return s;
    }

    // A manual "Đọc lại" lifts the user-action pauses but never a service cooldown.
    public static ProviderState Retry(ProviderState state, long now)
    {
        var s = state.Copy();
        s.NextRead = Math.Max(now, s.RetryAt);
        if (s.Phase == Phase.NeedsSetup || s.Phase == Phase.NeedsLogin || s.Phase == Phase.NeedsInstall)
            s.Failures = 0;
        return s;
    }

    // Switch to another verified account, e.g. after Codex reports a new login.
    public static ProviderState Switch(ProviderState state, AccountScope account, Reading last)
    {
        var s = state.Copy();
        s.Account = account;
        s.Snapshot = last;
        if (s.Phase == Phase.Live || s.Phase == Phase.Stale)
            s.Phase = Phase.Connecting;
        return s;
    }

    public static ProviderState Forget(ProviderState state, string message)
    {
        var s = state.Copy();
        s.Account = null;
        s.Snapshot = null;
        if (s.Phase != Phase.Off)
            s.Phase = Phase.Connecting;
        s.Message = message;
        return s;
    }

    public static ProviderState Complete(ProviderState state, ReadResult result, long now, int interval)
    {
        var s = state.Copy();
        s.Busy = false;
        if (s.Phase == Phase.Off)
            return s;
        if (result.VerifiedAccount != null && !result.VerifiedAccount.Matches(s.Snapshot))
        {
            s.Account = result.VerifiedAccount;
            s.Snapshot = null;
        }
        switch (result.Phase)
        {
        case Phase.Live:
        {
            var r = result.Reading;
            var scope = AccountScope.From(r);
            if (s.Account == null || !s.Account.Matches(r))
            {
                // A different verified account replaces the old numbers instead of mixing with them.
                s.Account = scope;
                s.Snapshot = r;
            }
            else if (s.Snapshot == null || r.Time >= s.Snapshot.Time)
                s.Snapshot = r;
            s.Phase = Phase.Live;
            s.Message = null;
            s.Failures = 0;
            s.RetryAt = 0;
            s.NextRead = now + interval;
            return s;
        }
        case Phase.Cooldown:
            s.Phase = Phase.Cooldown;
            s.RetryAt = Math.Max(result.RetryAt, now + 30);
            s.NextRead = s.RetryAt;
            s.Message = result.Message + " Đọc lại từ " + Fmt.Local(s.RetryAt, "dd/MM HH:mm:ss") + ".";
            return s;
        case Phase.NeedsInstall:
        case Phase.NeedsLogin:
            // These numbers can no longer be attributed to a signed-in account.
            s.Phase = result.Phase;
            s.Account = null;
            s.Snapshot = null;
            s.Message = result.Message;
            s.RetryAt = Math.Max(s.RetryAt, result.RetryAt);
            // Check again later in case the user installs or signs in from a terminal.
            s.NextRead = Math.Max(s.RetryAt, now + (result.NextIn > 0 ? result.NextIn
                                                     : result.Phase == Phase.NeedsInstall ? 60 : 300));
            return s;
        case Phase.NeedsSetup:
            s.Phase = Phase.NeedsSetup;
            s.Message = result.Message;
            s.NextRead = Int64.MaxValue;
            return s;
        default:
        {
            s.Message = result.Message;
            if (result.NextIn > 0)
                s.NextRead = now + result.NextIn;
            else
            {
                s.Failures = Math.Min(8, s.Failures + 1);
                long backoff = Math.Min(MaxBackoffSeconds, (long)Math.Max(30, interval) << Math.Min(5, s.Failures - 1));
                s.NextRead = now + backoff;
            }
            s.Phase = KeepsNumbers(s, now) ? Phase.Stale : Phase.Error;
            return s;
        }
        }
    }

    // Live numbers become "~" once two reads are overdue.
    public static ProviderState Age(ProviderState state, long now, int interval)
    {
        if (state.Phase != Phase.Live || state.Snapshot == null || state.Busy ||
            now - state.Snapshot.Time <= 2 * interval + 15)
            return state;
        var s = state.Copy();
        s.Phase = Phase.Stale;
        return s;
    }

    static bool KeepsNumbers(ProviderState s, long now)
    {
        return s.Snapshot != null && now - s.Snapshot.Time <= KeepNumbersSeconds;
    }
}
}
