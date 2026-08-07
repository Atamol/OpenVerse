using OpenVerse.Engine;

namespace OpenVerse.Tests;

// A pair holds one battle at a time, so two rooms playing at once need two. before the pool they shared one board and
// answered each other's questions with no sign of it
[Collection("Engine")]
public class MirrorPoolTests
{
    static string? Csv() => Fixtures.CardMasterCsv();

    [Fact]
    public void TwoRoomsGetTwoDifferentPairs()
    {
        if (Csv() is not { } csv) return;
        using var pool = new MirrorPool(csv, cap: 2);

        var one = pool.Rent("room-1");
        var two = pool.Rent("room-2");

        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotSame(one, two);
        Assert.NotSame(one!.A, two!.A);
    }

    [Fact]
    public void TheSameRoomKeepsItsPair()
    {
        if (Csv() is not { } csv) return;
        using var pool = new MirrorPool(csv, cap: 2);

        Assert.Same(pool.Rent("room-1"), pool.Rent("room-1"));
        Assert.Equal(1, pool.Built);
    }

    // over the cap a battle relays with no engine, which is what it did before the pool existed. sharing a board would
    // be worse than having none
    [Fact]
    public void OverTheCapNoPairIsHandedOut()
    {
        if (Csv() is not { } csv) return;
        using var pool = new MirrorPool(csv, cap: 1);

        Assert.NotNull(pool.Rent("room-1"));
        Assert.Null(pool.Rent("room-2"));
    }

    // The relay never calls Rent, it calls For, and testing only Rent let `?? Pair` hand the shared board to every room
    // past the cap. The cap itself is not asserted here: ShadowBridge.Init is idempotent, so whichever test reaches it
    // first fixes the pool size for the run. what has to hold at any cap is that two rooms never share a board
    [Fact]
    public void TheBridgeNeverHandsTwoRoomsTheSameBoard()
    {
        if (Csv() is not { } csv) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);

        var rooms = Enumerable.Range(1, 6).Select(i => $"share-{i}").ToArray();
        try
        {
            var handed = rooms.Select(ShadowBridge.For).ToArray();

            Assert.Contains(handed, p => p is null);   // the cap is enforced at all
            var live = handed.Where(p => p is not null).ToArray();
            Assert.Equal(live.Length, live.Distinct().Count());
        }
        finally
        {
            foreach (var r in rooms) ShadowBridge.Release(r);
        }
    }

    // the whole reason for pooling: a released pair comes back warm rather than costing another engine boot
    [Fact]
    public void AReleasedPairIsReusedRatherThanRebuilt()
    {
        if (Csv() is not { } csv) return;
        using var pool = new MirrorPool(csv, cap: 1);

        var first = pool.Rent("room-1");
        Assert.NotNull(first);
        pool.Release("room-1");

        Assert.Same(first, pool.Rent("room-2"));
        Assert.Equal(1, pool.Built);
    }

    [Fact]
    public void ReleasingLetsTheRoomCountFall()
    {
        if (Csv() is not { } csv) return;
        using var pool = new MirrorPool(csv, cap: 2);

        pool.Rent("room-1");
        Assert.Equal(1, pool.Rented);
        pool.Release("room-1");
        Assert.Equal(0, pool.Rented);
    }

    [Fact]
    public void ReleasingARoomThatNeverRentedIsHarmless()
    {
        if (Csv() is not { } csv) return;
        using var pool = new MirrorPool(csv, cap: 1);
        pool.Release("never-rented");
        Assert.Equal(0, pool.Rented);
    }
}
