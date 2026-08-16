using OpenVerse.Decker.Data;

namespace OpenVerse.Tests;

// CardFilterLoader reads from embedded resources (Resources/unlimited_cards.json,
// Resources/resurgent_cards.json), curated once from the real card_master_full.csv.gz - no
// external file path, so unlike TextLoader/StatsLoader these tests don't need a fixture file.
public class CardFilterLoaderTests
{
    [Fact]
    public void UnlimitedCardIdsExcludesTheTokenSetButKeepsRealCards()
    {
        var loader = new CardFilterLoader();

        // 100011010 (Goblin) - a real Follower, used throughout the rest of this test suite
        Assert.Contains(100011010, loader.UnlimitedCardIds);
        // every id in the token set (card_set_id 90000 in card_master_full.csv.gz) was excluded -
        // spot-checked against a known token-set-only id
        Assert.DoesNotContain(800044070, loader.UnlimitedCardIds);
    }

    [Fact]
    public void UnlimitedCardIdsHasTheExpectedCount()
    {
        var loader = new CardFilterLoader();

        // 11866 rows - 3424 token-set (card_set_id 90000) - 378 resurgent = 8064, which is 4032
        // distinct cards once each foil/normal pair is collapsed - the client's own Unlimited count.
        Assert.Equal(8064, loader.UnlimitedCardIds.Count);
    }

    [Fact]
    public void ResurgentHasTheExpectedCount()
    {
        var loader = new CardFilterLoader();

        // column 76 (IsResurgentCard, per the decompiled client's CardCSVData field order) == "1",
        // same token-set (card_set_id 90000) exclusion as UnlimitedCardIds - 540 raw rows - 162 in
        // the token set = 378
        Assert.Equal(378, loader.Resurgent.Count);
    }

    [Fact]
    public void ResurgentAndUnlimitedAreDisjointSoTheButtonsSelectSeparatePools()
    {
        var loader = new CardFilterLoader();

        Assert.Contains(131011020, loader.Resurgent);
        Assert.DoesNotContain(131011020, loader.UnlimitedCardIds);
        Assert.DoesNotContain(loader.Resurgent, loader.UnlimitedCardIds.Contains);

        // a token-set id: excluded from both, not merely shuffled from one pool into the other
        Assert.DoesNotContain(800044070, loader.Resurgent);
        Assert.DoesNotContain(800044070, loader.UnlimitedCardIds);
    }

    [Fact]
    public void ResurgentStillCoversEveryCardTheRawResourceListed()
    {
        var loader = new CardFilterLoader();

        // subtracting resurgent from unlimited must not shrink Resurgent itself
        Assert.Equal(378, loader.Resurgent.Count);
    }
}
