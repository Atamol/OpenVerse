using OpenVerse.Common;

namespace OpenVerse.Tests;

public class CardCostMapTests
{
    static string? DataDir() => Fixtures.DataDir();

    [Fact]
    public void MissingMasterYieldsEmptyMap()
    {
        Assert.Empty(CardCostMap.Load(Path.Combine(Path.GetTempPath(), "openverse-no-such-dir")));
    }

    // the card from the desynced match: the peer logged useCost5 (base) for a card that discounts 1 per boost
    [Fact]
    public void SpellboostCardFromTheDesyncedMatchParses()
    {
        if (DataDir() is not { } dir) return;
        var map = CardCostMap.Load(dir);
        var c = map[709314010];
        Assert.Equal(5, c.BaseCost);
        Assert.Equal(1, c.SpellboostStep);
    }

    // the other casualty: cost 1, no cost_change at all -> must not claim a discount
    [Fact]
    public void NonSpellboostCardHasNoStep()
    {
        if (DataDir() is not { } dir) return;
        var c = CardCostMap.Load(dir)[130124010];
        Assert.Equal(1, c.BaseCost);
        Assert.Equal(0, c.SpellboostStep);
    }

    // a wrong column index would silently poison every price, so pin the shape against the real master
    [Fact]
    public void CostsAreSaneAcrossTheWholeMaster()
    {
        if (DataDir() is not { } dir) return;
        var map = CardCostMap.Load(dir);
        Assert.True(map.Count > 10000);
        // A few unplayable specials sit at 30. anything wilder means the column index drifted
        Assert.All(map.Values, c => Assert.InRange(c.BaseCost, 0, 30));
        // UnreadableStep (-1) means the card has a when_spell_charge cost_change whose step would not parse
        Assert.All(map.Values, c => Assert.InRange(c.SpellboostStep, CardCostMap.UnreadableStep, 5));
        // spellboost discounts are a real but small minority. if this ever hits 0 the parse silently broke
        Assert.InRange(map.Values.Count(c => c.SpellboostStep > 0), 1, 400);
        // the relay declines to price anything in here, so a jump means the option column changed shape
        Assert.InRange(map.Values.Count(c => c.SpellboostStep == CardCostMap.UnreadableStep), 0, 50);
    }

    // The master carries a -1/-99 sentinel instead of a cost for a few rows. pricing off those would pin a bogus
    // absolute cost on the peer, so they must not be in the map at all
    [Fact]
    public void SentinelCostRowsAreExcluded()
    {
        if (DataDir() is not { } dir) return;
        Assert.DoesNotContain(CardCostMap.Load(dir).Values, c => c.BaseCost < 0);
    }
}
