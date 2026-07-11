using System.Security.Cryptography;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using OpenVerse.Common;

namespace OpenVerse.Tests;

[Collection("Sqlite")]
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

    // load/index splices user_card_list + user_sleeve_list into the stub via string concat, so this
    // also proves the assembled blob stays valid JSON (Call would throw on a parse failure).
    [Fact]
    public async Task LoadIndexGrantsSleevesAndAlt()
    {
        var data = await Call("/shadowverse/load/index", new { });

        var sleeves = data.GetProperty("user_sleeve_list");
        Assert.True(sleeves.GetArrayLength() > 100);
        Assert.True(sleeves[0].TryGetProperty("sleeve_id", out _));

        var ids = new HashSet<long>();
        foreach (var c in data.GetProperty("user_card_list").EnumerateArray())
            ids.Add(c.GetProperty("card_id").GetInt64());
        Assert.Contains(705114010L, ids);        // alt illustration of 100114010
        Assert.DoesNotContain(1001140100L, ids); // no foil ids: premium is off by default
    }

    [Fact]
    public void IntroduceDeckInfoServesRealDecks()
    {
        var dbp = Path.Combine(Path.GetTempPath(), $"ov-intro-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DeckStore(dbp);
            List<OpenVerse.Api.IntroDeck> d =
                [new("Fairy Forestcraft", 3, 1, [.. Enumerable.Repeat(100311010, 40)], 100311010, "せせらぎ", "Cup Champion")];
            OpenVerse.Api.IntroSeries[] series = [new(1, "Tempest–Dawnbreak", d), new(2, "Wonderland–Brigade", d)];
            var handler = new OpenVerse.Api.DeckHandler(store, null, series);
            using var doc = JsonDocument.Parse(handler.IntroduceDeckInfo("""{"series_id":1}"""));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("series_id").GetInt32());
            Assert.Equal(2, root.GetProperty("series_list").GetArrayLength());  // both periods listed
            var decks = root.GetProperty("display_deck_list");
            Assert.Equal(1, decks.GetArrayLength());
            Assert.Equal("Fairy Forestcraft", decks[0].GetProperty("deck_name").GetString());
            Assert.Equal(3, decks[0].GetProperty("class_id").GetInt32());
            Assert.Equal(40, decks[0].GetProperty("card_id_array").GetArrayLength());
            Assert.Equal("せせらぎ", decks[0].GetProperty("player_name").GetString());
            Assert.Equal("Cup Champion", decks[0].GetProperty("introduction").GetString());
            Assert.Equal(100311010, decks[0].GetProperty("thumbnail_card_id").GetInt32());
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbp); } catch { } }
    }

    [Fact]
    public void LoadIndexUpgradesSeededStarterDecks()
    {
        var dbp = Path.Combine(Path.GetTempPath(), $"ov-mig-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DeckStore(dbp);
            int[] official = [100411010, 100411010, 100411020];
            // an old seeded starter (named スターター) and a deck the user made themselves
            store.Save(new Deck { UserKey = "u", DeckNo = 1, Format = 1, ClassId = 4, DeckName = "スターター", CardIdArray = [1, 2, 3] });
            store.Save(new Deck { UserKey = "u", DeckNo = 2, Format = 1, ClassId = 4, DeckName = "mine", CardIdArray = [7, 8] });
            List<OpenVerse.Common.DefaultDeckBuilder.DefaultDeck> starters = [new(4, official)];
            var handler = new OpenVerse.Api.DeckHandler(store, starters);

            handler.BuildLoadIndexDeckGroups("u");

            Assert.Equal(official, store.Get("u", 1)!.CardIdArray);  // starter upgraded to official cards
            Assert.Equal([7, 8], store.Get("u", 2)!.CardIdArray);    // user's own deck left alone
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbp); } catch { } }
    }

    [Fact]
    public void PracticeEndpointsServeRosterDecksAndResult()
    {
        var dbp = Path.Combine(Path.GetTempPath(), $"ov-prac-{Guid.NewGuid():N}.db");
        try
        {
            var store = new DeckStore(dbp);
            var deck = new OpenVerse.Api.DeckHandler(store);
            var roster = """[{"practice_id":1,"class_id":1,"ai_deck_level":106,"ai_logic_level":2,"ai_max_life":20}]""";
            var h = new OpenVerse.Api.PracticeHandler(roster, deck);

            using var info = JsonDocument.Parse(h.Handle("practice/info", "u"));   // roster array as-is
            Assert.Equal(106, info.RootElement[0].GetProperty("ai_deck_level").GetInt32());

            using var dl = JsonDocument.Parse(h.Handle("practice/deck_list", "u")); // deck groups + maintenance
            Assert.True(dl.RootElement.TryGetProperty("user_deck_rotation", out _));
            Assert.True(dl.RootElement.TryGetProperty("maintenance_card_list", out _));

            Assert.Equal("{}", h.Handle("practice/start", "u"));
            using var fin = JsonDocument.Parse(h.Handle("practice/finish", "u"));    // result shape the client reads
            Assert.True(fin.RootElement.TryGetProperty("reward_list", out _));
            Assert.True(fin.RootElement.TryGetProperty("class_level", out _));
        }
        finally { SqliteConnection.ClearAllPools(); try { File.Delete(dbp); } catch { } }
    }

    [Fact]
    public async Task InfoOnEmptyReturnsGroups()
    {
        var data = await Call("/shadowverse/deck/info", new { deck_format = 0 });
        // each group carries the trailing empty "create new" slot even with no stored decks
        var rot = data.GetProperty("user_deck_rotation");
        Assert.Equal(1, rot.GetArrayLength());
        Assert.Equal(1, data.GetProperty("user_deck_unlimited").GetArrayLength());
        Assert.Empty(rot[0].GetProperty("card_id_array").EnumerateArray());
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
        Assert.Equal(2, list.GetArrayLength());   // saved deck + trailing create-new slot
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
        // only the trailing create-new slot is left
        Assert.Equal(1, after.GetProperty("user_deck_list").GetArrayLength());
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
