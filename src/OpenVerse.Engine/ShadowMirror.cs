using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace OpenVerse.Engine;

// one headless copy of the client's engine. the engine hangs the current battle off process-wide statics, so each
// mirror owns its load context and its own worker thread, and two of them are two genuinely separate boards.
// net48, reached by reflection (EngineHost is not referenceable from net10)
public sealed class ShadowMirror : IDisposable
{
    const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;

    // 250ms was enough for the second question and not for the first: the engine's first call in a fresh context JITs
    // its way through the receive path, and on a busy machine that overran, so the relay silently priced the card off
    // its printed cost. the client waits 95s before giving up on a battle, so this is cheap
    public const int AnswerBudgetMs = 5_000;

    public string Name { get; }

    // held, not discarded: the context is collectible, and letting it go unreferenced has the GC unload the engine out
    // from under a live match ("AssemblyLoadContext is unloading or was already unloaded")
    EngineContext? _ctx;
    Type? _host;
    MethodInfo? _boot, _create, _ingest, _verdict, _state, _close, _costOf, _cardIdOf, _answerConditions, _setDeckMirror;
    MethodInfo? _project, _cursor, _lastVerdict, _playByIntent;
    // unbounded: a dropped job is a hole in the board, and the board is what every later answer is read off. the old
    // 256-slot cap turned a slow boot or a busy machine into a silently wrong cost
    readonly BlockingCollection<Action> _work = new();
    Thread? _worker;
    int _inFlight;
    int _handle = -1;

    public bool Ready { get; private set; }
    public string? Failure { get; private set; }
    public int Dropped { get; private set; }
    public int Timeouts { get; private set; }
    public bool Live => _handle > 0;

    public ShadowMirror(string name) => Name = name;

    public bool Init(string cardMasterCsv)
    {
        if (Ready) return true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll");
            if (!File.Exists(path)) { Failure = "OpenVerse.EngineHost.dll not present"; return false; }

            _ctx = new EngineContext(Name);
            _host = _ctx.LoadFromAssemblyPath(path).GetType("OpenVerse.EngineHost.ShadowMatch");
            if (_host is null) { Failure = "ShadowMatch not found in the host"; return false; }

            _boot = _host.GetMethod("Boot", Pub);
            _create = _host.GetMethod("Create", Pub);
            _ingest = _host.GetMethod("Ingest", Pub);
            _verdict = _host.GetMethod("Verdict", Pub);
            _state = _host.GetMethod("State", Pub);
            _close = _host.GetMethod("Close", Pub);
            // optional surface: looked up but not required, null-checked at each call site, so an older host DLL loses
            // one answer instead of disabling the mirror
            _costOf = _host.GetMethod("CostOf", Pub);
            _cardIdOf = _host.GetMethod("CardIdOf", Pub);
            _answerConditions = _host.GetMethod("AnswerConditions", Pub);
            _setDeckMirror = _host.GetMethod("SetDeckMirror", Pub);
            _project = _host.GetMethod("Project", Pub);
            _cursor = _host.GetMethod("RandomCursor", Pub);
            _lastVerdict = _host.GetMethod("LastVerdict", Pub);
            _playByIntent = _host.GetMethod("PlayByIntent", Pub);
            if (_boot is null || _create is null || _ingest is null || _verdict is null || _state is null || _close is null)
            {
                Failure = "the host is missing part of its surface";
                return false;
            }

            if (_boot.Invoke(null, [cardMasterCsv]) is not true) { Failure = HostError() ?? "boot failed"; return false; }

            _worker = new Thread(Drain) { IsBackground = true, Name = $"engine-{Name}" };
            _worker.Start();
            Ready = true;
            return true;
        }
        catch (Exception e) { Failure = e.InnerException?.Message ?? e.Message; return false; }
    }

    string? HostError() => _host?.GetProperty("LastError", Pub)?.GetValue(null) as string;

    void Drain()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try { job(); } catch { /* an observation is never worth taking the server down for */ }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    bool Post(Action job)
    {
        if (!Ready) return false;
        Interlocked.Increment(ref _inFlight);
        if (_work.IsAddingCompleted is false && _work.TryAdd(job)) return true;
        Interlocked.Decrement(ref _inFlight);
        // unreachable while the queue is unbounded and open, and worth a line if it ever stops being either
        Dropped++;
        Console.WriteLine($"[{Name}] DROPPED a job ({Dropped} so far) - the board is now incomplete");
        return false;
    }

    // tests and shutdown only; the relay never blocks on the worker
    public bool WaitIdle(int timeoutMs = 120_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    // playerHand/enemyHand are the post-mulligan opening-hand indices: without them the board never leaves the deck
    public void Begin(int seed, bool playerFirst, int[] playerDeck, int[] enemyDeck,
                      int[] playerHand, int[] enemyHand, Action<string> log,
                      int selfIdxSeed = -1, int oppoIdxSeed = -1)
    {
        Post(() =>
        {
            // returning here instead would leave the previous match answering this one's questions off its own board,
            // and Create's own "already running" guard never gets to fire, so nothing is logged at all
            if (_handle > 0)
            {
                log($"[{Name}] match {_handle} was never closed, evicting it");
                _close!.Invoke(null, [_handle]);
                _handle = -1;
            }
            _handle = (int)_create!.Invoke(null, [seed, playerFirst, playerDeck, enemyDeck, playerHand, enemyHand])!;
            log(_handle > 0
                ? $"[{Name}] observing, {(playerFirst ? "player" : "peer")} first"
                : $"[{Name}] not started ({HostError()})");
            if (_handle > 0 && _setDeckMirror is not null && selfIdxSeed != -1
                && _setDeckMirror.Invoke(null, [_handle, selfIdxSeed, oppoIdxSeed]) is not true)
                log($"[{Name}] deck mirror not installed ({HostError()})");
        });
    }

    // the relay adds these for the real peer's placeholder model; a mirror re-simulates from full information and needs
    // none. knownList walks ReplaceReceivedCard.CreateActualCard, which NREs headless (no card view)
    static readonly string[] PeerOnlyFields = ["knownList"];

    public void Observe(string uri, JsonObject body, bool isPlayer, Action<string> log)
    {
        if (!Ready) return;
        // copied on the relay thread: it will not keep the node alive for the worker
        var flat = Flatten(body);
        foreach (var f in PeerOnlyFields) flat.Remove(f);
        // a cardId on a uList entry takes the same CreateActualCard path knownList did, so drop it to the CardId==0
        // branch (the entry itself stays, since it drives random resolution)
        if (flat.TryGetValue("uList", out var ul) && ul is List<object?> entries)
            foreach (var e in entries)
                if (e is Dictionary<string, object?> d) d.Remove("cardId");
        Post(() =>
        {
            if (_handle <= 0) return;
            var why = (string)_ingest!.Invoke(null, [_handle, uri, flat, isPlayer])!;
            if (why.Length > 0) log($"[{Name}] {uri} not applied - {why}");
        });
    }

    // this board's own play, driven the way its client drove it. false means it could not be, and the caller should
    // fall back to the receive path rather than leave the board without the play
    public bool TryPlayByIntent(int idx, List<object> targets, List<object> choices, out string why,
                                int timeoutMs = AnswerBudgetMs)
    {
        why = "no engine";
        if (!Ready || _handle <= 0 || _playByIntent is null) return false;
        var ok = Ask(_playByIntent, [_handle, idx, targets, choices], o => o as string ?? "", out why!, "",
                     timeoutMs, "intent play unanswered");
        why ??= "";
        return ok && why.Length == 0;
    }

    bool Ask<T>(MethodInfo? m, object?[] args, Func<object?, T> read, out T value, T missing, int timeoutMs, string what)
    {
        value = missing;
        if (!Ready || _handle <= 0 || m is null) return false;
        var answer = missing;
        using var done = new ManualResetEventSlim(false);
        if (!Post(() =>
        {
            try { answer = read(m.Invoke(null, args)); }
            finally { done.Set(); }
        })) return false;
        if (!done.Wait(timeoutMs))
        {
            Timeouts++;
            Console.WriteLine($"[{Name}] TIMED OUT after {timeoutMs}ms ({Timeouts} so far) - {what}");
            return false;
        }
        value = answer;
        return true;
    }

    public bool TryCostOf(bool isSelfPlayer, int idx, out int cost, int timeoutMs = AnswerBudgetMs) =>
        Ask(_costOf, [_handle, isSelfPlayer, idx], o => (int)o!, out cost, -1, timeoutMs,
            "falling back to a value the engine did not supply") && cost >= 0;

    // which card sits at an index, any zone, so one query replaces a per-route reconstruction. 0 = unknown
    public bool TryCardIdOf(bool isSelfPlayer, int idx, out int cardId, int timeoutMs = AnswerBudgetMs) =>
        Ask(_cardIdOf, [_handle, isSelfPlayer, idx], o => (int)o!, out cardId, 0, timeoutMs,
            "falling back to a value the engine did not supply") && cardId != 0;

    // answers the actor's skill-condition queries as ready-made knownList entries; a spec it cannot evaluate has no row
    public bool TryConditionAnswers(bool isSelfPlayer, int cardIdx, JsonArray specs, out JsonArray entries,
                                    int timeoutMs = AnswerBudgetMs)
    {
        entries = new JsonArray();
        if (!Ready || _handle <= 0 || _answerConditions is null || specs.Count == 0) return false;
        // flattened here, on the relay thread: the worker must not touch nodes the relay still owns
        var flat = specs.Select(n => (object?)(n is JsonObject o ? Flatten(o) : null)).Where(x => x is not null).ToList();
        if (flat.Count == 0) return false;

        if (!Ask(_answerConditions, [_handle, isSelfPlayer, cardIdx, flat], o => o as List<object>,
                 out var rows, null, timeoutMs, "conditions unanswered") || rows is null)
            return false;

        foreach (var r in rows)
            if (r is Dictionary<string, object?> d)
                entries.Add(Unflatten(d));
        return entries.Count > 0;
    }

    public bool TryProject(bool isSelfPlayer, int idx, out JsonObject entry, int timeoutMs = AnswerBudgetMs)
    {
        entry = new JsonObject();
        if (!Ask(_project, [_handle, isSelfPlayer, idx], o => o as System.Collections.IDictionary,
                 out var d, null, timeoutMs, "card not projected") || d is null || d.Count == 0)
            return false;
        foreach (System.Collections.DictionaryEntry kv in d)
            entry[(string)kv.Key] = kv.Value is null ? null : JsonValue.Create(Convert.ToInt32(kv.Value));
        return true;
    }

    // what a stock client would have decided about the last message this board took. the same check that writes
    // ConductError on a real client, asked before the message goes out instead of read out of a log afterwards
    public bool TryLastVerdict(out string verdict, out bool passed, int timeoutMs = AnswerBudgetMs)
    {
        passed = true;
        var ok = Ask(_lastVerdict, [_handle], o => o as string ?? "", out verdict!, "", timeoutMs, "verdict unread");
        verdict ??= "";
        passed = !verdict.StartsWith("FAIL", StringComparison.Ordinal);
        return ok && verdict.Length > 0;
    }

    // the shared StableRandom cursor, or -1. a receiver's spin is the actor's delta minus its own
    public bool TryRandomCursor(out int cursor, int timeoutMs = AnswerBudgetMs) =>
        Ask(_cursor, [_handle], o => (int)o!, out cursor, -1, timeoutMs, "cursor unread") && cursor >= 0;

    static JsonObject Unflatten(Dictionary<string, object?> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v is null ? null : JsonValue.Create(v);
        return o;
    }

    public void CompareCost(bool isSelfPlayer, int idx, int? relayCost, Action<string> log)
    {
        if (!Ready || _handle <= 0 || _costOf is null) return;
        Post(() =>
        {
            if (_handle <= 0) return;
            var live = (int)_costOf.Invoke(null, [_handle, isSelfPlayer, idx])!;
            if (live < 0) return;
            if (relayCost is null) log($"cost idx={idx}: relay said nothing, engine says {live}");
            else if (relayCost != live) log($"cost idx={idx}: relay {relayCost} vs engine {live}  DISAGREE");
        });
    }

    public void End(Action<int, string> report)
    {
        Post(() =>
        {
            if (_handle <= 0) return;
            try { report((int)_verdict!.Invoke(null, [_handle])!, (string)_state!.Invoke(null, [_handle])!); }
            finally { _close!.Invoke(null, [_handle]); _handle = -1; }
        });
    }

    public void Dispose()
    {
        if (!Ready) return;
        End((_, _) => { });
        WaitIdle(5_000);
        _work.CompleteAdding();
        _worker?.Join(5_000);
        Ready = false;
        // the context is deliberately NOT unloaded: every cached MethodInfo would have to go first, and a half-unloaded
        // context takes the engine down mid-answer with no exception the caller can see
    }

    // plain BCL collections cross the net10/net48 line unchanged (same runtime types); JSON nodes do not
    internal static Dictionary<string, object?> Flatten(JsonObject o)
    {
        var d = new Dictionary<string, object?>(o.Count);
        foreach (var (k, v) in o) d[k] = Value(v);
        return d;
    }

    static object? Value(JsonNode? n) => n switch
    {
        JsonObject o => Flatten(o),
        JsonArray a => a.Select(Value).ToList(),
        JsonValue v => v.TryGetValue<int>(out var i) ? i
                     : v.TryGetValue<bool>(out var b) ? b
                     : v.TryGetValue<long>(out var l) ? l
                     : v.TryGetValue<double>(out var f) ? f
                     : v.ToString(),
        _ => null,
    };
}
