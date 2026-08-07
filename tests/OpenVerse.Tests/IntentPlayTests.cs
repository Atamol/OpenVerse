using System.Text.Json.Nodes;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// A client never receives its own action, it ran it through OperateMgr. driving the actor's board the same way is what
// makes it a copy of that client rather than a spectator of it, and the two paths do not touch StableRandom alike
[Collection("Engine")]
public class IntentPlayTests
{
    static string? Csv() => Fixtures.CardMasterCsv();

    // a 1pp 1/1 with no targeting, so a failure is the seam and not the card
    const int Plain = 100011010;
    static int[] Deck() => Enumerable.Repeat(Plain, 40).ToArray();

    static MirrorPair? Started(string csv)
    {
        var p = MirrorPair.Boot(csv, "intent");
        if (!p.Ready) { p.Dispose(); return null; }
        p.Begin(7, aFirst: true, Deck(), Deck(), [1, 2, 3], [1, 2, 3], _ => { });
        p.Observe("TurnStart", JsonNode.Parse("""{"turn":1,"isSelf":1}""")!.AsObject(), fromA: true, _ => { });
        p.WaitIdle();
        return p;
    }

    [Fact]
    public void TheActorsBoardCanPlayItsOwnCardThroughOperateMgr()
    {
        if (Csv() is not { } csv) return;
        using var p = Started(csv);
        if (p is null) return;

        var ok = p.A.TryPlayByIntent(1, [], [], out var why);
        p.WaitIdle();

        Assert.True(ok, $"the intent seam refused a plain hand play: {why}");
    }

    // the fallback is what makes this safe to ship: a refusal must be reported, not swallowed
    [Fact]
    public void AnIndexNotInHandIsRefusedWithAReason()
    {
        if (Csv() is not { } csv) return;
        using var p = Started(csv);
        if (p is null) return;

        Assert.False(p.A.TryPlayByIntent(999, [], [], out var why));
        Assert.Contains("999", why);
    }

    [Fact]
    public void ATargetThatIsNotOnTheBoardIsRefused()
    {
        if (Csv() is not { } csv) return;
        using var p = Started(csv);
        if (p is null) return;

        Assert.False(p.A.TryPlayByIntent(1, [999], [], out var why));
        Assert.Contains("target", why);
    }

    // off by default: a board that drives its own play differently is a new way to be wrong, so it stays opt-in until
    // real matches say otherwise
    [Fact]
    public void IntentDrivingIsOffUnlessAskedFor()
    {
        if (Environment.GetEnvironmentVariable("OPENVERSE_ENGINE_INTENT") is not null) return;
        Assert.False(MirrorPair.IntentDriven);
    }
}
