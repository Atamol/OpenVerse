using OpenVerse.Engine;
using Xunit.Abstractions;

namespace OpenVerse.Tests;

// The offline replay rig that found the shadow's drift lives outside the repo and carries its own copies of the ingest
// path, so a green run there says nothing about the code that actually ships. this drives the same captured match
// through ShadowBridge, which is the seam the relay uses, and asks the price the relay would ask for
[Collection("Engine")]
public class ShadowCostFidelityTests
{
    readonly ITestOutputHelper _out;
    public ShadowCostFidelityTests(ITestOutputHelper o) => _out = o;

    // 18 base cost, and the capture charges it 16 times, so the actor played it for 2
    const int DimensionShift = 101334020;
    const int DimensionShiftIdx = 2;
    const int WirePrice = 2;

    [Fact]
    public void PricesTheCapturedSpellboostPlayExactlyAsTheWireDid()
    {
        if (Capture.Csv() is not { } csv || Capture.Rows() is not { } rows) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);

        var (selfDeck, oppoDeck) = Capture.Decks(rows);
        var (selfHand, oppoHand) = Capture.Hands(rows);
        Assert.Equal(DimensionShift, selfDeck[DimensionShiftIdx - 1]);

        // no deck mirror: installing one from the capture's idxChangeSeed leaves the shadow 3 charges short here, so the
        // relay does not install it either until a capture says which way round is in step with the players
        var log = new List<string>();
        ShadowBridge.Begin(7, playerFirst: true, selfDeck, oppoDeck, selfHand, oppoHand, log.Add);
        Assert.True(ShadowBridge.WaitIdle(), "the shadow never finished starting");

        try
        {
            int? priced = null;
            foreach (var r in rows)
            {
                // read before ingesting: this is where the relay asks, with the play still in hand
                if (priced is null && r.Uri == "PlayActions" && r.IsPlayer
                    && Capture.Int(r.Body["playIdx"]) == DimensionShiftIdx
                    && ShadowBridge.TryCostOf(isSelfPlayer: true, DimensionShiftIdx, out var live))
                    priced = live;

                ShadowBridge.Observe(r.Uri, r.Body, r.IsPlayer, log.Add);
                Assert.True(ShadowBridge.WaitIdle(), $"the shadow stalled on row {r.Line} ({r.Uri})");
            }

            foreach (var l in log.TakeLast(20)) _out.WriteLine(l);
            Assert.Equal(WirePrice, priced);

            // the reveal path the relay uses at Observe: if the trust gate silences this, summoned cards stop
            // resolving for the peer, which is worse than the blank it was added to avoid
            Assert.True(ShadowBridge.TryCardIdOf(isSelfPlayer: true, DimensionShiftIdx, out var revealed),
                "the shadow declined to name a card it holds");
            _out.WriteLine($"CardIdOf(idx {DimensionShiftIdx}) = {revealed}");
        }
        finally
        {
            ShadowBridge.End((_, _) => { });
            ShadowBridge.WaitIdle();
        }
    }
}
