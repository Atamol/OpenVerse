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

        // 11866 total card_master_full.csv.gz rows - 3424 in the token set (card_set_id 90000) = 8442
        Assert.Equal(8442, loader.UnlimitedCardIds.Count);
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
    public void ResurgentIsAlwaysASubsetOfUnlimitedCardIds()
    {
        var loader = new CardFilterLoader();

        // both lists exclude the same token set (card_set_id 90000), so every Resurgent id is
        // necessarily also Unlimited-legal - unlike before this exclusion was added, Resurgent
        // membership now DOES imply Unlimited-legality
        Assert.Contains(131011020, loader.Resurgent);
        Assert.Contains(131011020, loader.UnlimitedCardIds);

        Assert.All(loader.Resurgent, id => Assert.Contains(id, loader.UnlimitedCardIds));

        // a token-set id that used to be a Resurgent-but-not-Unlimited example - confirms it was
        // dropped from Resurgent entirely, not just left dangling as a token-set exception
        Assert.DoesNotContain(800044070, loader.Resurgent);
    }
}
