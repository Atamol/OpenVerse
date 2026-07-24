using System.Collections.Concurrent;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Api;

public sealed class RoomHandler
{
    readonly RoomStore _rooms;
    readonly UserStore _users;
    readonly DeckStore _decks;
    readonly BattleDeckStore _battleDecks;
    // each participant's deck_no from set_deck, resolved to the shared battle_deck table at do_matching
    readonly ConcurrentDictionary<string, int> _deckByUdid = new();

    public RoomHandler(RoomStore rooms, UserStore users, DeckStore decks, BattleDeckStore battleDecks)
    {
        _rooms = rooms;
        _users = users;
        _decks = decks;
        _battleDecks = battleDecks;
    }

    public static bool CanHandle(string path) =>
        path.StartsWith("/shadowverse/open_room/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/shadowverse/open_room_battle/", StringComparison.OrdinalIgnoreCase);

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
            "do_matching" => DoMatching(ownerUdid),
            // set_deck carries the participant's chosen deck_no; stash it so do_matching can resolve the real deck
            "set_deck" => SetDeck(ownerUdid, root),
            "deck_entry" or "ban_deck" or "finish_load"
                => JsonSerializer.Serialize(new { result_reason = 0 }),
            "finish" or "force_finish" => Finish(root),
            "kick_room" or "force_kick_room" => JsonSerializer.Serialize(new { result_reason = 0, room_result = 1 }),
            _ => "{}",
        };
    }

    string SetDeck(string udid, JsonElement root)
    {
        _deckByUdid[udid] = GetInt(root, "deck_no");
        return JsonSerializer.Serialize(new { result_reason = 0 });
    }

    // the client posts its own battle_result here and needs it echoed back: RoomBattleFinishTask.Parse bails unless
    // data is NON-EMPTY (FinishTaskBase.IsResponseDataExist rejects Count==0), leaving Data.RoomMatchFinish.data null,
    // which is what makes the result screen give up with the 3502 go-to-title popup instead of returning to the room
    string Finish(JsonElement root) =>
        JsonSerializer.Serialize(new
        {
            battle_result = GetInt(root, "battle_result"),
            get_class_experience = 0,
            class_experience = 0,
        });

    // open_room_battle/do_matching: the room already holds the battle server + id (from create/enter), so report success.
    // also resolve this participant's chosen deck and hand it to the Battle process via the shared battle_deck table
    string DoMatching(string udid)
    {
        var room = _rooms.FindByUser(udid);
        if (room is null)
            return JsonSerializer.Serialize(new { matching_state = 0, retry_period = 1, timeout_period = 60 });
        WriteBattleDeck(room, udid);
        return JsonSerializer.Serialize(new
        {
            matching_state = room.OwnerUdid == udid ? 3007 : 3004,
            timeout_period = 60,
            retry_period = 1,
            battle_id = room.BattleId,
            node_server_url = room.NodeServerUrl,
            card_master_id = 0,
        });
    }

    void WriteBattleDeck(Room room, string udid)
    {
        if (!_deckByUdid.TryGetValue(udid, out var deckNo))
        {
            Console.WriteLine($"WriteBattleDeck: no set_deck for udid={udid}");
            return;
        }
        var deck = _decks.Get(udid, deckNo);
        var isOwner = room.OwnerUdid == udid;
        Console.WriteLine($"WriteBattleDeck udid={udid} roomId={room.RoomId} isOwner={isOwner} deckNo={deckNo} found={deck is not null} cards={deck?.CardIdArray.Length ?? 0}");
        if (deck is null) return;
        var classId = ClassOf(deck.CardIdArray);
        _battleDecks.Set(new BattleDeck
        {
            RoomId = room.RoomId,
            IsOwner = isOwner,
            ClassId = classId,
            SubClassId = deck.SubClassId,
            // default leader chara_id equals class_id for the 8 base classes; GetClassPrm throws on 0/-1
            CharaId = classId,
            SleeveId = deck.SleeveId,
            LeaderSkinId = deck.LeaderSkinId,
            CardIds = deck.CardIdArray,
            UserName = _users.GetOrCreate(udid).Name,
        });
    }

    // a card's class is its 100,000s digit (verified vs card master clan column). deck class = most common nonzero digit
    static int ClassOf(int[] cardIds)
    {
        var digits = cardIds.Select(c => c / 100000 % 10).Where(d => d is >= 1 and <= 8).ToList();
        return digits.Count > 0 ? digits.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key : 1;
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
