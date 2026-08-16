using OpenVerse.Decker.Data;

namespace OpenVerse.Tests;

public class FilterEngineTests
{
    private static readonly int[] Universe = [1, 2, 3, 4, 5, 6];

    [Fact]
    public void NoActiveChildLeavesTheUniverseUntouched()
    {
        var engine = new FilterEngine();
        engine.AddStatic(FilterChild.Cost("2"), [1, 2]);

        Assert.Equal(Universe, engine.Apply(Universe, new HashSet<FilterChild>()));
    }

    [Fact]
    public void ChildrenInOneGroupAreOred()
    {
        var engine = new FilterEngine();
        engine.AddStatic(FilterChild.Cost("2"), [1, 2]);
        engine.AddStatic(FilterChild.Cost("3"), [5]);

        var result = engine.Apply(Universe, new HashSet<FilterChild> { FilterChild.Cost("2"), FilterChild.Cost("3") });

        Assert.Equal([1, 2, 5], result);
    }

    [Fact]
    public void SeparateGroupsAreAnded()
    {
        var engine = new FilterEngine();
        engine.AddStatic(FilterChild.Cost("2"), [1, 2, 3]);
        engine.AddStatic(FilterChild.Kind("Fol"), [2, 3, 4]);

        var result = engine.Apply(Universe, new HashSet<FilterChild> { FilterChild.Cost("2"), FilterChild.Kind("Fol") });

        Assert.Equal([2, 3], result);
    }

    // a group nobody selected a button in must not wipe the result - the natural bug when each
    // group starts from an empty "passed" set and only active children add to it.
    [Fact]
    public void AGroupWithNoActiveChildImposesNoRestriction()
    {
        var engine = new FilterEngine();
        engine.AddStatic(FilterChild.Cost("2"), [1, 2, 3]);
        engine.AddStatic(FilterChild.Kind("Fol"), [2, 3, 4]);

        var result = engine.Apply(Universe, new HashSet<FilterChild> { FilterChild.Cost("2") });

        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public void ResultKeepsUniverseOrderNotFilterOrder()
    {
        var engine = new FilterEngine();
        engine.AddStatic(FilterChild.Cost("2"), [5, 1, 3]);

        var result = engine.Apply(Universe, new HashSet<FilterChild> { FilterChild.Cost("2") });

        Assert.Equal([1, 3, 5], result);
    }

    [Fact]
    public void DynamicFilterOnlySeesCardsThatSurvivedTheStaticGroups()
    {
        IReadOnlyCollection<int>? seen = null;
        var engine = new FilterEngine();
        engine.AddDynamic(FilterChild.SearchText, (_, candidates) =>
        {
            seen = candidates;
            return candidates;
        });
        engine.AddStatic(FilterChild.Cost("2"), [1, 2]);

        engine.Apply(Universe, new HashSet<FilterChild> { FilterChild.Cost("2"), FilterChild.SearchText });

        // registered first, but still evaluated after the cost group narrowed 6 cards down to 2
        Assert.Equal([1, 2], seen!.OrderBy(id => id));
    }

    [Fact]
    public void DynamicFilterReceivesItsRegisteredArgument()
    {
        var engine = new FilterEngine();
        engine.AddDynamic(FilterChild.SearchText,
            (argument, candidates) => candidates.Where(id => id == (int)argument!));

        var result = engine.Apply(
            Universe,
            new HashSet<FilterChild> { FilterChild.SearchText },
            new Dictionary<FilterChild, object?> { [FilterChild.SearchText] = 4 });

        Assert.Equal([4], result);
    }

    [Fact]
    public void DynamicFilterCannotResurrectACardAnEarlierGroupRejected()
    {
        var engine = new FilterEngine();
        engine.AddStatic(FilterChild.Cost("2"), [1, 2]);
        engine.AddDynamic(FilterChild.SearchText, (_, _) => Universe);

        var result = engine.Apply(Universe, new HashSet<FilterChild> { FilterChild.Cost("2"), FilterChild.SearchText });

        Assert.Equal([1, 2], result);
    }
}
