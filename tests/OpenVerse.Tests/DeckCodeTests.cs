using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenVerse.Api;
using OpenVerse.Common;

namespace OpenVerse.Tests;

[Collection("Sqlite")]
public class DeckCodeTests : IDisposable
{
    readonly string _dbPath;
    readonly DeckCodeStore _store;
    readonly DeckCodeHandler _handler;

    public DeckCodeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ov-dc-{Guid.NewGuid():N}.db");
        _store = new DeckCodeStore(_dbPath);
        _handler = new DeckCodeHandler(_store);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    [Theory]
    [InlineData("/api/v1/game_api/deck", true)]
    [InlineData("/api/v1/game_api/deck_code", true)]
    [InlineData("/api/v1/game_api/deck_image", false)]
    [InlineData("/deck/update", false)]
    [InlineData("/deck/get", false)]
    public void CanHandleMatchesOnlyPortalDeckEndpoints(string path, bool expected)
        => Assert.Equal(expected, DeckCodeHandler.CanHandle(path));

    // generate (TwoPickUI / DeckDetail) then resolve (DeckCreateMenuUI) is the whole loop friends use.
    [Fact]
    public void GenerateThenResolveRoundTrips()
    {
        var gen = _handler.Handle("""{"clan":4,"deck_format":1,"cardID":[100411110,100411110,100421310]}""");
        Assert.Equal(1, gen.ResultCode);
        var code = JsonDocument.Parse(gen.Data).RootElement.GetProperty("deck_code").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(code));

        var res = _handler.Handle($$"""{"deck_code":"{{code}}"}""");
        Assert.Equal(1, res.ResultCode);
        var deck = JsonDocument.Parse(res.Data).RootElement.GetProperty("deck");
        Assert.Equal(4, deck.GetProperty("clan").GetInt32());
        Assert.Equal(10, deck.GetProperty("sub_clan").GetInt32()); // no sub-class -> client sentinel
        Assert.Equal([100411110, 100411110, 100421310],
            deck.GetProperty("cardID").EnumerateArray().Select(e => e.GetInt32()));
        Assert.False(deck.TryGetProperty("rotation_id", out _)); // omitted when null
    }

    [Fact]
    public void GenerateKeepsSubClassAndRotation()
    {
        var gen = _handler.Handle("""{"clan":2,"sub_clan":6,"deck_format":7,"cardID":[1,2],"rotation_id":"r2025"}""");
        var code = JsonDocument.Parse(gen.Data).RootElement.GetProperty("deck_code").GetString()!;

        var deck = JsonDocument.Parse(_handler.Handle($$"""{"deck_code":"{{code}}"}""").Data).RootElement.GetProperty("deck");
        Assert.Equal(6, deck.GetProperty("sub_clan").GetInt32());
        Assert.Equal("r2025", deck.GetProperty("rotation_id").GetString());
    }

    [Fact]
    public void UnknownCodeReportsInvalid()
    {
        var res = _handler.Handle("""{"deck_code":"ZZZZZZ"}""");
        Assert.NotEqual(1, res.ResultCode); // non-1 makes the client show 存在しないコード
    }

    [Fact]
    public void ResolveIsCaseInsensitiveAndTrimmed()
    {
        var code = JsonDocument.Parse(_handler.Handle("""{"clan":1,"deck_format":1,"cardID":[5]}""").Data)
            .RootElement.GetProperty("deck_code").GetString()!;
        var res = _handler.Handle($$"""{"deck_code":" {{code.ToLowerInvariant()}} "}""");
        Assert.Equal(1, res.ResultCode);
    }

    [Fact]
    public void PurgeRemovesCodesOlderThanMaxAge()
    {
        _store.Save("OLD001", new DeckCodeStore.Entry(1, 10, 1, [5], null));
        _store.Save("NEW001", new DeckCodeStore.Entry(2, 10, 1, [6], null));
        Backdate("OLD001", TimeSpan.FromDays(8));

        Assert.Equal(1, _store.PurgeOlderThan(TimeSpan.FromDays(7)));
        Assert.Null(_store.Get("OLD001"));
        Assert.NotNull(_store.Get("NEW001"));
    }

    [Fact]
    public void PurgeKeepsCodesWithinMaxAge()
    {
        _store.Save("RECENT", new DeckCodeStore.Entry(1, 10, 1, [5], null));
        Backdate("RECENT", TimeSpan.FromDays(6));
        Assert.Equal(0, _store.PurgeOlderThan(TimeSpan.FromDays(7)));
        Assert.NotNull(_store.Get("RECENT"));
    }

    void Backdate(string code, TimeSpan age)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE deck_codes SET created_at = $at WHERE code = $c";
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Subtract(age).ToString("O"));
        cmd.Parameters.AddWithValue("$c", code);
        cmd.ExecuteNonQuery();
    }
}
