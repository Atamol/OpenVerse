using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;

namespace OpenVerse.Engine;

// a headless copy of the client's engine, run beside a live match to observe only, so nothing it computes reaches a
// player. net48, reached by reflection (EngineHost is not referenceable from net10). work runs on one background
// thread with a bounded queue that drops on overflow rather than blocking the relay
public static class ShadowBridge
{
    const BindingFlags Pub = BindingFlags.Public | BindingFlags.Static;

    // how far the engine is trusted. everything above Observe is opt-in via OPENVERSE_ENGINE_ROLE because it changes
    // what players see; Observe reaches no client
    public enum EngineRole { Off, Observe, AdviseCost, AnswerBlanks, DecideResult }

    public static EngineRole Role { get; private set; } = EngineRole.Observe;

    static Type? _host;
    static MethodInfo? _boot, _create, _ingest, _verdict, _state, _close, _costOf, _cardIdOf, _answerConditions;
    static readonly BlockingCollection<Action> _work = new(boundedCapacity: 256);
    static Thread? _worker;

    public static bool Ready { get; private set; }
    public static string? Failure { get; private set; }
    public static int Dropped { get; private set; }

    // false + Failure set when the shadow is not available
    public static bool Init(string cardMasterCsv)
    {
        if (Ready) return true;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll");
            if (!File.Exists(path)) { Failure = "OpenVerse.EngineHost.dll not present"; return false; }

            _host = Assembly.LoadFrom(path).GetType("OpenVerse.EngineHost.ShadowMatch");
            if (_host is null) { Failure = "ShadowMatch not found in the host"; return false; }

            _boot = _host.GetMethod("Boot", Pub);
            _create = _host.GetMethod("Create", Pub);
            _ingest = _host.GetMethod("Ingest", Pub);
            _verdict = _host.GetMethod("Verdict", Pub);
            _state = _host.GetMethod("State", Pub);
            _close = _host.GetMethod("Close", Pub);
            // optional surface: looked up but not required, null-checked at each call site, so an older host DLL loses
            // one answer instead of disabling the shadow
            _costOf = _host.GetMethod("CostOf", Pub);
            _cardIdOf = _host.GetMethod("CardIdOf", Pub);
            _answerConditions = _host.GetMethod("AnswerConditions", Pub);
            if (_boot is null || _create is null || _ingest is null || _verdict is null || _state is null
                || _close is null)
            {
                Failure = "the host is missing part of its surface";
                return false;
            }

            if (Enum.TryParse<EngineRole>(Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_ROLE"), true, out var r))
                Role = r;

            if (_boot.Invoke(null, [cardMasterCsv]) is not true) { Failure = HostError() ?? "boot failed"; return false; }

            _worker = new Thread(Drain) { IsBackground = true, Name = "shadow-engine" };
            _worker.Start();
            Ready = true;
            return true;
        }
        catch (Exception e) { Failure = e.InnerException?.Message ?? e.Message; return false; }
    }

    static string? HostError() => _host?.GetProperty("LastError", Pub)?.GetValue(null) as string;

    static int _inFlight;

    static void Drain()
    {
        foreach (var job in _work.GetConsumingEnumerable())
        {
            try { job(); } catch { /* an observation is never worth taking the server down for */ }
            finally { Interlocked.Decrement(ref _inFlight); }
        }
    }

    static bool Post(Action job)
    {
        if (!Ready) return false;
        Interlocked.Increment(ref _inFlight);
        if (_work.TryAdd(job)) return true;
        Interlocked.Decrement(ref _inFlight);
        Dropped++;
        return false;
    }

    // tests and shutdown only; the relay never blocks on the worker
    public static bool WaitIdle(int timeoutMs = 120_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Volatile.Read(ref _inFlight) > 0)
        {
            if (Environment.TickCount64 > until) return false;
            Thread.Sleep(10);
        }
        return true;
    }

    static int _handle = -1;

    // playerHand/enemyHand are the post-mulligan opening-hand indices: the shadow never sees the Deal/Swap, so without
    // them its board never leaves the deck. one match at a time
    public static void Begin(int seed, bool playerFirst, int[] playerDeck, int[] enemyDeck,
                             int[] playerHand, int[] enemyHand, Action<string> log)
    {
        Post(() =>
        {
            if (_handle > 0) return;
            _handle = (int)_create!.Invoke(null, [seed, playerFirst, playerDeck, enemyDeck, playerHand, enemyHand])!;
            log(_handle > 0
                ? $"shadow: observing, {(playerFirst ? "player" : "peer")} first"
                : $"shadow: not started ({HostError()})");
        });
    }

    // the relay adds these for the real peer's placeholder model; the shadow re-simulates from full information and
    // needs none. knownList walks ReplaceReceivedCard.CreateActualCard, which NREs headless (no card view)
    static readonly string[] PeerOnlyFields = ["knownList"];

    // fire and forget
    public static void Observe(string uri, JsonObject body, bool isPlayer, Action<string> log)
    {
        if (_handle <= 0 && !Ready) return;
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
            if (why.Length > 0) log($"shadow: {uri} not applied - {why}");
        });
    }

    // live cost of a hand card, queued so it sees every message ingested before it. bounded wait, then give up, and a
    // miss falls back to the relay's own synthesis
    public static bool TryCostOf(bool isSelfPlayer, int idx, out int cost, int timeoutMs = 250)
    {
        cost = -1;
        if (!Ready || _handle <= 0 || _costOf is null) return false;
        int answer = -1;
        using var done = new ManualResetEventSlim(false);
        if (!Post(() =>
        {
            try { answer = (int)_costOf!.Invoke(null, [_handle, isSelfPlayer, idx])!; }
            finally { done.Set(); }
        })) return false;
        if (!done.Wait(timeoutMs)) return false;
        cost = answer;
        return cost >= 0;
    }

    // which card sits at an index, any zone, so one query replaces a per-route reconstruction. 0 = unknown, keep what you had
    public static bool TryCardIdOf(bool isSelfPlayer, int idx, out int cardId, int timeoutMs = 250)
    {
        cardId = 0;
        if (!Ready || _handle <= 0 || _cardIdOf is null) return false;
        int answer = 0;
        using var done = new ManualResetEventSlim(false);
        if (!Post(() =>
        {
            try { answer = (int)_cardIdOf!.Invoke(null, [_handle, isSelfPlayer, idx])!; }
            finally { done.Set(); }
        })) return false;
        if (!done.Wait(timeoutMs)) return false;
        cardId = answer;
        return cardId != 0;
    }

    // answers the actor's skill-condition queries as ready-made knownList entries; a spec it cannot evaluate has no
    // row. call before the play reaches the shadow, since it needs the pre-play board
    public static bool TryConditionAnswers(bool isSelfPlayer, int cardIdx, JsonArray specs,
                                           out JsonArray entries, int timeoutMs = 250)
    {
        entries = new JsonArray();
        if (!Ready || _handle <= 0 || _answerConditions is null || specs.Count == 0) return false;
        // flattened here, on the relay thread: the worker must not touch nodes the relay still owns
        var flat = specs.Select(n => (object?)(n is JsonObject o ? Flatten(o) : null)).Where(x => x is not null).ToList();
        if (flat.Count == 0) return false;

        List<object>? rows = null;
        using var done = new ManualResetEventSlim(false);
        if (!Post(() =>
        {
            try { rows = (List<object>?)_answerConditions.Invoke(null, [_handle, isSelfPlayer, cardIdx, flat]); }
            finally { done.Set(); }
        })) return false;
        if (!done.Wait(timeoutMs) || rows is null || rows.Count == 0) return false;

        foreach (var r in rows)
            if (r is Dictionary<string, object?> d)
                entries.Add(Unflatten(d));
        return entries.Count > 0;
    }

    static JsonObject Unflatten(Dictionary<string, object?> d)
    {
        var o = new JsonObject();
        foreach (var (k, v) in d) o[k] = v is null ? null : JsonValue.Create(v);
        return o;
    }

    // log where the relay's synthesized price disagrees with the engine's, fire-and-forget: the evidence for turning
    // AdviseCost on, kept off the relay's path
    public static void CompareCost(bool isSelfPlayer, int idx, int? relayCost, Action<string> log)
    {
        if (!Ready || _handle <= 0 || _costOf is null) return;
        Post(() =>
        {
            var live = (int)_costOf.Invoke(null, [_handle, isSelfPlayer, idx])!;
            if (live < 0) return;
            if (relayCost is null) log($"cost idx={idx}: relay said nothing, engine says {live}");
            else if (relayCost != live) log($"cost idx={idx}: relay {relayCost} vs engine {live}  DISAGREE");
        });
    }

    // the engine's verdict on the finished board, to compare against the relay's
    public static void End(Action<int, string> report)
    {
        Post(() =>
        {
            if (_handle <= 0) return;
            report((int)_verdict!.Invoke(null, [_handle])!, (string)_state!.Invoke(null, [_handle])!);
            _close!.Invoke(null, [_handle]);
            _handle = -1;
        });
    }

    // plain BCL collections cross the net10/net48 line unchanged (same runtime types); JSON nodes do not
    static Dictionary<string, object?> Flatten(JsonObject o)
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
