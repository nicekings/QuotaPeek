using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NhipPC
{
// Only call for metadata in a provider-owned, scoped usage panel. Identity is never inferred
// from a bare address, tier, title, billing notice or a surrounding conversation.
internal static class AccountPanel
{
    public static void Bind(Reading r, IEnumerable<string> fields)
    {
        if (r.Windows.Count == 0)
            return;
        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaces = new HashSet<string>(StringComparer.Ordinal);
        bool managed = false;
        foreach (var field in fields)
        {
            int colon = field.IndexOf(':');
            if (colon < 1)
            {
                if (IsWorkspaceLabel(Fmt.Fold(field)))
                    managed = true;
                continue;
            }
            if (field.Contains("\n") || field.Contains("\r"))
                continue;
            string label = Fmt.Fold(field.Substring(0, colon));
            string value = field.Substring(colon + 1).Trim();
            // Native form labels may be exposed separately with their trailing colon.
            if (value.Length == 0)
            {
                if (IsWorkspaceLabel(label))
                    managed = true;
                continue;
            }
            if (Regex.IsMatch(label, @"^(?:account|current account|account email|email|signed in as|tai khoan|email tai khoan)$"))
            {
                if (value.Length > 254 || !Regex.IsMatch(value, @"^[a-zA-Z0-9._+'%-]+@[a-zA-Z0-9.-]+\.[a-zA-Z0-9-]+$"))
                    return;
                emails.Add(value);
            }
            else if (Regex.IsMatch(label, @"^(?:plan|current plan|subscription plan|goi|goi hien tai|goi dang ky)$"))
            {
                if (!Regex.IsMatch(value, @"^[\p{L}\d][\p{L}\d ._+()/-]{0,63}$"))
                    return;
                plans.Add(value);
            }
            else if (Regex.IsMatch(label, @"^(?:organization id|organisation id|workspace id|id to chuc|id khong gian lam viec)$"))
            {
                if (!Regex.IsMatch(value, @"^[a-zA-Z0-9_-]{1,128}$"))
                    return;
                workspaces.Add(value);
            }
            else if (IsWorkspaceLabel(label))
                managed = true;
        }
        if (emails.Count != 1 || plans.Count != 1 || workspaces.Count > 1)
            return;
        string plan = plans.Single();
        if (workspaces.Count == 0 && (managed || Regex.IsMatch(Fmt.Fold(plan),
            @"\b(team|enterprise|business|workspace|organization|education|school|doanh nghiep|to chuc)\b")))
            return;
        r.AccountKey = AccountScope.Key(r.Provider, "email", emails.Single(), workspaces.FirstOrDefault());
        r.Plan = plan;
    }
    static bool IsWorkspaceLabel(string label)
    {
        return Regex.IsMatch(label, @"^(?:organization|organisation|workspace|to chuc|khong gian lam viec)$");
    }
}
}
