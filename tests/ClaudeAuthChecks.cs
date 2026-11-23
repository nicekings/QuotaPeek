using System;

namespace NhipPC
{
internal static partial class Checks
{
    static void ClaudeAuthStatus()
    {
        Func<string, int, Reading> status = (json, code) => {
            var r = Reading.Start("claude", "Claude Code · nền");
            CliIdentity.ReadClaudeStatus(r, json, code);
            return r;
        };
        string subscription = Json.Encode(new { loggedIn = true, authMethod = "claude.ai",
            email = "viewer@example.test", subscriptionType = "max", orgId = "personal-org" });
        var bound = status(subscription, 0);
        Check(AccountScope.From(bound) != null && !bound.ScopeUnverified && !bound.NeedsLogin &&
              bound.AccountKey == AccountScope.Key("claude", "email", "viewer@example.test", "personal-org"),
              "Only a successful Claude subscription login establishes the owner");
        foreach (var method in new[] { "api_key", "api_key_helper", "console", "third_party", "oauth_token" })
        {
            var r = status(Json.Encode(new { loggedIn = true, authMethod = method,
                email = "viewer@example.test", subscriptionType = "max" }), 0);
            Check(r.NeedsLogin && r.ScopeUnverified && r.AccountKey == null && r.Plan == null &&
                  r.Notice.Contains("thuê bao") && (method != "api_key" || r.Notice.Contains("API")),
                  "Claude auth mode " + method + " requires a subscription connection");
        }
        var missing = status(Json.Encode(new { loggedIn = true, authMethod = "claude.ai",
            email = "viewer@example.test" }), 0);
        Check(!missing.NeedsLogin && missing.ScopeUnverified && missing.AccountKey == null &&
              missing.Notice.Contains("đã đăng nhập") && missing.Notice.Contains("chưa báo tên gói"),
              "A signed-in account missing its plan remains unbound without asking to sign in again");
        var failed = status(subscription, 1);
        var loggedOut = status("{\"loggedIn\":false}", 1);
        var malformed = status("unavailable", 0);
        Check(failed.ScopeUnverified && failed.AccountKey == null && loggedOut.NeedsLogin &&
              loggedOut.ScopeUnverified && malformed.ScopeUnverified && !malformed.NeedsLogin,
              "An unsuccessful or unreadable auth report cannot bind a stale credential");
        var verification = status("Verify that you are human", 0);
        Check(verification.NeedsLogin && verification.ScopeUnverified && verification.Notice.Contains("xác minh"),
              "Claude verification prompt requests user action without reading quotas");
        var throttled = status("Too many requests. Verify your identity.\nRetry-After: 90", 0);
        Check(throttled.RateLimited && !throttled.NeedsLogin && throttled.RetryAfterSeconds == 90,
              "A throttle takes priority over sign-in prompts and preserves Retry-After");
        var errorReply = status(Json.Encode(new { loggedIn = false, error = new { message = "HTTP 429 Too many requests" } }), 0);
        Check(errorReply.RateLimited && !errorReply.NeedsLogin, "A throttled auth report with loggedIn=false never requests login");
    }
}
}
