using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

// A deck in the 大会上位デッキ紹介 browser. Format 1 = rotation, 2 = unlimited
public sealed record IntroDeck(string Name, int Clan, int Format, int[] CardIds, int ThumbnailCardId,
    string PlayerName, string Introduction);
public sealed record IntroSeries(int SeriesId, string SeriesName, List<IntroDeck> Decks,
    int DisplayFormat = 2, int IsTsRotation = 0);

public sealed class DeckHandler
{
    static readonly Dictionary<int, string> GroupKey = new()
    {
        [1] = "user_deck_rotation",
        [2] = "user_deck_unlimited",
        [3] = "user_deck_pre_rotation",
        [4] = "user_deck_crossover",
        [5] = "user_deck_my_rotation",
        [39] = "user_deck_avatar",
    };

    readonly DeckStore _store;
    readonly object[] _defaultDeckList;
    readonly List<DefaultDeckBuilder.DefaultDeck> _starters;
    readonly List<IntroSeries> _introSeries;

    public DeckHandler(DeckStore store, IEnumerable<DefaultDeckBuilder.DefaultDeck>? defaultDecks = null,
        IEnumerable<IntroSeries>? introSeries = null)
    {
        _store = store;
        _introSeries = (introSeries ?? []).ToList();
        _starters = (defaultDecks ?? []).ToList();
        _defaultDeckList = _starters.Select((d, i) => (object)new
        {
            deck_no = 900000 + i,
            deck_name = "スターター",
            class_id = d.ClassId,
            sub_class_id = 10,
            card_id_array = d.CardIdArray,
            sleeve_id = 3000011L,
            leader_skin_id = 0,
            is_random_leader_skin = 0,
            leader_skin_id_list = Array.Empty<int>(),
            format = 2,
            is_complete_deck = true,
            is_include_un_possession_card = false,
            restricted_card_exists = false,
            current_format = 2,
            is_recommend = 1,
        }).ToArray();
    }

    // load/index deck groups. The client drops empty custom-deck groups from its DeckGroupDataBase
    // (DeckListUpdate skips count==0), so an empty format NREs in the deck editor. Seed starters
    // once per format so each group is non-empty, then serve the user's stored decks.
    public string BuildLoadIndexDeckGroups(string userKey)
    {
        MigrateStarters(userKey);
        SeedStarters(userKey, 1);
        SeedStarters(userKey, 2);
        object Group(int fmt) => new { user_deck_list = DeckListWithEmptySlot(userKey, fmt) };
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["user_deck_rotation"] = Group(1),
            ["user_deck_unlimited"] = Group(2),
        });
    }

    // The client turns the first empty (card_id_array == []) custom-deck slot into the "create new"
    // button, so every deck list needs a trailing empty slot for new decks to be creatable.
    object[] DeckListWithEmptySlot(string userKey, int format) =>
        [.. _store.List(userKey, format).Select(ToJson), ToJson(new Deck { Format = format })];

    // Upgrade the auto-generated "スターター" decks a user was seeded with to the official ones. Matches by
    // the seeded name and only rewrites when the cards differ, so it is idempotent.
    void MigrateStarters(string userKey)
    {
        if (_starters.Count == 0) return;
        var official = _starters.GroupBy(d => d.ClassId).ToDictionary(g => g.Key, g => g.First().CardIdArray);
        foreach (var deck in _store.List(userKey))
        {
            if (deck.DeckName != "スターター") continue;
            if (!official.TryGetValue(deck.ClassId, out var off)) continue;
            if (!deck.CardIdArray.SequenceEqual(off))
            {
                deck.CardIdArray = off;
                _store.Save(deck);
            }
        }
    }

    void SeedStarters(string userKey, int format)
    {
        if (_store.List(userKey, format).Count > 0) return;
        foreach (var d in _starters)
            _store.Save(new Deck
            {
                UserKey = userKey,
                DeckNo = _store.NextDeckNo(userKey, format),
                Format = format,
                ClassId = d.ClassId,
                DeckName = "スターター",
                CardIdArray = d.CardIdArray,
            });
    }

    // introduce_deck/info: returns the requested series_id's decks plus the full series_list of periods.
    // Copying a deck runs the normal deck/info + deck/update flow. Falls back to starters if no intro data
    public string IntroduceDeckInfo(string reqJson)
    {
        if (_introSeries.Count == 0)
        {
            var starters = _starters.Select((d, i) => IntroEntry(800000 + i, 2, d.ClassId, "サンプルデッキ",
                d.CardIdArray, d.CardIdArray.Length > 0 ? d.CardIdArray[0] : 0, "OpenVerse", "全カード解放環境のサンプルデッキ")).ToArray();
            return Serialize(new Dictionary<string, object>
            {
                ["series_id"] = 1, ["display_format"] = 2, ["display_deck_list"] = starters,
                ["series_list"] = new[] { (object)new { series_id = 1, series_name = "サンプル", is_ts_rotation = 0 } },
            });
        }
        using var doc = JsonDocument.Parse(reqJson);
        var reqId = GetInt(doc.RootElement, "series_id");
        var series = _introSeries.FirstOrDefault(s => s.SeriesId == reqId) ?? _introSeries[0];
        var decks = series.Decks.Select((d, i) =>
            IntroEntry(800000 + series.SeriesId * 100 + i, d.Format, d.Clan, d.Name, d.CardIds, d.ThumbnailCardId, d.PlayerName, d.Introduction)).ToArray();
        return Serialize(new Dictionary<string, object>
        {
            ["series_id"] = series.SeriesId,
            ["display_format"] = series.DisplayFormat,
            ["display_deck_list"] = decks,
            ["series_list"] = IntroSeriesList(),
        });
    }

    // introduce_deck/series_list: just the list of periods.
    public string IntroduceDeckSeriesList() =>
        Serialize(new Dictionary<string, object> { ["series_list"] = IntroSeriesList() });

    object[] IntroSeriesList() =>
        _introSeries.Select(s => (object)new { series_id = s.SeriesId, series_name = s.SeriesName, is_ts_rotation = s.IsTsRotation }).ToArray();

    object IntroEntry(int deckNo, int format, int clan, string name, int[] cardIds, int thumbnail,
        string playerName, string introduction)
    {
        var o = (Dictionary<string, object>)ToJson(new Deck
        {
            DeckNo = deckNo,
            Format = format,
            ClassId = clan,
            DeckName = name,
            CardIdArray = cardIds,
        });
        o["player_name"] = playerName;
        o["introduction"] = introduction;
        o["thumbnail_card_id"] = thumbnail;
        return o;
    }

    public static bool CanHandle(string path) =>
        path.StartsWith("/shadowverse/deck/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/shadowverse/auto_deck/", StringComparison.OrdinalIgnoreCase);

    public string Handle(string path, string reqJson, string userKey)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var root = doc.RootElement;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name switch
        {
            "info" or "my_list" or "deck_list" => Info(userKey, GetInt(root, "deck_format")),
            "get_empty_deck_number" => EmptyNo(userKey, GetInt(root, "deck_format")),
            "update" => Update(userKey, root),
            "update_name" => UpdateName(userKey, root),
            "update_sleeve" => UpdateSleeve(userKey, root),
            "update_leader_skin" => UpdateLeaderSkin(userKey, root),
            "update_random_leader_skin" => UpdateRandomLeaderSkin(userKey, root),
            "update_order" => UpdateOrder(userKey, root),
            "delete_deck_list" => DeleteList(userKey, root),
            "create" => AutoCreate(userKey, root),
            _ => "{}",
        };
    }

    // practice/deck_list is parsed by the same ParseDeckInfoResponceData as deck/info, so it needs the
    // deck/info shape (each group is a plain deck array), not the load/index shape.
    public string PracticeDeckList(string userKey) => Info(userKey, 0);

    string Info(string userKey, int deckFormat)
    {
        var payload = new Dictionary<string, object> { ["maintenance_card_list"] = Array.Empty<int>() };
        if (deckFormat == 0)
        {
            foreach (var (fmt, key) in GroupKey)
                payload[key] = DeckListWithEmptySlot(userKey, fmt);
        }
        else
        {
            payload["user_deck_list"] = DeckListWithEmptySlot(userKey, deckFormat);
        }
        payload["default_deck_list"] = _defaultDeckList;
        return Serialize(payload);
    }

    string EmptyNo(string userKey, int deckFormat) =>
        Serialize(new Dictionary<string, object> { ["empty_deck_num"] = _store.NextDeckNo(userKey, deckFormat) });

    string Update(string userKey, JsonElement root)
    {
        var format = GetInt(root, "deck_format");
        var deckNo = GetInt(root, "deck_no");
        var isDelete = GetInt(root, "is_delete");
        if (isDelete == 1)
        {
            _store.Delete(userKey, deckNo);
        }
        else
        {
            _store.Save(new Deck
            {
                UserKey = userKey,
                DeckNo = deckNo == 0 ? _store.NextDeckNo(userKey, format) : deckNo,
                Format = format,
                ClassId = GetInt(root, "class_id"),
                SubClassId = TryGetInt(root, "sub_class_id") ?? 10,
                DeckName = GetString(root, "deck_name") ?? "",
                SleeveId = TryGetLong(root, "sleeve_id") ?? 3000011L,
                LeaderSkinId = GetInt(root, "leader_skin_id"),
                IsRandomLeaderSkin = GetBool(root, "is_random_leader_skin"),
                LeaderSkinIdList = GetIntArray(root, "leader_skin_id_list"),
                RotationId = GetString(root, "rotation_id"),
                CardIdArray = GetIntArray(root, "card_id_array"),
            });
        }
        var list = DeckListWithEmptySlot(userKey, format);
        var payload = new Dictionary<string, object>
        {
            ["achieved_info"] = new { },
            ["reward_list"] = Array.Empty<object>(),
            // DeckUpdateTask reads the concrete user_deck_list, not the group key
            ["user_deck_list"] = list,
        };
        if (GroupKey.TryGetValue(format, out var key))
            payload[key] = list;
        return Serialize(payload);
    }

    string UpdateName(string userKey, JsonElement root)
    {
        var d = MustGet(userKey, GetInt(root, "deck_no"));
        d.DeckName = GetString(root, "deck_name") ?? d.DeckName;
        _store.Save(d);
        return Serialize(new Dictionary<string, object> { ["user_deck"] = ToJson(d) });
    }

    string UpdateSleeve(string userKey, JsonElement root)
    {
        var d = MustGet(userKey, GetInt(root, "deck_no"));
        d.SleeveId = TryGetLong(root, "sleeve_id") ?? d.SleeveId;
        _store.Save(d);
        return Serialize(new Dictionary<string, object> { ["user_deck"] = ToJson(d) });
    }

    string UpdateLeaderSkin(string userKey, JsonElement root)
    {
        var d = MustGet(userKey, GetInt(root, "deck_no"));
        d.LeaderSkinId = GetInt(root, "leader_skin_id");
        _store.Save(d);
        return Serialize(new Dictionary<string, object> { ["user_deck"] = ToJson(d) });
    }

    string UpdateRandomLeaderSkin(string userKey, JsonElement root)
    {
        var d = MustGet(userKey, GetInt(root, "deck_no"));
        d.LeaderSkinIdList = GetIntArray(root, "leader_skin_id_list");
        d.IsRandomLeaderSkin = true;
        if (d.LeaderSkinIdList.Length > 0) d.LeaderSkinId = d.LeaderSkinIdList[0];
        _store.Save(d);
        return Serialize(new Dictionary<string, object> { ["user_deck"] = ToJson(d) });
    }

    string UpdateOrder(string userKey, JsonElement root)
    {
        var format = GetInt(root, "deck_format");
        _store.UpdateOrder(userKey, format, GetIntArray(root, "deck_order"));
        return Info(userKey, format);
    }

    string DeleteList(string userKey, JsonElement root)
    {
        _store.DeleteMany(userKey, GetIntArray(root, "deck_no_list"));
        return Info(userKey, GetInt(root, "deck_format"));
    }

    static string AutoCreate(string _, JsonElement root)
    {
        int cls = GetInt(root, "class_id");
        var cards = Enumerable.Repeat(cls * 100_000_000 + 1, 40).ToArray();
        return JsonSerializer.Serialize(cards);
    }

    Deck MustGet(string userKey, int deckNo) =>
        _store.Get(userKey, deckNo) ?? throw new InvalidOperationException($"deck {deckNo} not found");

    static object ToJson(Deck d)
    {
        var o = new Dictionary<string, object>
        {
            ["deck_no"] = d.DeckNo,
            ["deck_name"] = d.DeckName,
            ["class_id"] = d.ClassId,
            ["sub_class_id"] = d.SubClassId,
            ["card_id_array"] = d.CardIdArray,
            ["sleeve_id"] = d.SleeveId,
            ["leader_skin_id"] = d.LeaderSkinId,
            ["is_random_leader_skin"] = d.IsRandomLeaderSkin ? 1 : 0,
            ["leader_skin_id_list"] = d.LeaderSkinIdList,
            ["format"] = d.Format,
            ["is_complete_deck"] = d.CardIdArray.Length == 40,
            ["is_include_un_possession_card"] = false,
            ["restricted_card_exists"] = false,
            ["current_format"] = d.Format,
            ["is_recommend"] = 0,
            ["create_deck_time"] = d.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
        };
        // omit rotation_id when null. GetValueOrDefault(...,null).ToString() NREs on a JSON null value
        if (d.RotationId is not null) o["rotation_id"] = d.RotationId;
        return o;
    }

    static string Serialize(object o) => JsonSerializer.Serialize(o);

    static int GetInt(JsonElement e, string k) => TryGetInt(e, k) ?? 0;
    static int? TryGetInt(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    static long? TryGetLong(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    static string? GetString(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool GetBool(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.GetInt32() != 0,
            _ => false,
        };
    }
    static int[] GetIntArray(JsonElement e, string k)
    {
        if (!e.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        var list = new List<int>(v.GetArrayLength());
        foreach (var i in v.EnumerateArray()) if (i.ValueKind == JsonValueKind.Number) list.Add(i.GetInt32());
        return [.. list];
    }
}
