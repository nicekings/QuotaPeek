using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace NhipPC
{
public sealed class ReadGuard
{
    public long UntilUtc { get; set; }
    static readonly object sync = new object();
    static readonly Dictionary<string, long> holds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    internal static long Until(string provider)
    {
        string path = Store.PathFor("cooldown-" + provider);
        lock (sync)
        {
            long held;
            holds.TryGetValue(path, out held);
            var saved = Store.Read<ReadGuard>(path);
            return Math.Max(held, saved == null ? 0 : saved.UntilUtc);
        }
    }
    internal static void Delay(string provider, long unixDeadline)
    {
        lock (sync)
        {
            if (unixDeadline <= Until(provider))
                return;
            string path = Store.PathFor("cooldown-" + provider);
            holds[path] = unixDeadline;
            try
            {
                Store.Write(path, Json.Encode(new ReadGuard { UntilUtc = unixDeadline }));
            }
            catch (IOException) {}
            catch (UnauthorizedAccessException) {}
        }
    }
}

// Account identifiers are hashed before a reading leaves its collector.
public sealed class AccountScope
{
    public string Provider { get; set; }
    public string AccountKey { get; set; }
    public string Plan { get; set; }

    static string Digest(params string[] parts)
    {
        var input = new StringBuilder();
        foreach (string part in parts)
            input.Append(part.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(part);
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(input.ToString()))).Replace("-", "").ToLowerInvariant();
    }
    public static string Key(string provider, string kind, string identifier, string workspace = null)
    {
        if (!Catalog.Providers.Contains(provider) || String.IsNullOrWhiteSpace(kind) ||
            String.IsNullOrWhiteSpace(identifier))
            return null;
        identifier = identifier.Trim();
        if (kind == "email")
            identifier = identifier.ToLowerInvariant();
        return Digest(provider, kind, identifier, (workspace ?? "").Trim());
    }
    internal static AccountScope From(Reading r)
    {
        if (r == null)
            return null;
        var profile = new AccountScope { Provider = r.Provider, AccountKey = r.AccountKey, Plan = (r.Plan ?? "").Trim() };
        return profile.Valid(r.Provider) ? profile : null;
    }
    internal bool Valid(string provider)
    {
        return Provider == provider && Catalog.Providers.Contains(provider) &&
               AccountKey != null && AccountKey.Length == 64 &&
               AccountKey.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')) &&
               !String.IsNullOrWhiteSpace(Plan) && Plan.Length <= 100 &&
               !new[] { "unknown", "n/a", "none", "unspecified", "null", "-" }.Contains(Plan.Trim(), StringComparer.OrdinalIgnoreCase);
    }
    internal bool Matches(Reading r)
    {
        return r != null && Provider == r.Provider && AccountKey == r.AccountKey &&
               String.Equals(Plan, (r.Plan ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
    internal string Name(string prefix)
    {
        return prefix + Provider + "-" + Digest(AccountKey, Plan.Trim().ToUpperInvariant());
    }
    internal static AccountScope Load(string provider)
    {
        var profile = Store.Read<AccountScope>(Store.PathFor("active-" + provider));
        return profile != null && profile.Valid(provider) ? profile : null;
    }
    internal Reading Last()
    {
        var last = Store.Read<Reading>(Store.PathFor(Name("last-")));
        return Matches(last) && last.Valid(Provider) ? last : null;
    }
}
}
