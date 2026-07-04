using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

public sealed class RoomHandler
{
    readonly RoomStore _rooms;
    readonly UserStore _users;

    public RoomHandler(RoomStore rooms, UserStore users) { _rooms = rooms; _users = users; }

    public static bool CanHandle(string path) =>
        path.StartsWith("/shadowverse/open_room/", StringComparison.OrdinalIgnoreCase);

    public string Handle(string path, string reqJson, string ownerUdid)
    {
        using var doc = JsonDocument.Parse(reqJson);
        var root = doc.RootElement;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return name switch
        {
            "create_room" => Create(ownerUdid, root),
            "enter_room" => Enter(ownerUdid, root),
            "leave_room" => Leave(root),
            "close_room" => Close(ownerUdid),
            "force_release_room" => ForceRelease(ownerUdid),
            "initialize_room_battle" => InitializeBattle(root),
            _ => "{}",
        };
    }

    string Create(string ownerUdid, JsonElement root)
    {
        _users.GetOrCreate(ownerUdid);
        var room = _rooms.Create(
            ownerUdid,
            GetInt(root, "battle_type"),
            GetInt(root, "battle_rule"),
            GetInt(root, "deck_format"),
            GetInt(root, "two_pick_type"),
            GetInt(root, "can_friend_watch") == 1,
            GetInt(root, "can_guild_watch") == 1);
        return JsonSerializer.Serialize(new
        {
            room_id = room.RoomId,
            display_room_id = room.RoomId,
            is_invitation_user = false,
            is_enabled_all_card = true,
            node_server_url = room.NodeServerUrl,
            battle_id = room.BattleId,
        });
    }

    string Enter(string visitorUdid, JsonElement root)
    {
        var roomId = GetString(root, "room_id") ?? "";
        var room = _rooms.Enter(visitorUdid, roomId);
        if (room is null)
            return JsonSerializer.Serialize(new { result_reason = -3 });
        var owner = _users.GetOrCreate(room.OwnerUdid);
        _users.GetOrCreate(visitorUdid);
        return JsonSerializer.Serialize(new
        {
            result_reason = 0,
            is_friend = 0,
            guild_id = 0,
            oppo_guild_id = 0,
            oppo_info = new
            {
                oppoId = owner.ViewerId,
                battlePoint = owner.BattlePoint,
                degreeId = owner.DegreeId,
                emblemId = owner.EmblemId,
                country_code = owner.CountryCode,
                rank = owner.Rank,
                max_rank = owner.MaxRank,
                userName = owner.Name,
                isOfficial = owner.IsOfficial,
            },
            node_server_url = room.NodeServerUrl,
            battle_id = room.BattleId,
            is_invitation_user = false,
            is_enabled_all_card = true,
            battle_type = room.BattleType,
            battle_rule = room.BattleRule,
            deck_format = room.DeckFormat,
            two_pick_type = room.TwoPickType,
        });
    }

    string Leave(JsonElement root)
    {
        _rooms.Leave(GetString(root, "room_id") ?? "");
        return JsonSerializer.Serialize(new { result_reason = 0, room_result = 1 });
    }

    string Close(string ownerUdid)
    {
        var room = _rooms.FindByOwner(ownerUdid);
        if (room is not null) _rooms.Close(room.RoomId);
        return "{}";
    }

    string ForceRelease(string ownerUdid)
    {
        var room = _rooms.FindByOwner(ownerUdid);
        if (room is not null) _rooms.Close(room.RoomId);
        return JsonSerializer.Serialize(new { room_result = 1 });
    }

    string InitializeBattle(JsonElement root)
    {
        var room = _rooms.Get(GetString(root, "room_id") ?? "");
        return JsonSerializer.Serialize(new
        {
            battle_id = room?.BattleId ?? "",
            my_battle_result = new { },
            opponent_battle_result = new { },
            used_deck = 0,
            is_settled = 0,
        });
    }

    static int GetInt(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    static string? GetString(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
