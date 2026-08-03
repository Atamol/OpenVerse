using System.IO.Compression;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// a pair holds one battle at a time, so two rooms playing at once need two. before the pool they shared one board and
// answered each other's questions with no sign of it
[Collection("Engine")]
public class MirrorPoolTests
{
    static string? Csv()
    {
        var gz = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release", "data", "card_master_full.csv.gz");
        if (!File.Exists(gz)) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll"))) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll"))) return null;
        using var fs = File.OpenRead(gz);
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }

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
