using Microsoft.Data.Sqlite;
using OpenVerse.Common;

namespace OpenVerse.Tests;

[Collection("Sqlite")]
public class DeckStoreTests : IDisposable
{
    readonly string _dbPath;
    readonly DeckStore _store;

    public DeckStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ov-test-{Guid.NewGuid():N}.db");
        _store = new DeckStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void NextDeckNoStartsAtOne()
    {
        Assert.Equal(1, _store.NextDeckNo("u1", 1));
    }

    [Fact]
    public void SaveAndList()
    {
        var d = new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "test", CardIdArray = [100, 200, 300] };
        _store.Save(d);
        var list = _store.List("u1", 1);
        Assert.Single(list);
        Assert.Equal("test", list[0].DeckName);
        Assert.Equal([100, 200, 300], list[0].CardIdArray);
    }

    [Fact]
    public void UpdateOverwrites()
    {
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "a", CardIdArray = [1] });
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "b", CardIdArray = [1, 2] });
        var d = _store.Get("u1", 1);
        Assert.NotNull(d);
        Assert.Equal("b", d!.DeckName);
        Assert.Equal(2, d.CardIdArray.Length);
    }

    [Fact]
    public void DeleteHidesFromList()
    {
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "a" });
        _store.Delete("u1", 1);
        Assert.Empty(_store.List("u1", 1));
        Assert.Null(_store.Get("u1", 1));
    }

    [Fact]
    public void NextDeckNoSkipsDeleted()
    {
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "a" });
        _store.Delete("u1", 1);
        Assert.Equal(2, _store.NextDeckNo("u1", 1));
    }

    [Fact]
    public void UsersAreIsolated()
    {
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "a" });
        Assert.Empty(_store.List("u2", 1));
        Assert.Equal(1, _store.NextDeckNo("u2", 1));
    }

    [Fact]
    public void FormatFilterRespected()
    {
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "rot" });
        _store.Save(new Deck { UserKey = "u1", DeckNo = 2, Format = 2, ClassId = 4, DeckName = "unl" });
        Assert.Equal("rot", _store.List("u1", 1).Single().DeckName);
        Assert.Equal("unl", _store.List("u1", 2).Single().DeckName);
        Assert.Equal(2, _store.List("u1", null).Count);
    }

    [Fact]
    public void UpdateOrderRewritesDisplayOrder()
    {
        _store.Save(new Deck { UserKey = "u1", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "a" });
        _store.Save(new Deck { UserKey = "u1", DeckNo = 2, Format = 1, ClassId = 4, DeckName = "b" });
        _store.UpdateOrder("u1", 1, [2, 1]);
        var list = _store.List("u1", 1);
        Assert.Equal("b", list[0].DeckName);
        Assert.Equal("a", list[1].DeckName);
    }
}
