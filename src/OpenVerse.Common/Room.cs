using System.Collections.Concurrent;

namespace OpenVerse.Common;

public sealed class Room
{
    public string RoomId { get; set; } = "";
    public string BattleId { get; set; } = "";
    public string OwnerUdid { get; set; } = "";
    public string? VisitorUdid { get; set; }
    public int BattleType { get; set; }
    public int BattleRule { get; set; }
    public int DeckFormat { get; set; }
    public int TwoPickType { get; set; }
    public bool CanFriendWatch { get; set; }
    public bool CanGuildWatch { get; set; }
    public string NodeServerUrl { get; set; } = "";
}

public sealed class RoomStore
{
    readonly ConcurrentDictionary<string, Room> _rooms = new();

    public string NodeServerUrl { get; set; } = "127.0.0.1:3001";

    public Room Create(string ownerUdid, int battleType, int battleRule, int deckFormat, int twoPickType, bool canFriendWatch, bool canGuildWatch)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            var roomId = Random.Shared.Next(10000, 100000).ToString();
            var room = new Room
            {
                RoomId = roomId,
                BattleId = Guid.NewGuid().ToString("N"),
                OwnerUdid = ownerUdid,
                BattleType = battleType,
                BattleRule = battleRule,
                DeckFormat = deckFormat,
                TwoPickType = twoPickType,
                CanFriendWatch = canFriendWatch,
                CanGuildWatch = canGuildWatch,
                NodeServerUrl = NodeServerUrl,
            };
            if (_rooms.TryAdd(roomId, room)) return room;
        }
        throw new InvalidOperationException("failed to allocate a unique room_id");
    }

    public Room? Get(string roomId) => _rooms.TryGetValue(roomId, out var r) ? r : null;

    public Room? FindByOwner(string ownerUdid) =>
        _rooms.Values.FirstOrDefault(r => r.OwnerUdid == ownerUdid);

    public Room? Enter(string visitorUdid, string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var r)) return null;
        r.VisitorUdid = visitorUdid;
        return r;
    }

    public void Leave(string roomId) { if (_rooms.TryGetValue(roomId, out var r)) r.VisitorUdid = null; }

    public bool Close(string roomId) => _rooms.TryRemove(roomId, out _);
}
