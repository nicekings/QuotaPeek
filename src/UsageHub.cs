using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NhipPC
{
// Owns one ProviderState per service. Sources report results; Reducer decides the next state.
// Everything here runs on the UI thread: background work posts its result back through `post`.
internal sealed class UsageHub : IDisposable
{
    readonly Dictionary<string, ProviderState> states = new Dictionary<string, ProviderState>();
    readonly Dictionary<string, CancellationTokenSource> workers = new Dictionary<string, CancellationTokenSource>();
    readonly Dictionary<string, int> generations = new Dictionary<string, int>();
    readonly Dictionary<string, long> saved = new Dictionary<string, long>();
    readonly Dictionary<string, DateTime> bells = new Dictionary<string, DateTime>();
    readonly Action<Action> post;
    CodexFeed codex;
    long nextBellCheck;
    bool closed;
    public event Action Changed;

    // Seconds between automatic reads. Codex paces its own app-server session.
    public static int Interval(string id)
    {
        switch (id)
        {
        case "codex":
        case "gemini":
            return 30;
        case "grok":
            return 180;
        default:
            return 120;
        }
    }
    // Status line events from an open CLI session wake an early read, at most this often.
    const int BellSpacing = 20;

    public UsageHub(Preferences p, Action<Action> post)
    {
        this.post = post;
        Directory.CreateDirectory(Store.Root);
        Store.RemoveLegacy();
        foreach (string id in Catalog.Providers)
        {
            states[id] = Reducer.Stop();
            generations[id] = 0;
        }
        Apply(p);
    }
    public ProviderState this[string id]
    {
        get { return states[id]; }
    }
    void Signal()
    {
        if (Changed != null)
            Changed();
    }
    void Set(string id, ProviderState next)
    {
        states[id] = next;
        Signal();
    }

    public void Apply(Preferences p)
    {
        long now = Fmt.Now;
        foreach (string id in Catalog.Providers)
        {
            bool on = p.On(id);
            if (on == (states[id].Phase != Phase.Off))
                continue;
            generations[id]++;
            CancellationTokenSource worker;
            if (workers.TryGetValue(id, out worker))
            {
                worker.Cancel();
                workers.Remove(id);
            }
            if (id == "codex" && codex != null)
            {
                codex.Dispose();
                codex = null;
            }
            if (!on)
            {
                states[id] = Reducer.Stop();
                continue;
            }
            var account = AccountScope.Load(id);
            states[id] = Reducer.Start(account, account == null ? null : account.Last(), ReadGuard.Until(id), now);
            bells[id] = BellTime(id);
            if (id == "codex")
                StartCodex();
        }
        Signal();
    }

    bool Current(string id, int generation)
    {
        return !closed && generations[id] == generation && states[id].Phase != Phase.Off;
    }

    void StartCodex()
    {
        int gen = generations["codex"];
        var s = states["codex"];
        s.Busy = true;
        if (s.Phase == Phase.Connecting)
            s.Message = "Đang kết nối Codex…";
        codex = new CodexFeed(
            (r, delta) => post(delegate {
                if (Current("codex", gen))
                    Complete("codex", ReadResult.From("codex", r, Fmt.Now));
            }),
            (phase, message, clear) => post(delegate {
                if (!Current("codex", gen))
                    return;
                if (clear)
                    Forget("codex", message);
                if (phase == Phase.Connecting)
                {
                    var next = states["codex"].Copy();
                    next.Busy = true;
                    next.Message = message;
                    Set("codex", next);
                    return;
                }
                Complete("codex", ReadResult.Fail(phase, message, ReadGuard.Until("codex")));
            }),
            (chat, clear) => post(delegate {
                if (!Current("codex", gen))
                    return;
                if (clear)
                    Forget("codex", null);
                var next = states["codex"].Copy();
                next.Busy = true;
                next.Message = "Đang đọc lại tài khoản Codex…";
                Set("codex", next);
            }),
            identity => post(delegate {
                if (!Current("codex", gen))
                    return;
                var scope = AccountScope.From(identity);
                var s2 = states["codex"];
                if (scope != null && (s2.Account == null || !s2.Account.Matches(identity)))
                {
                    SaveSelection("codex", scope);
                    Set("codex", Reducer.Switch(s2, scope, scope.Last()));
                }
            }));
    }

    void Forget(string id, string message)
    {
        SaveSelection(id, null);
        states[id] = Reducer.Forget(states[id], message);
    }

    static bool Same(AccountScope a, AccountScope b)
    {
        return a == null ? b == null
                         : b != null && a.Provider == b.Provider && a.AccountKey == b.AccountKey &&
                               String.Equals(a.Plan, b.Plan, StringComparison.OrdinalIgnoreCase);
    }
    static void SaveSelection(string id, AccountScope profile)
    {
        try { Store.Write(Store.PathFor("active-" + id), Json.Encode(profile)); }
        catch (IOException) {}
        catch (UnauthorizedAccessException) {}
    }

    // The single entry point for a finished read, from any source.
    internal void Complete(string id, ReadResult result)
    {
        long now = Fmt.Now;
        var before = states[id];
        var next = Reducer.Complete(before, result, now, Interval(id));
        if (result.Phase == Phase.Cooldown && next.RetryAt > now && id != "codex")
            ReadGuard.Delay(id, next.RetryAt);
        if (!Same(before.Account, next.Account))
            SaveSelection(id, next.Account);
        if (result.Phase == Phase.Live)
            Persist(id, before.Snapshot, next.Snapshot, now);
        Set(id, next);
    }

    void Persist(string id, Reading previous, Reading current, long now)
    {
        long last;
        saved.TryGetValue(id, out last);
        bool different = previous == null || previous.Source != current.Source ||
                         Json.Encode(previous.Windows) != Json.Encode(current.Windows);
        if (!different && now - last < 30)
            return;
        try
        {
            Store.SaveReading(current);
            saved[id] = now;
        }
        catch (IOException) {}
        catch (UnauthorizedAccessException) {}
    }

    // "Đọc lại": lift user-action pauses, keep service cooldowns, read as soon as allowed.
    public void Refresh(string id)
    {
        if (closed)
            return;
        long now = Fmt.Now;
        foreach (string key in Catalog.Providers)
        {
            if ((id != null && key != id) || states[key].Phase == Phase.Off)
                continue;
            if (key == "codex")
            {
                if (codex != null && codex.Refresh())
                {
                    var next = states[key].Copy();
                    next.Busy = true;
                    next.Message = "Đang đọc lại hạn mức Codex…";
                    states[key] = next;
                }
                continue;
            }
            states[key] = Reducer.Retry(states[key], now);
        }
        Tick();
        Signal();
    }

    public void Tick()
    {
        if (closed)
            return;
        long now = Fmt.Now;
        if (now >= nextBellCheck)
        {
            nextBellCheck = now + 2;
            CheckBells(now);
        }
        foreach (string id in Catalog.Providers)
        {
            var s = Reducer.Age(states[id], now, Interval(id));
            if (s != states[id])
                Set(id, s);
            if (id == "codex" || s.Phase == Phase.Off || s.Busy || workers.ContainsKey(id) ||
                now < s.NextRead || now < s.RetryAt)
                continue;
            if (id == "gemini" && DesktopUsage.Windows(id).Count == 0)
            {
                // Checking for the window is cheap; only start UI Automation when it is open.
                Complete(id, new ReadResult { Phase = Phase.Error, Message = DesktopUsage.NoWindowNotice(id), NextIn = 10 });
                continue;
            }
            StartRead(id);
        }
    }

    static DateTime BellTime(string id)
    {
        try { return File.GetLastWriteTimeUtc(Store.PathFor("bell-" + id)); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    internal void CheckBells(long now)
    {
        foreach (string id in new[] { "claude", "antigravity" })
        {
            var s = states[id];
            if (s.Phase == Phase.Off)
                continue;
            var time = BellTime(id);
            DateTime last;
            bells.TryGetValue(id, out last);
            if (time <= last)
                continue;
            bells[id] = time;
            // A message in an open session changes usage. Paused states still wait for the user.
            if (s.Phase == Phase.NeedsSetup || s.Phase == Phase.NeedsLogin || s.Phase == Phase.NeedsInstall ||
                s.Phase == Phase.Cooldown)
                continue;
            var next = s.Copy();
            next.NextRead = Math.Min(next.NextRead, Math.Max(now, s.LastAttempt + BellSpacing));
            states[id] = next;
        }
    }

    void StartRead(string id)
    {
        int gen = generations[id];
        var worker = new CancellationTokenSource();
        workers[id] = worker;
        var token = worker.Token;
        Set(id, Reducer.Begin(states[id], Fmt.Now));
        Task.Run(delegate {
            Reading r = null;
            try { r = ReadWorker(id, token); }
            catch (Exception) {}
            post(delegate {
                CancellationTokenSource current;
                if (workers.TryGetValue(id, out current) && current == worker)
                    workers.Remove(id);
                worker.Dispose();
                if (Current(id, gen))
                    Complete(id, ReadResult.From(id, r, Fmt.Now));
            });
        });
    }

    // Collectors run in a child process: a hung CLI or UI Automation call can be killed safely.
    static Reading ReadWorker(string id, CancellationToken token)
    {
        bool window = id == "gemini";
        using (var p = Process.Start(Tools.Redirected(Tools.Self, (window ? "--read-window " : "--read-cli ") + id)))
        {
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            p.StandardInput.Close();
            using (token.Register(delegate { Tools.Kill(p); }))
            {
                if (!p.WaitForExit(window ? 8000 : 50000))
                {
                    Tools.Kill(p);
                    return null;
                }
                if (p.ExitCode != 0 || !Task.WaitAll(new Task[] { output, error }, 700))
                    return null;
                return Json.Decode<Reading>(output.Result);
            }
        }
    }

    public void Dispose()
    {
        closed = true;
        foreach (var worker in workers.Values)
            worker.Cancel();
        workers.Clear();
        if (codex != null)
            codex.Dispose();
    }
}
}
