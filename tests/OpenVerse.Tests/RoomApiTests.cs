using System.Security.Cryptography;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class RoomApiTests : IClassFixture<RoomApiTests.Fixture>
{
    public class Fixture : WebApplicationFactory<Program>, IDisposable
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"ov-room-{Guid.NewGuid():N}.db");

        public Fixture() => Environment.SetEnvironmentVariable("OPENVERSE_DECK_DB", DbPath);

        void IDisposable.Dispose()
        {
            base.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(DbPath); } catch { }
        }
    }

    readonly HttpClient _c;

    public RoomApiTests(Fixture f) => _c = f.CreateClient();

    async Task<JsonElement> Call(string path, object req, string udid)
    {
        var reqJson = JsonSerializer.Serialize(req);
        var body = WireCrypto.EncryptApi(MessagePackSerializer.ConvertFromJson(reqJson), udid, RandomNumberGenerator.GetBytes(32));
        var msg = new HttpRequestMessage(HttpMethod.Post, path);
        msg.Headers.Add("udid", udid);
        msg.Content = new ByteArrayContent(body);
        var res = await _c.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        var back = MessagePackSerializer.ConvertToJson(WireCrypto.DecryptApi(Convert.FromBase64String(text), udid));
        return JsonDocument.Parse(back).RootElement.GetProperty("data");
    }

    [Fact]
    public async Task CreateReturnsRoomIdAndNodeUrl()
    {
        var udid = $"{Guid.NewGuid():N}";
        var data = await Call("/shadowverse/open_room/create_room", new
        {
            battle_type = 1,
            battle_rule = 1,
            can_friend_watch = 0,
            can_guild_watch = 0,
            deck_format = 1,
            two_pick_type = 0,
            is_guild_chat = 0,
        }, udid);
        Assert.Matches(@"^\d{5}$", data.GetProperty("room_id").GetString()!);
        Assert.NotEmpty(data.GetProperty("battle_id").GetString()!);
        Assert.NotEmpty(data.GetProperty("node_server_url").GetString()!);
    }

    [Fact]
    public async Task EnterReturnsOppoInfo()
    {
        var ownerUdid = $"{Guid.NewGuid():N}";
        var visitorUdid = $"{Guid.NewGuid():N}";
        var created = await Call("/shadowverse/open_room/create_room", new
        {
            battle_type = 1, battle_rule = 1, can_friend_watch = 0, can_guild_watch = 0,
            deck_format = 1, two_pick_type = 0, is_guild_chat = 0,
        }, ownerUdid);
        var roomId = created.GetProperty("room_id").GetString();
        var entered = await Call("/shadowverse/open_room/enter_room", new { room_id = roomId }, visitorUdid);
        Assert.Equal(0, entered.GetProperty("result_reason").GetInt32());
        var oppo = entered.GetProperty("oppo_info");
        Assert.StartsWith("player_", oppo.GetProperty("userName").GetString());
        Assert.True(oppo.GetProperty("oppoId").GetInt64() > 0);
        Assert.NotEmpty(entered.GetProperty("node_server_url").GetString()!);
    }

    [Fact]
    public async Task EnterMissingRoomReturnsError()
    {
        var udid = $"{Guid.NewGuid():N}";
        var data = await Call("/shadowverse/open_room/enter_room", new { room_id = "00000" }, udid);
        Assert.NotEqual(0, data.GetProperty("result_reason").GetInt32());
    }

    [Fact]
    public async Task CloseThenEnterFails()
    {
        var ownerUdid = $"{Guid.NewGuid():N}";
        var created = await Call("/shadowverse/open_room/create_room", new
        {
            battle_type = 1, battle_rule = 1, can_friend_watch = 0, can_guild_watch = 0,
            deck_format = 1, two_pick_type = 0, is_guild_chat = 0,
        }, ownerUdid);
        var roomId = created.GetProperty("room_id").GetString();
        await Call("/shadowverse/open_room/close_room", new { }, ownerUdid);
        var entered = await Call("/shadowverse/open_room/enter_room", new { room_id = roomId }, $"{Guid.NewGuid():N}");
        Assert.NotEqual(0, entered.GetProperty("result_reason").GetInt32());
    }

    [Fact]
    public async Task InitializeBattleReturnsBattleId()
    {
        var ownerUdid = $"{Guid.NewGuid():N}";
        var created = await Call("/shadowverse/open_room/create_room", new
        {
            battle_type = 1, battle_rule = 1, can_friend_watch = 0, can_guild_watch = 0,
            deck_format = 1, two_pick_type = 0, is_guild_chat = 0,
        }, ownerUdid);
        var roomId = created.GetProperty("room_id").GetString();
        var expected = created.GetProperty("battle_id").GetString();
        var data = await Call("/shadowverse/open_room/initialize_room_battle", new { room_id = roomId }, ownerUdid);
        Assert.Equal(expected, data.GetProperty("battle_id").GetString());
    }

    [Fact]
    public async Task DoMatchingReportsSuccessWithBattleInfo()
    {
        var owner = $"{Guid.NewGuid():N}";
        var created = await Call("/shadowverse/open_room/create_room", new
        {
            battle_type = 1, battle_rule = 1, can_friend_watch = 0, can_guild_watch = 0,
            deck_format = 1, two_pick_type = 0, is_guild_chat = 0,
        }, owner);
        var roomId = created.GetProperty("room_id").GetString();
        var battleId = created.GetProperty("battle_id").GetString();
        var visitor = $"{Guid.NewGuid():N}";
        await Call("/shadowverse/open_room/enter_room", new { room_id = roomId }, visitor);

        var om = await Call("/shadowverse/open_room_battle/do_matching", new { }, owner);
        Assert.Equal(3007, om.GetProperty("matching_state").GetInt32());    // owner
        Assert.Equal(battleId, om.GetProperty("battle_id").GetString());

        var vm = await Call("/shadowverse/open_room_battle/do_matching", new { }, visitor);
        Assert.Equal(3004, vm.GetProperty("matching_state").GetInt32());    // visitor
        Assert.Equal(battleId, vm.GetProperty("battle_id").GetString());
    }
}
