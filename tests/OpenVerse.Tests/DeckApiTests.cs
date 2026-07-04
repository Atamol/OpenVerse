using System.Security.Cryptography;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class DeckApiTests : IClassFixture<DeckApiTests.Fixture>
{
    public class Fixture : WebApplicationFactory<Program>, IDisposable
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"ov-api-{Guid.NewGuid():N}.db");

        public Fixture()
        {
            Environment.SetEnvironmentVariable("OPENVERSE_DECK_DB", DbPath);
        }

        void IDisposable.Dispose()
        {
            base.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(DbPath); } catch { }
        }
    }

    readonly Fixture _f;
    readonly HttpClient _c;
    readonly string _udid;

    public DeckApiTests(Fixture f)
    {
        _f = f;
        _c = _f.CreateClient();
        _udid = $"{Guid.NewGuid():N}";
    }

    async Task<JsonElement> Call(string path, object req)
    {
        var reqJson = JsonSerializer.Serialize(req);
        var body = WireCrypto.EncryptApi(MessagePackSerializer.ConvertFromJson(reqJson), _udid, RandomNumberGenerator.GetBytes(32));
        var msg = new HttpRequestMessage(HttpMethod.Post, path);
        msg.Headers.Add("udid", _udid);
        msg.Content = new ByteArrayContent(body);
        var res = await _c.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        var backJson = MessagePackSerializer.ConvertToJson(WireCrypto.DecryptApi(Convert.FromBase64String(text), _udid));
        return JsonDocument.Parse(backJson).RootElement.GetProperty("data");
    }

    [Fact]
    public async Task InfoOnEmptyReturnsGroups()
    {
        var data = await Call("/shadowverse/deck/info", new { deck_format = 0 });
        Assert.Equal(0, data.GetProperty("user_deck_rotation").GetArrayLength());
        Assert.Equal(0, data.GetProperty("user_deck_unlimited").GetArrayLength());
    }

    [Fact]
    public async Task GetEmptyDeckNumberStartsAtOne()
    {
        var data = await Call("/shadowverse/deck/get_empty_deck_number", new { deck_format = 1 });
        Assert.Equal(1, data.GetProperty("empty_deck_num").GetInt32());
    }

    [Fact]
    public async Task UpdateCreatesDeckThenInfoShowsIt()
    {
        await Call("/shadowverse/deck/update", new
        {
            deck_no = 0,
            class_id = 4,
            deck_format = 1,
            deck_name = "dragon",
            card_id_array = new[] { 100, 101, 102 },
            leader_skin_id = 0,
            is_random_leader_skin = false,
            leader_skin_id_list = Array.Empty<int>(),
            sleeve_id = 3000011L,
            is_delete = 0,
            rotation_id = "",
        });
        var data = await Call("/shadowverse/deck/info", new { deck_format = 1 });
        var list = data.GetProperty("user_deck_list");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("dragon", list[0].GetProperty("deck_name").GetString());
        Assert.Equal(4, list[0].GetProperty("class_id").GetInt32());
        Assert.Equal(3, list[0].GetProperty("card_id_array").GetArrayLength());
    }

    [Fact]
    public async Task UpdateWithIsDeleteRemoves()
    {
        await Call("/shadowverse/deck/update", new
        {
            deck_no = 0,
            class_id = 2,
            deck_format = 1,
            deck_name = "gone",
            card_id_array = new[] { 1, 2 },
            leader_skin_id = 0,
            is_random_leader_skin = false,
            leader_skin_id_list = Array.Empty<int>(),
            sleeve_id = 3000011L,
            is_delete = 0,
            rotation_id = "",
        });
        var before = await Call("/shadowverse/deck/info", new { deck_format = 1 });
        var deckNo = before.GetProperty("user_deck_list")[0].GetProperty("deck_no").GetInt32();
        await Call("/shadowverse/deck/update", new
        {
            deck_no = deckNo,
            class_id = 2,
            deck_format = 1,
            deck_name = "gone",
            card_id_array = Array.Empty<int>(),
            leader_skin_id = 0,
            is_random_leader_skin = false,
            leader_skin_id_list = Array.Empty<int>(),
            sleeve_id = 3000011L,
            is_delete = 1,
            rotation_id = "",
        });
        var after = await Call("/shadowverse/deck/info", new { deck_format = 1 });
        Assert.Equal(0, after.GetProperty("user_deck_list").GetArrayLength());
    }

    [Fact]
    public async Task UpdateNameChangesName()
    {
        await Call("/shadowverse/deck/update", new
        {
            deck_no = 0,
            class_id = 3,
            deck_format = 2,
            deck_name = "old",
            card_id_array = new[] { 10 },
            leader_skin_id = 0,
            is_random_leader_skin = false,
            leader_skin_id_list = Array.Empty<int>(),
            sleeve_id = 3000011L,
            is_delete = 0,
            rotation_id = "",
        });
        var info = await Call("/shadowverse/deck/info", new { deck_format = 2 });
        var deckNo = info.GetProperty("user_deck_list")[0].GetProperty("deck_no").GetInt32();
        var data = await Call("/shadowverse/deck/update_name", new { deck_no = deckNo, deck_format = 2, deck_name = "new" });
        Assert.Equal("new", data.GetProperty("user_deck").GetProperty("deck_name").GetString());
    }

    [Fact]
    public async Task UpdateSleeveChangesSleeve()
    {
        await Call("/shadowverse/deck/update", new
        {
            deck_no = 0,
            class_id = 5,
            deck_format = 1,
            deck_name = "s",
            card_id_array = new[] { 1 },
            leader_skin_id = 0,
            is_random_leader_skin = false,
            leader_skin_id_list = Array.Empty<int>(),
            sleeve_id = 3000011L,
            is_delete = 0,
            rotation_id = "",
        });
        var info = await Call("/shadowverse/deck/info", new { deck_format = 1 });
        var deckNo = info.GetProperty("user_deck_list")[0].GetProperty("deck_no").GetInt32();
        var data = await Call("/shadowverse/deck/update_sleeve", new { deck_no = deckNo, deck_format = 1, sleeve_id = 3000042L });
        Assert.Equal(3000042L, data.GetProperty("user_deck").GetProperty("sleeve_id").GetInt64());
    }

    [Fact]
    public async Task AutoCreateReturnsFortyCards()
    {
        var reqJson = JsonSerializer.Serialize(new
        {
            deck_format = 1,
            class_id = 4,
            chosen_card_ids = Array.Empty<int>(),
            tournament_id = 0,
            rotation_id = "",
        });
        var body = WireCrypto.EncryptApi(MessagePackSerializer.ConvertFromJson(reqJson), _udid, RandomNumberGenerator.GetBytes(32));
        var msg = new HttpRequestMessage(HttpMethod.Post, "/shadowverse/auto_deck/create");
        msg.Headers.Add("udid", _udid);
        msg.Content = new ByteArrayContent(body);
        var res = await _c.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        var backJson = MessagePackSerializer.ConvertToJson(WireCrypto.DecryptApi(Convert.FromBase64String(text), _udid));
        var data = JsonDocument.Parse(backJson).RootElement.GetProperty("data");
        Assert.Equal(40, data.GetArrayLength());
    }
}
