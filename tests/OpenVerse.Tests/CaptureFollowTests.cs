using OpenVerse.Engine;
using Xunit.Abstractions;

namespace OpenVerse.Tests;

// everything above Observe on the EngineRole ladder assumes the shadow is on the same board as the clients. that has
// never been measured against real traffic: the last time it was looked at, most plays were throwing and the pair was
// answering off an empty board. this drives the captured match through a real MirrorPair and counts what lands
[Collection("Engine")]
public class CaptureFollowTests
{
    readonly ITestOutputHelper _out;
    public CaptureFollowTests(ITestOutputHelper o) => _out = o;

    sealed record Run(int Relayed, List<string> Unapplied, MirrorPair Pair);

    static Run? Replay(string csv, List<Capture.Row> rows, Action<string>? trace = null)
    {
        var pair = MirrorPair.Boot(csv, "follow");
        if (!pair.Ready) { pair.Dispose(); return null; }

        var (selfDeck, oppoDeck) = Capture.Decks(rows);
        var (selfHand, oppoHand) = Capture.Hands(rows);

        var unapplied = new List<string>();
        void Log(string l)
        {
            trace?.Invoke(l);
            if (l.Contains("not applied")) unapplied.Add(l);
        }

        // the capture's own seed is not recorded, and the pair only needs both halves started from the same one
        pair.Begin(7, aFirst: true, selfDeck, oppoDeck, selfHand, oppoHand, Log);
        pair.WaitIdle();

        var relayed = 0;
        foreach (var r in rows.Where(r => r.Relayed))
        {
            relayed++;
            pair.Observe(r.Uri, r.Body, fromA: r.IsPlayer, l => Log($"row {r.Line} {l}"));
            pair.WaitIdle();
        }
        return new Run(relayed, unapplied, pair);
    }

    // The premise the whole design rests on. not an assertion on zero yet: this records where it actually stands so a
    // regression is visible, and the number is the thing to drive down
    [Fact]
    public void FollowsTheCapturedMatchAndReportsWhatItDropped()
    {
        if (Capture.Csv() is not { } csv || Capture.Rows() is not { } rows) return;
        if (Replay(csv, rows) is not { } run) return;
        using var pair = run.Pair;

        var byReason = run.Unapplied
            .GroupBy(l => l[(l.IndexOf("not applied - ", StringComparison.Ordinal) + 14)..])
            .OrderByDescending(g => g.Count());

        _out.WriteLine($"relayed rows: {run.Relayed}, unapplied: {run.Unapplied.Count}");
        foreach (var g in byReason) _out.WriteLine($"  {g.Count()}x {g.Key}");
        foreach (var l in run.Unapplied.Take(10)) _out.WriteLine($"    {l}");

        Assert.True(run.Relayed > 0, "the capture carried no relayed rows, so this measured nothing");
    }

    // A board that stopped following says so by going quiet rather than by throwing, so ask it something only a live
    // board can answer. this is the check that would have caught the shadow sitting on hand 0 / deck 40
    [Fact]
    public void BothBoardsAreStillLiveAtTheEndOfTheCapture()
    {
        if (Capture.Csv() is not { } csv || Capture.Rows() is not { } rows) return;
        if (Replay(csv, rows) is not { } run) return;
        using var pair = run.Pair;

        Assert.True(pair.A.TryCardIdOf(isSelfPlayer: true, 1, out var a), "mirror A could not name its own idx 1");
        Assert.True(pair.B.TryCardIdOf(isSelfPlayer: true, 1, out var b), "mirror B could not name its own idx 1");
        _out.WriteLine($"A idx1={a} B idx1={b}");

        // each mirror faces dummies, so neither may have picked up the other's real cards along the way
        Assert.True(pair.A.TryCardIdOf(isSelfPlayer: false, 1, out var facedByA));
        Assert.Equal(MirrorPair.Dummy, facedByA);
    }
}
