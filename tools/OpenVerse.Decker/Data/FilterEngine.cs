namespace OpenVerse.Decker.Data;

/// <summary>
/// A filter group. Children inside one group are OR'd, and groups are AND'd with each other.
/// </summary>
public enum FilterGroup
{
    Cost,
    CardKind,
    Format,
    Clan,
    Rarity,
    Tribe,
    Keyword,
    CardSet,
    SearchText,
}

/// <summary>
/// One filter button. Keywords and tribes come from the loaded card data rather than a fixed list,
/// so a child is identified by a string within its group instead of by an enum member.
/// </summary>
public readonly record struct FilterChild(FilterGroup Group, string Id)
{
    public static FilterChild Cost(string label) => new(FilterGroup.Cost, label);
    public static FilterChild Kind(string label) => new(FilterGroup.CardKind, label);
    public static FilterChild Format(string label) => new(FilterGroup.Format, label);
    public static FilterChild Clan(int clanId) => new(FilterGroup.Clan, clanId.ToString());
    public static FilterChild Rarity(int rarity) => new(FilterGroup.Rarity, rarity.ToString());
    public static FilterChild Tribe(string tribe) => new(FilterGroup.Tribe, tribe);
    public static FilterChild Keyword(string keyword) => new(FilterGroup.Keyword, keyword);
    public static FilterChild CardSet(int cardSetId) => new(FilterGroup.CardSet, cardSetId.ToString());

    public static FilterChild SearchText { get; } = new(FilterGroup.SearchText, nameof(SearchText));
}

/// <summary>
/// Receives only the cards still alive after every cheaper group has run, so it stays small.
/// </summary>
public delegate IEnumerable<int> DynamicFilter(object? argument, IReadOnlyCollection<int> candidates);

/// <summary>
/// Evaluates <c>group AND group AND ...</c> where each group is <c>child OR child OR ...</c>.
/// Static children carry a card-id set built once at registration; dynamic children run a
/// callback, and their groups are always evaluated last so the callback sees the fewest cards.
/// </summary>
public sealed class FilterEngine
{
    private sealed class Group(FilterGroup name)
    {
        public FilterGroup Name { get; } = name;
        public Dictionary<FilterChild, IReadOnlySet<int>> Static { get; } = [];
        public Dictionary<FilterChild, DynamicFilter> Dynamic { get; } = [];
    }

    private readonly List<Group> _groups = [];

    public void AddStatic(FilterChild child, IEnumerable<int> cardIds) =>
        GroupOf(child.Group).Static[child] = cardIds.ToHashSet();

    public void AddDynamic(FilterChild child, DynamicFilter filter) =>
        GroupOf(child.Group).Dynamic[child] = filter;

    private Group GroupOf(FilterGroup group)
    {
        var existing = _groups.Find(g => g.Name == group);
        if (existing is not null)
        {
            return existing;
        }
        var created = new Group(group);
        _groups.Add(created);
        return created;
    }

    /// <summary>
    /// Returns the surviving cards in <paramref name="universe"/> order. A group with no active
    /// child imposes no restriction; only <paramref name="active"/> children narrow the result.
    /// </summary>
    public List<int> Apply(
        IReadOnlyList<int> universe,
        IReadOnlySet<FilterChild> active,
        IReadOnlyDictionary<FilterChild, object?>? arguments = null)
    {
        var surviving = universe.ToHashSet();

        // OrderBy is stable, so static-only groups keep registration order and run first no matter
        // when their dynamic siblings were registered.
        foreach (var group in _groups.OrderBy(g => g.Dynamic.Count > 0))
        {
            var statics = group.Static.Where(pair => active.Contains(pair.Key)).ToArray();
            var dynamics = group.Dynamic.Where(pair => active.Contains(pair.Key)).ToArray();
            if (statics.Length == 0 && dynamics.Length == 0)
            {
                continue;
            }

            var passed = new HashSet<int>();
            foreach (var (_, cardIds) in statics)
            {
                foreach (var cardId in cardIds)
                {
                    if (surviving.Contains(cardId))
                    {
                        passed.Add(cardId);
                    }
                }
            }

            if (dynamics.Length > 0)
            {
                var candidates = surviving.ToArray();
                foreach (var (child, filter) in dynamics)
                {
                    foreach (var cardId in filter(arguments?.GetValueOrDefault(child), candidates))
                    {
                        if (surviving.Contains(cardId))
                        {
                            passed.Add(cardId);
                        }
                    }
                }
            }

            surviving.IntersectWith(passed);
            if (surviving.Count == 0)
            {
                break;
            }
        }

        return universe.Where(surviving.Contains).ToList();
    }
}
