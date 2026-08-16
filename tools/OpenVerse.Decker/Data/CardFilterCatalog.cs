namespace OpenVerse.Decker.Data;

/// <summary>
/// How cards are bucketed into filters
/// </summary>
public static class CardFilterCatalog
{
    public static readonly (string Label, Func<int, bool> Matches)[] Costs =
    [
        ("≦1", cost => cost <= 1),
        ("2", cost => cost == 2),
        ("3", cost => cost == 3),
        ("4", cost => cost == 4),
        ("5", cost => cost == 5),
        ("6", cost => cost == 6),
        ("7", cost => cost == 7),
        ("8", cost => cost == 8),
        ("9", cost => cost == 9),
        ("10≦", cost => cost >= 10),
    ];

    public static readonly (string Label, CardType[] Types)[] Kinds =
    [
        ("Fol", [CardType.Follower]),
        ("Spl", [CardType.Spell]),
        ("Amu", [CardType.CooltimeAmulet, CardType.PermanentAmulet]),
    ];

    public const string UnlimitedLabel = "Unlimited";
    public const string ResurgentLabel = "Resurgent";

    /// <summary>0 is Neutral, others are crafts</summary>
    public static readonly int[] ClanIds = [0, 1, 2, 3, 4, 5, 6, 7, 8];
    public static readonly int[] Rarities = [1, 2, 3, 4];

    public static FilterEngine Build(
        TextLoader text, StatsLoader stats, CardFilterLoader cardFilters, CardSetNames cardSets)
    {
        var order = stats.NormalOrder;
        var engine = new FilterEngine();

        CardStats StatsOf(int cardId) =>
            stats.Id2UnevolvedStats.GetValueOrDefault(cardId, MissingStats.Value);

        foreach (var (label, matches) in Costs)
        {
            engine.AddStatic(FilterChild.Cost(label), order.Where(id => matches(StatsOf(id).Cost)));
        }
        foreach (var (label, types) in Kinds)
        {
            engine.AddStatic(FilterChild.Kind(label), order.Where(id => types.Contains(StatsOf(id).CardType)));
        }
        engine.AddStatic(FilterChild.Format(UnlimitedLabel), cardFilters.UnlimitedCardIds);
        engine.AddStatic(FilterChild.Format(ResurgentLabel), cardFilters.Resurgent);

        foreach (var clanId in ClanIds)
        {
            engine.AddStatic(FilterChild.Clan(clanId), order.Where(id => StatsOf(id).Clan == clanId));
        }
        foreach (var rarity in Rarities)
        {
            engine.AddStatic(FilterChild.Rarity(rarity), order.Where(id => StatsOf(id).Rarity == rarity));
        }
        foreach (var tribe in stats.AllTribes)
        {
            engine.AddStatic(FilterChild.Tribe(tribe),
                order.Where(id => stats.Id2Tribes.GetValueOrDefault(id, []).Contains(tribe)));
        }
        foreach (var keyword in text.Keywords)
        {
            var needle = keyword.ToLowerInvariant();
            engine.AddStatic(FilterChild.Keyword(keyword), order.Where(id =>
                text.Id2SearchText.GetValueOrDefault(id, string.Empty).Contains(needle, StringComparison.Ordinal)));
        }

        foreach (var setId in cardSets.Packs)
        {
            engine.AddStatic(FilterChild.CardSet(setId), order.Where(id =>
                CardSetNames.BucketOf(stats.Id2CardSetId.GetValueOrDefault(id)) == setId));
        }

        engine.AddDynamic(FilterChild.SearchText, (argument, candidates) => MatchSearchTerms(text, argument, candidates));
        return engine;
    }

    // prepares dynamic id filter for text search for each search opeartion
    private static IEnumerable<int> MatchSearchTerms(TextLoader text, object? argument, IReadOnlyCollection<int> candidates)
    {
        if (argument is not string query)
        {
            return candidates;
        }
        var terms = query.Replace('　', ' ').ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return candidates;
        }

        return candidates.Where(id =>
            text.Id2SearchText.TryGetValue(id, out var searchText) &&
            terms.All(term => searchText.Contains(term, StringComparison.Ordinal)));
    }
}
