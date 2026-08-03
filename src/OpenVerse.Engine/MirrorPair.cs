using System.Text.Json.Nodes;

namespace OpenVerse.Engine;

// Two engines per battle, one per client's viewpoint. One cannot do it: _stableRandomOnlySelf is per client, a
// receiver's spin needs that receiver's own draw count, and OperateReceiveChecker hardcodes BattleEnemy.
// GetSpinCount returning {vid, value} is the original server saying the same thing
public sealed class MirrorPair : IDisposable
{
    // what the client fabricates for an opponent deck it was never told
    public const int Dummy = 100111010;
    public const int DeckSize = 40;

    public ShadowMirror A { get; }
    public ShadowMirror B { get; }

    public bool Ready => A.Ready && B.Ready;
    public string? Failure => A.Failure ?? B.Failure;

    MirrorPair(ShadowMirror a, ShadowMirror b) { A = a; B = b; }

    public static MirrorPair Boot(string cardMasterCsv, string name = "pair")
    {
        var a = new ShadowMirror($"{name}-A");
        var b = new ShadowMirror($"{name}-B");
        var second = Task.Run(() => b.Init(cardMasterCsv));
        a.Init(cardMasterCsv);
        second.Wait();
        return new MirrorPair(a, b);
    }

    static int[] Dummies() => Enumerable.Repeat(Dummy, DeckSize).ToArray();

    public void Begin(int seed, bool aFirst, int[] aDeck, int[] bDeck, int[] aHand, int[] bHand, Action<string> log)
    {
        // Each side faces dummies, which is the board its client actually has. playerFirst is viewpoint-relative
        A.Begin(seed, aFirst, aDeck, Dummies(), aHand, bHand, log);
        B.Begin(seed, !aFirst, bDeck, Dummies(), bHand, aHand, log);
    }

    // Opt-in until this is measured: a board that drives its own play differently is a new way to be wrong
    public static bool IntentDriven =>
        Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_INTENT")?.Trim().ToLowerInvariant() is "1" or "true" or "on";

    public void Observe(string uri, JsonObject body, bool fromA, Action<string> log)
    {
        // The same message is self to one board and peer to the other. Inverting it is what must never be got wrong
        var actor = Actor(fromA);
        if (!IntentDriven || !DroveOwnPlay(actor, uri, body, log))
            actor.Observe(uri, body, isPlayer: true, log);
        Receiver(fromA).Observe(uri, body, isPlayer: false, log);
    }

    // A client never receives its own action, it ran it through OperateMgr, and the two paths do not draw the same way
    static bool DroveOwnPlay(ShadowMirror actor, string uri, JsonObject body, Action<string> log)
    {
        if (uri != "PlayActions") return false;
        if (body["playIdx"]?.GetValue<int>() is not int idx || idx <= 0) return false;

        var targets = Idxs(body["targetList"]).Concat(Idxs(body["oppoTargetList"])).Cast<object>().ToList();
        var choices = Idxs(body["choiceIdList"]).Cast<object>().ToList();
        if (actor.TryPlayByIntent(idx, targets, choices, out var why)) return true;
        log($"[{actor.Name}] could not play idx={idx} by intent ({why}); falling back to the receive path");
        return false;
    }

    static List<int> Idxs(JsonNode? n) => n is not JsonArray a ? [] :
        [.. a.Select(e => e is JsonObject o ? o["idx"]?.GetValue<int>() ?? -1 : e?.GetValue<int>() ?? -1).Where(i => i > 0)];

    public ShadowMirror Actor(bool fromA) => fromA ? A : B;
    public ShadowMirror Receiver(bool fromA) => fromA ? B : A;

    // The same check that writes ConductError on a real client. Reporting only: the actor has already committed
    // locally, so withholding the message would leave it with the card on board and the peer without it forever
    public bool PeerRefusedLast(bool fromA, out string verdict)
    {
        verdict = "";
        return Ready && Receiver(fromA).TryLastVerdict(out verdict, out var passed) && !passed;
    }

    // Spin is forward-only, so a negative delta cannot be expressed and is refused rather than sent
    public bool TrySpin(bool fromA, out int spin)
    {
        spin = 0;
        if (!Ready) return false;
        if (!Actor(fromA).TryRandomCursor(out var actor) || !Receiver(fromA).TryRandomCursor(out var receiver))
            return false;
        spin = actor - receiver;
        return spin >= 0;
    }

    public bool TryProject(bool fromA, int idx, out JsonObject entry) =>
        Actor(fromA).TryProject(isSelfPlayer: true, idx, out entry);

    public void End(Action<string, int, string> report)
    {
        A.End((code, state) => report(A.Name, code, state));
        B.End((code, state) => report(B.Name, code, state));
    }

    public bool WaitIdle(int timeoutMs = 120_000) => A.WaitIdle(timeoutMs) && B.WaitIdle(timeoutMs);

    public void Dispose() { A.Dispose(); B.Dispose(); }
}
