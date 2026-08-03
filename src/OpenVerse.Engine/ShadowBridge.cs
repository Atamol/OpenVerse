using System.Text.Json.Nodes;

namespace OpenVerse.Engine;

public static class ShadowBridge
{
    // how far the engine is trusted. everything above Observe is opt-in via OPENVERSE_ENGINE_ROLE because it changes
    // what players see; Observe reaches no client
    public enum EngineRole { Off, Observe, AdviseCost, AnswerBlanks, DecideResult }

    public static EngineRole Role { get; private set; } = EngineRole.Observe;

    public static MirrorPair? Pair { get; private set; }
    public static MirrorPool? Pool { get; private set; }
    static ShadowMirror? _solo;

    static int PairCap =>
        int.TryParse(Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_PAIRS"), out var n) && n > 0 ? n : 2;

    // the A board. before Init, a mirror that is not Ready, so every call declines instead of throwing
    public static ShadowMirror Primary => Pair?.A ?? (_solo ??= new ShadowMirror("engine-0"));

    public static bool Ready => Primary.Ready;
    public static string? Failure => Pair?.Failure ?? Primary.Failure;
    public static int Dropped => Primary.Dropped;
    public static int Timeouts => Primary.Timeouts;

    public static bool Init(string cardMasterCsv)
    {
        if (Enum.TryParse<EngineRole>(Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_ROLE"), true, out var r))
            Role = r;
        if (Pair is { Ready: true }) return true;
        var p = MirrorPair.Boot(cardMasterCsv, "engine");
        Pair = p;
        if (p.Ready) Pool = new MirrorPool(cardMasterCsv, PairCap);
        return p.Ready;
    }

    public static MirrorPair? For(string battleId) => Pool?.Rent(battleId) ?? Pair;

    public static void Release(string battleId) => Pool?.Release(battleId);

    public static bool WaitIdle(int timeoutMs = 120_000) => Pair?.WaitIdle(timeoutMs) ?? Primary.WaitIdle(timeoutMs);

    public static void Begin(int seed, bool playerFirst, int[] playerDeck, int[] enemyDeck,
                             int[] playerHand, int[] enemyHand, Action<string> log,
                             int selfIdxSeed = -1, int oppoIdxSeed = -1)
    {
        if (Pair is { } p) p.Begin(seed, playerFirst, playerDeck, enemyDeck, playerHand, enemyHand, log);
        else Primary.Begin(seed, playerFirst, playerDeck, enemyDeck, playerHand, enemyHand, log, selfIdxSeed, oppoIdxSeed);
    }

    public static void Observe(string uri, JsonObject body, bool isPlayer, Action<string> log)
    {
        if (Pair is { } p) p.Observe(uri, body, fromA: isPlayer, log);
        else Primary.Observe(uri, body, isPlayer, log);
    }

    public static bool TryCostOf(bool isSelfPlayer, int idx, out int cost,
                                 int timeoutMs = ShadowMirror.AnswerBudgetMs) =>
        Actor(isSelfPlayer).TryCostOf(isSelfPlayer: true, idx, out cost, timeoutMs);

    public static bool TryCardIdOf(bool isSelfPlayer, int idx, out int cardId,
                                   int timeoutMs = ShadowMirror.AnswerBudgetMs) =>
        Actor(isSelfPlayer).TryCardIdOf(isSelfPlayer: true, idx, out cardId, timeoutMs);

    public static bool TryConditionAnswers(bool isSelfPlayer, int cardIdx, JsonArray specs, out JsonArray entries,
                                           int timeoutMs = ShadowMirror.AnswerBudgetMs) =>
        Actor(isSelfPlayer).TryConditionAnswers(isSelfPlayer: true, cardIdx, specs, out entries, timeoutMs);

    // a question about a side is answered by the board that OWNS that side, where the card is a real card rather than
    // one of the forty dummies. with one board the caller had to say which half to look in; with two it says whose
    static ShadowMirror Actor(bool isSelfPlayer) => Pair?.Actor(isSelfPlayer) ?? Primary;

    public static void CompareCost(bool isSelfPlayer, int idx, int? relayCost, Action<string> log) =>
        Actor(isSelfPlayer).CompareCost(isSelfPlayer: true, idx, relayCost, log);

    public static bool TryProject(bool isSelfPlayer, int idx, out JsonObject entry)
    {
        entry = new JsonObject();
        return Pair is { } p && p.TryProject(isSelfPlayer, idx, out entry);
    }

    // the receiver's cursor repair: the actor's draws minus that receiver's. only a pair can compute it
    public static bool TrySpin(bool isSelfPlayer, out int spin)
    {
        spin = 0;
        return Pair is { } p && p.TrySpin(isSelfPlayer, out spin);
    }

    public static void End(Action<int, string> report)
    {
        if (Pair is { } p) p.End((_, code, state) => report(code, state));
        else Primary.End(report);
    }
}
