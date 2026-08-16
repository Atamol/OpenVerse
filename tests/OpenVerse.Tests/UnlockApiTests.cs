using System.Security.Cryptography;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// End to end for the guild switch: press the button, then check that the two blobs the client reads at login came back
// different. Both are read once per launch, which is why the state has to live on the server and not in the session
[Collection("Sqlite")]
public class UnlockApiTests : IClassFixture<UnlockApiTests.Fixture>
{
    public class Fixture : WebApplicationFactory<Program>, IDisposable
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"ov-unlock-{Guid.NewGuid():N}.db");

        public Fixture() => Environment.SetEnvironmentVariable("OPENVERSE_DECK_DB", DbPath);

        void IDisposable.Dispose()
        {
            base.Dispose();
            SqliteConnection.ClearAllPools();
            try { File.Delete(DbPath); } catch { }
        }
    }

    readonly HttpClient _c;
    readonly string _udid;

    public UnlockApiTests(Fixture f)
    {
        _c = f.CreateClient();
        _udid = $"{Guid.NewGuid():N}";
    }

    async Task<JsonElement> Call(string path, object req, string? asUdid = null)
    {
        var udid = asUdid ?? _udid;
        var body = WireCrypto.EncryptApi(MessagePackSerializer.ConvertFromJson(JsonSerializer.Serialize(req)),
            udid, RandomNumberGenerator.GetBytes(32));
        var msg = new HttpRequestMessage(HttpMethod.Post, path);
        msg.Headers.Add("udid", udid);
        msg.Content = new ByteArrayContent(body);
        var res = await _c.SendAsync(msg);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync();
        var back = MessagePackSerializer.ConvertToJson(WireCrypto.DecryptApi(Convert.FromBase64String(text), udid));
        return JsonDocument.Parse(back).RootElement.GetProperty("data");
    }

    Task<JsonElement> LoadIndex(string? asUdid = null) => Call("/shadowverse/load/index", new { }, asUdid);
    Task<JsonElement> MyPage() => Call("/shadowverse/mypage/index", new { });

    // reaching the guild screen always goes through home first, and only that counts as a press
    async Task<JsonElement> PressGuild()
    {
        await MyPage();
        return await Call("/shadowverse/guild/info", new { });
    }

    static int CopiesOf(JsonElement load, long cardId) =>
        load.GetProperty("user_card_list").EnumerateArray()
            .First(c => c.GetProperty("card_id").GetInt64() == cardId).GetProperty("number").GetInt32();

    [Fact]
    public async Task BeforeTheSwitchNothingIsRelaxed()
    {
        var load = await LoadIndex();
        Assert.Empty(load.GetProperty("unlimited_restricted_base_card_id_list").EnumerateObject());
        Assert.Equal(3, CopiesOf(load, 100114010L));
    }

    [Fact]
    public async Task PressingTheGuildButtonRaisesTheCopyCapAndTheOwnedCount()
    {
        await PressGuild();
        var load = await LoadIndex();

        var restricted = load.GetProperty("unlimited_restricted_base_card_id_list").EnumerateObject().ToList();
        Assert.True(restricted.Count > 1000, $"only {restricted.Count} base cards were raised");
        Assert.All(restricted, p => Assert.Equal(UnlimitedUnlock.MaxCopies, p.Value.GetInt32()));

        // the deck editor also stops at what you own, so the grant has to move with the cap
        Assert.Equal(UnlimitedUnlock.MaxCopies, CopiesOf(load, 100114010L));
    }

    // CardMasterLocalFileUtility keeps the copy on disk while the hash matches, so the same hash means the client
    // never re-reads the master and the resurgent flag stays set
    [Fact]
    public async Task TheCardMasterHashMovesWithTheSwitch()
    {
        var locked = (await LoadIndex()).GetProperty("card_master_hash").GetString();
        await PressGuild();
        var unlocked = (await LoadIndex()).GetProperty("card_master_hash").GetString();
        Assert.NotEqual(locked, unlocked);
    }

    [Fact]
    public async Task TheCardMasterItselfChanges()
    {
        var locked = (await Call("/shadowverse/immutable_data/card_master", new { })).GetProperty("card_master").GetString();
        await PressGuild();
        var unlocked = (await Call("/shadowverse/immutable_data/card_master", new { })).GetProperty("card_master").GetString();
        Assert.NotEqual(locked, unlocked);
    }

    static Dictionary<string, string> Masters(string payload)
    {
        using var gz = new System.IO.Compression.GZipStream(
            new MemoryStream(Convert.FromBase64String(payload)), System.IO.Compression.CompressionMode.Decompress);
        using var sr = new StreamReader(gz, System.Text.Encoding.UTF8);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(sr.ReadToEnd())!;
    }

    static int ClanOf(string csv, long cardId)
    {
        var line = csv.Split('\n').First(l => l.StartsWith($"{cardId},"));
        var (s, e) = UnlimitedUnlock.FieldRange(line, 7);
        return int.Parse(line[s..e]);
    }

    // master 1 is the one every deck screen reads and master 2 is where the matching reply points the battle, so the
    // flattened clan has to be in the first only. Both the same would take clan=dragon down with it
    [Fact]
    public async Task OnlyTheEditorsMasterLosesTheClan()
    {
        await PressGuild();
        var both = Masters((await Call("/shadowverse/immutable_data/card_master", new { })).GetProperty("card_master").GetString()!);

        const long dragonCard = 100411010;
        Assert.Equal(0, ClanOf(both["1"], dragonCard));
        Assert.NotEqual(0, ClanOf(both["2"], dragonCard));
    }

    [Fact]
    public async Task WithTheSwitchOffBothMastersKeepTheirClans()
    {
        var both = Masters((await Call("/shadowverse/immutable_data/card_master", new { })).GetProperty("card_master").GetString()!);
        const long dragonCard = 100411010;
        Assert.NotEqual(0, ClanOf(both["1"], dragonCard));
        Assert.NotEqual(0, ClanOf(both["2"], dragonCard));
    }

    // Master 2 is the untouched CSV in both payloads, so every battle goes there whatever the switch says. Gating it
    // on the switch would leave a hole: turning it off does not reload the master the client is still running
    [Fact]
    public async Task EveryBattleIsPointedAtTheUntouchedMaster()
    {
        var created = await Call("/shadowverse/open_room/create_room", new
        {
            battle_type = 1, battle_rule = 1, can_friend_watch = 0, can_guild_watch = 0,
            deck_format = 2, two_pick_type = 0, is_guild_chat = 0,
        });
        var roomId = created.GetProperty("room_id").GetString();
        var visitor = $"{Guid.NewGuid():N}";
        await Call("/shadowverse/open_room/enter_room", new { room_id = roomId }, visitor);

        var locked = await Call("/shadowverse/open_room_battle/do_matching", new { }, visitor);
        Assert.Equal(2, locked.GetProperty("card_master_id").GetInt32());

        await PressGuild();
        var unlocked = await Call("/shadowverse/open_room_battle/do_matching", new { });
        Assert.Equal(2, unlocked.GetProperty("card_master_id").GetInt32());
    }

    [Fact]
    public async Task PressingItTwicePutsEverythingBack()
    {
        var before = await LoadIndex();
        await PressGuild();
        await PressGuild();
        var after = await LoadIndex();
        Assert.Empty(after.GetProperty("unlimited_restricted_base_card_id_list").EnumerateObject());
        Assert.Equal(before.GetProperty("card_master_hash").GetString(), after.GetProperty("card_master_hash").GetString());
        Assert.Equal(3, CopiesOf(after, 100114010L));
    }

    // the way a mixed-class deck gets in: the editor only offers the deck's own class, but DeckCreateMenuUI accepts
    // any id that exists in the master, so a code can carry cards no editor would have let you pick
    [Fact]
    public async Task ADeckCodeCanBeMintedFromCardIdsOfAnyClass()
    {
        var req = new { clan = 1, deck_format = 2, cardID = new[] { 100114010, 100211010, 100311010 } };
        var res = await _c.PostAsync("/openverse/deckcode",
            new StringContent(JsonSerializer.Serialize(req), System.Text.Encoding.UTF8, "application/json"));
        res.EnsureSuccessStatusCode();

        var code = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
            .RootElement.GetProperty("deck_code").GetString();
        Assert.False(string.IsNullOrWhiteSpace(code));
    }

    // the flip cannot reach a running client, so mypage has to offer the one door back through login: MyPageTask puts
    // a BackToTitle button on this prompt, and BackToTitle is SoftwareReset.exec
    [Fact]
    public async Task AfterAFlipMypageAsksTheClientToGoBackToTheTitle()
    {
        await LoadIndex();
        var quiet = await Call("/shadowverse/mypage/index", new { });
        Assert.False(quiet.TryGetProperty("can_give_daily_login_bonus", out var before) && before.GetBoolean());

        await PressGuild();
        var nagged = await Call("/shadowverse/mypage/index", new { });
        Assert.True(nagged.GetProperty("can_give_daily_login_bonus").GetBoolean());

        // going back through login settles it, so the prompt stops
        await LoadIndex();
        var settled = await Call("/shadowverse/mypage/index", new { });
        Assert.False(settled.TryGetProperty("can_give_daily_login_bonus", out var after) && after.GetBoolean());
    }

    // one box hosts everyone, so a switch that leaked would change the other player's collection mid-session
    [Fact]
    public async Task TheOtherPlayerIsUntouched()
    {
        await PressGuild();
        var other = await LoadIndex($"{Guid.NewGuid():N}");
        Assert.Empty(other.GetProperty("unlimited_restricted_base_card_id_list").EnumerateObject());
    }
}
