using System.Text.Json.Nodes;

namespace OpenVerse.Battle;

public sealed class BattleHub
{
    readonly SessionManager _sessions;

    public BattleHub(SessionManager sessions) => _sessions = sessions;

    public async Task Dispatch(Session s, string uri, JsonNode? payload, int? ackId)
    {
        var pubSeq = payload?["pubSeq"]?.GetValue<int>() ?? 0;
        switch (uri)
        {
            case "InitNetwork":
                if (ackId is int i1) await s.SendAck(i1, pubSeq);
                await s.SendMsg("InitNetwork", new { });
                break;
            case "RoomCreate":
                if (ackId is int i2) await s.SendAckResult(i2, pubSeq, new { resultCode = 0 });
                break;
            case "RoomEntry":
                if (ackId is int i3) await s.SendAckResult(i3, pubSeq, new { resultCode = 0 });
                var peer = _sessions.Peer(s);
                if (peer is not null)
                {
                    await peer.SendMsg("Matched", MatchedPayload(peer, s, s.BattleId, turnState: 0));
                    await s.SendMsg("Matched", MatchedPayload(s, peer, s.BattleId, turnState: 1));
                }
                break;
            case "Leave":
            case "Release":
            case "ForceRelease":
                if (ackId is int i4) await s.SendAck(i4, pubSeq);
                var p = _sessions.Peer(s);
                if (p is not null) await p.SendMsg("Release", new { });
                break;
            default:
                if (ackId is int i5) await s.SendAck(i5, pubSeq);
                break;
        }
    }

    static object MatchedPayload(Session self, Session oppo, string bid, int turnState) => new
    {
        bid,
        turnState,
        selfInfo = UserInfo(self),
        oppoInfo = UserInfo(oppo),
        selfDeck = Enumerable.Range(0, 40).Select(idx => new { idx, cardId = 100000001 }).ToArray(),
    };

    static object UserInfo(Session s) => new
    {
        rank = 1,
        classId = 1,
        charaId = 1,
        viewerId = long.TryParse(s.ViewerId, out var v) ? v : 0,
        userName = "player_" + s.ViewerId,
        fieldId = 1,
        seed = 12345,
        deckCount = 40,
    };
}
