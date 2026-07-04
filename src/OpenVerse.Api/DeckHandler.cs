using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

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

    public DeckHandler(DeckStore store) { _store = store; }

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

    string Info(string userKey, int deckFormat)
    {
        var payload = new Dictionary<string, object> { ["maintenance_card_list"] = Array.Empty<int>() };
        if (deckFormat == 0)
        {
            foreach (var (fmt, key) in GroupKey)
                payload[key] = _store.List(userKey, fmt).Select(ToJson).ToArray();
        }
        else
        {
            payload["user_deck_list"] = _store.List(userKey, deckFormat).Select(ToJson).ToArray();
        }
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
        var payload = new Dictionary<string, object>
        {
            ["achieved_info"] = new { },
            ["reward_list"] = Array.Empty<object>(),
        };
        if (GroupKey.TryGetValue(format, out var key))
            payload[key] = _store.List(userKey, format).Select(ToJson).ToArray();
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

    static object ToJson(Deck d) => new
    {
        deck_no = d.DeckNo,
        deck_name = d.DeckName,
        class_id = d.ClassId,
        sub_class_id = d.SubClassId,
        card_id_array = d.CardIdArray,
        sleeve_id = d.SleeveId,
        leader_skin_id = d.LeaderSkinId,
        is_random_leader_skin = d.IsRandomLeaderSkin ? 1 : 0,
        leader_skin_id_list = d.LeaderSkinIdList,
        rotation_id = d.RotationId,
        format = d.Format,
        is_complete_deck = d.CardIdArray.Length == 40,
        is_include_un_possession_card = false,
        restricted_card_exists = false,
        current_format = d.Format,
        is_recommend = 0,
        create_deck_time = d.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
    };

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
