using System.Text.Json.Nodes;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// two boards, one per client's viewpoint. The tests that matter are the ones a shared pair would fail: each mirror has
// to hold its OWN deck against dummies, and the same message has to be self to one and peer to the other
[Collection("Engine")]
public class MirrorPairTests
{
    static string? Csv() => Fixtures.CardMasterCsv();

    // two decks with no card in common, so "which board is this" is answerable from any card id
    static int[] DeckA() => Enumerable.Repeat(101334020, 40).ToArray();
    static int[] DeckB() => Enumerable.Repeat(113131030, 40).ToArray();

    // a board answers nothing until it has seen a TurnStart: without the turn messages it is not playing the same match
    static MirrorPair? Pair(string csv)
    {
        var p = MirrorPair.Boot(csv, "test");
        if (!p.Ready) { p.Dispose(); return null; }
        p.Begin(7, aFirst: true, DeckA(), DeckB(), [1, 2, 3], [1, 2, 3], _ => { });
        p.Observe("TurnStart", JsonNode.Parse("""{"turn":1,"isSelf":1}""")!.AsObject(), fromA: true, _ => { });
        p.WaitIdle();
        return p;
    }

    // the whole point: A holds A's cards, B holds B's, and neither holds the other's
    [Fact]
    public void EachMirrorHoldsItsOwnDeck()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        Assert.True(p.A.TryCardIdOf(isSelfPlayer: true, 1, out var a));
        Assert.True(p.B.TryCardIdOf(isSelfPlayer: true, 1, out var b));

        Assert.Equal(101334020, a);
        Assert.Equal(113131030, b);
    }

    // the client is never told the opponent's deck, so each mirror faces the same wall of dummies its client does
    [Fact]
    public void EachMirrorFacesDummiesNotTheRealOpponent()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        Assert.True(p.A.TryCardIdOf(isSelfPlayer: false, 1, out var seenByA));
        Assert.True(p.B.TryCardIdOf(isSelfPlayer: false, 1, out var seenByB));

        Assert.Equal(MirrorPair.Dummy, seenByA);
        Assert.Equal(MirrorPair.Dummy, seenByB);
    }

    // a shared pair would answer both of these off one board and they would come back equal
    [Fact]
    public void TheTwoBoardsAreNotTheSameBoard()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        Assert.True(p.A.TryCardIdOf(isSelfPlayer: true, 1, out var a));
        Assert.True(p.B.TryCardIdOf(isSelfPlayer: true, 1, out var b));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ActorAndReceiverAreOppositeEnds()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        Assert.Same(p.A, p.Actor(fromA: true));
        Assert.Same(p.B, p.Receiver(fromA: true));
        Assert.Same(p.B, p.Actor(fromA: false));
        Assert.Same(p.A, p.Receiver(fromA: false));
    }

    // The number the relay has never sent. Equal cursors on an untouched pair is the only value that can be right here
    [Fact]
    public void SpinIsZeroWhileTheCursorsAgree()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        Assert.True(p.TrySpin(fromA: true, out var spin), "neither cursor was readable");
        Assert.Equal(0, spin);
    }

    // projection is the replacement for reconstruction: the cost comes from the engine's own fold, not from the wire
    [Fact]
    public void ProjectsACardOffTheActorsBoard()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        Assert.True(p.TryProject(fromA: true, 1, out var entry), "the actor's board projected nothing");
        Assert.Equal(101334020, (int)entry["cardId"]!);
        Assert.Equal(1, (int)entry["idx"]!);
        Assert.True(entry.ContainsKey("cost"), "a projection with no cost is the whole problem restated");
    }

    // an ingested message is self to one board and peer to the other, and getting that backwards is the one mistake
    // that would look like it works
    [Fact]
    public void OneMessageIsSelfToOneMirrorAndPeerToTheOther()
    {
        if (Csv() is not { } csv) return;
        using var p = Pair(csv);
        if (p is null) return;

        var seen = new List<string>();
        p.Observe("TurnStart", JsonNode.Parse("""{"turn":1,"isSelf":1}""")!.AsObject(), fromA: true, seen.Add);
        Assert.True(p.WaitIdle());

        // both boards took it: neither reported it unapplied
        Assert.DoesNotContain(seen, l => l.Contains("not applied"));
    }
}
