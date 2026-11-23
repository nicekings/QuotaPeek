using System;
using System.IO;
using System.Linq;

namespace NhipPC
{
internal static partial class Checks
{
    static void ProviderRefreshChecks()
    {
        string parent = Store.Root;
        Environment.SetEnvironmentVariable("QUOTAPEEK_DATA_DIR", Path.Combine(parent, "pf"));
        try
        {
            long now = Fmt.Now;
            var p = new Preferences();
            foreach (string id in Catalog.Providers)
                p.Enabled[id] = id == "claude";
            var first = Sample("first@example.test", "pro", 0, now);
            var second = Sample("second@example.test", "pro", 0, now);
            using (var hub = new UsageHub(p, action => action()))
            {
                hub.Complete("claude", ReadResult.Ok(first));
                hub.Complete("claude", ReadResult.Ok(second));
                var saved = AccountScope.Load("claude");
                Check(saved.Matches(second) && saved.Last() != null && saved.Matches(saved.Last()),
                      "An account switch persists its own first sample even when all quotas match");

                hub["claude"].Busy = true;
                hub["claude"].LastAttempt = now;
                Store.Write(Store.PathFor("bell-claude"), "{}");
                hub.CheckBells(now);
                hub.Complete("claude", ReadResult.Ok(second));
                hub.CheckBells(now + 2);
                Check(hub["claude"].NextRead == now + 20,
                      "A status-line event during a read schedules another read after the spacing interval");

                var newOwner = Sample("third@example.test", "pro", 0, now);
                newOwner.Windows.Clear();
                hub.Complete("claude", ReadResult.From("claude", newOwner, now));
                Check(hub["claude"].Snapshot == null && AccountScope.Load("claude").Matches(newOwner),
                      "Confirming another account without new quotas clears the previous account's numbers");

                var spent = Reading.Start("claude", ClaudeUsage.Source);
                ClaudeUsage.Parse(spent, @"{""type"":""result"",""num_turns"":1}");
                hub.Complete("claude", ReadResult.From("claude", spent, now));
            }
            using (var hub = new UsageHub(p, action => action()))
            {
                Check(hub["claude"].Phase == Phase.NeedsSetup && hub["claude"].NextRead == Int64.MaxValue,
                      "Claude stays paused after restart if /usage reached the model");
                hub["claude"].Busy = true; // Inspect scheduling without starting a real CLI.
                hub.Refresh(null, userInitiated: false);
                Check(hub["claude"].NextRead == Int64.MaxValue, "Waking Windows preserves the Claude model-turn pause");
                hub.Refresh("claude");
                Check(hub["claude"].NextRead <= Fmt.Now, "An explicit Claude refresh lifts the model-turn pause");
                hub.Complete("claude", ReadResult.Fail(Phase.NeedsSetup, "test"));
                hub["claude"].Busy = true;
                hub.Refresh(null);
                Check(hub["claude"].NextRead <= Fmt.Now, "Dashboard F5 also lifts the model-turn pause");
            }
            using (var hub = new UsageHub(p, action => action()))
                Check(hub["claude"].Phase == Phase.Connecting, "The explicit retry clears the saved pause");

            var old = Sample("old@example.test", "pro", 25, now - Reducer.KeepNumbersSeconds - 1);
            var state = Reducer.Start(AccountScope.From(old), old, now + 900, now);
            foreach (Phase phase in new[] { Phase.Cooldown, Phase.Connecting, Phase.NeedsSetup, Phase.Stale })
            {
                state.Phase = phase;
                var choice = DisplayData.Choices(new HardwareFrame(), id => id == "claude" ? state : null)
                                        .Single(c => c.Key == "ai/claude/seven_day");
                Check(state.Phase == phase && !state.ShowsNumbers && choice.Number == "—" && choice.Percent == null &&
                      choice.Tip.Contains("75%"), "Old quota is restricted to the tooltip in " + phase);
            }

            foreach (Phase phase in new[] { Phase.Error, Phase.NeedsSetup, Phase.Cooldown })
            {
                Environment.SetEnvironmentVariable("QUOTAPEEK_DATA_DIR", Path.Combine(parent, "pf-" + phase));
                var missingQuota = Sample("second@example.test", "pro", 0, now);
                missingQuota.Windows.Clear();
                missingQuota.NeedsSetup = phase == Phase.NeedsSetup;
                missingQuota.RateLimited = phase == Phase.Cooldown;
                using (var hub = new UsageHub(p, action => action()))
                {
                    hub.Complete("claude", ReadResult.Ok(first));
                    hub.Complete("claude", ReadResult.From("claude", missingQuota, now));
                    Check(hub["claude"].Phase == phase && hub["claude"].Snapshot == null,
                          "New verified identity clears the other account's quotas on " + phase);
                }
                using (var hub = new UsageHub(p, action => action()))
                    Check(hub["claude"].Account.Matches(missingQuota) && hub["claude"].Snapshot == null,
                          "Restart retains the new active account after " + phase);
            }
        }
        finally { Environment.SetEnvironmentVariable("QUOTAPEEK_DATA_DIR", parent); }
    }
}
}
