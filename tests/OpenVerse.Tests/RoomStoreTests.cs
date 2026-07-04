using OpenVerse.Common;

namespace OpenVerse.Tests;

public class RoomStoreTests
{
    [Fact]
    public void CreateAllocates5DigitId()
    {
        var s = new RoomStore();
        var r = s.Create("owner", 1, 1, 1, 0, false, false);
        Assert.Matches(@"^\d{5}$", r.RoomId);
        Assert.NotEmpty(r.BattleId);
        Assert.Equal("owner", r.OwnerUdid);
    }

    [Fact]
    public void CreateProducesUniqueIds()
    {
        var s = new RoomStore();
        var seen = new HashSet<string>();
        for (int i = 0; i < 50; i++) Assert.True(seen.Add(s.Create("u" + i, 1, 1, 1, 0, false, false).RoomId));
    }

    [Fact]
    public void EnterAttachesVisitor()
    {
        var s = new RoomStore();
        var r = s.Create("owner", 1, 1, 1, 0, false, false);
        var joined = s.Enter("visitor", r.RoomId);
        Assert.NotNull(joined);
        Assert.Equal("visitor", joined!.VisitorUdid);
    }

    [Fact]
    public void EnterMissingRoomReturnsNull()
    {
        Assert.Null(new RoomStore().Enter("v", "99999"));
    }

    [Fact]
    public void CloseRemoves()
    {
        var s = new RoomStore();
        var r = s.Create("o", 1, 1, 1, 0, false, false);
        Assert.True(s.Close(r.RoomId));
        Assert.Null(s.Get(r.RoomId));
    }

    [Fact]
    public void LeaveClearsVisitor()
    {
        var s = new RoomStore();
        var r = s.Create("o", 1, 1, 1, 0, false, false);
        s.Enter("v", r.RoomId);
        s.Leave(r.RoomId);
        Assert.Null(s.Get(r.RoomId)!.VisitorUdid);
    }
}
