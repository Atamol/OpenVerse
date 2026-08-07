using OpenVerse.Decker.Data;

namespace OpenVerse.Tests;

// assets/stats_loader_card_master.csv.gz is a hand-authored fixture shaped like the real
// card_master_full.csv.gz (79 columns, per OpenVerse.Common.CardMasterCodec.Columns) - NOT
// extracted Cygames data. 5 rows:
//   900000001: Follower, cost 2, 2/2 -> 4/4 evolved, rarity 1
//   900000002: Spell,    cost 3, rarity 2
//   900000003: Amulet,   cost 1, rarity 1
//   900000004: PermanentAmulet (client's CHANT_FIELD), cost 1, rarity 3
//   900000005: Follower, cost 1, 1/1 -> 2/2 evolved, rarity 1
public class StatsLoaderTests
{
    private static readonly string CardMasterCsvPath =
        Path.Combine(AppContext.BaseDirectory, "assets", "stats_loader_card_master.csv.gz");

    [Fact]
    public void ConstructorThrowsWhenCardMasterFileMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new StatsLoader("does-not-exist.csv.gz", [900000001]));
    }

    [Fact]
    public void Id2UnevolvedStatsReflectsRealCostAtkLifeAndType()
    {
        var loader = new StatsLoader(CardMasterCsvPath, [900000001]);

        var stats = loader.Id2UnevolvedStats[900000001];
        Assert.Equal(2, stats.Cost);
        Assert.Equal(2, stats.Power);
        Assert.Equal(2, stats.Life);
        Assert.Equal(CardType.Follower, stats.CardType);
        Assert.Equal(1, stats.Rarity);
    }

    [Fact]
    public void Id2EvolvedStatsUsesTheEvolvedAtkAndLifeColumnsButKeepsCostAndType()
    {
        var loader = new StatsLoader(CardMasterCsvPath, [900000001]);

        var stats = loader.Id2EvolvedStats[900000001];
        Assert.Equal(2, stats.Cost); // cost doesn't change on evolving
        Assert.Equal(4, stats.Power);
        Assert.Equal(4, stats.Life);
        Assert.Equal(CardType.Follower, stats.CardType);
    }

    [Theory]
    [InlineData(900000002, CardType.Spell)]
    [InlineData(900000003, CardType.CooltimeAmulet)]
    [InlineData(900000004, CardType.PermanentAmulet)]
    public void CardTypeMatchesTheRealCharTypeColumn(int cardId, CardType expected)
    {
        var loader = new StatsLoader(CardMasterCsvPath, [cardId]);

        Assert.Equal(expected, loader.Id2UnevolvedStats[cardId].CardType);
    }

    [Theory]
    [InlineData(900000002)] // Spell
    [InlineData(900000003)] // Amulet
    [InlineData(900000004)] // PermanentAmulet
    public void NonFollowerCardsGetPowerAndLifeForcedToMinusOne(int cardId)
    {
        // the fixture's raw atk/life columns for these rows are "0", not -1 - StatsLoader has to
        // override them itself rather than trusting the CSV, since -1 is the "hide Power/Life"
        // sentinel every consumer (DeckCardEntry/CandidateCardEntry's StatsVisibility,
        // DescUserControl's RenderTitle, TextLoader's StatsTextOf) checks for
        var loader = new StatsLoader(CardMasterCsvPath, [cardId]);

        Assert.Equal(-1, loader.Id2UnevolvedStats[cardId].Power);
        Assert.Equal(-1, loader.Id2UnevolvedStats[cardId].Life);
        Assert.Equal(-1, loader.Id2EvolvedStats[cardId].Power);
        Assert.Equal(-1, loader.Id2EvolvedStats[cardId].Life);
    }

    [Fact]
    public void IdsNotInCardMasterAreAbsentFromBothStatsDictionaries()
    {
        var loader = new StatsLoader(CardMasterCsvPath, [900000001, 900000099]);

        Assert.False(loader.Id2UnevolvedStats.ContainsKey(900000099));
        Assert.False(loader.Id2EvolvedStats.ContainsKey(900000099));
    }

    [Fact]
    public void NormalOrderSortsByCostThenTypeThenRarity()
    {
        var loader = new StatsLoader(
            CardMasterCsvPath, [900000001, 900000002, 900000003, 900000004, 900000005]);

        // cost 1: 900000005 (Follower, rarity 1), 900000003 (Amulet, rarity 1), 900000004 (PermanentAmulet, rarity 3)
        // cost 2: 900000001 (Follower)
        // cost 3: 900000002 (Spell)
        Assert.Equal(
            [900000005, 900000003, 900000004, 900000001, 900000002],
            loader.NormalOrder);
    }

    [Fact]
    public void NormalOrderGroupsAmuletAndPermanentAmuletInTheSameTypeTier()
    {
        // both are cost 1, rarity differs (1 vs 3) - if they were in different type tiers,
        // rarity wouldn't be the deciding factor and this order could come out differently
        var loader = new StatsLoader(CardMasterCsvPath, [900000003, 900000004]);

        Assert.Equal([900000003, 900000004], loader.NormalOrder);
    }

    [Fact]
    public void NormalOrderExcludesIdsNotFoundInCardMaster()
    {
        var loader = new StatsLoader(CardMasterCsvPath, [900000001, 900000099]);

        Assert.DoesNotContain(900000099, loader.NormalOrder);
        Assert.Contains(900000001, loader.NormalOrder);
    }
}
