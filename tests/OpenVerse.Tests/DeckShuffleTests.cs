using System.Reflection;
using System.Runtime.CompilerServices;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// The client never shuffles, it draws deck slots in order, so draw order is entirely the relay's to decide. It used to
// seed that off (room, viewer) alone, so a rematch in the same room dealt the same cards in the same order.
// the seed still has to survive a mid-battle reconnect, so it is rolled per battle rather than per session
[Collection("Sqlite")]
public class DeckShuffleTests
{
    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    static Session MakeSession(SessionManager sessions, string id, string battleId, string viewerId)
    {
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetAuto(s, "Id", id);
        SetAuto(s, "BattleId", battleId);
        SetAuto(s, "ViewerId", viewerId);
        SetAuto(s, "RemoteIp", "");
        sessions.Add(s);
        return s;
    }

    // 40 distinct ids: a deck of one repeated card shuffles to itself and would pass every assertion here
    static BattleHub NewHub(out SessionManager sessions)
    {
        sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-shuf-{Guid.NewGuid():N}.db");
        var decks = new BattleDeckStore(db);
        foreach (var owner in new[] { true, false })
            decks.Set(new BattleDeck
            {
                RoomId = "room1",
                IsOwner = owner,
                CardIds = [.. Enumerable.Range(0, 40).Select(i => 100111010 + i * 10)],
            });
        return new BattleHub(sessions, decks, new(), new());
    }

    static int[] Deck(BattleHub hub, Session s) =>
        (int[])typeof(BattleHub).GetMethod("ShuffledDeckFor", MF)!.Invoke(hub, [s])!;

    // what Matched does for each player
    static void StartBattle(BattleHub hub, params Session[] players)
    {
        var seeds = (System.Collections.IDictionary)typeof(BattleHub).GetField("_deckSeed", MF)!.GetValue(hub)!;
        var shuffled = (System.Collections.IDictionary)typeof(BattleHub).GetField("_shuffled", MF)!.GetValue(hub)!;
        var roll = typeof(BattleHub).GetMethod("RollSeed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var key = typeof(BattleHub).GetMethod("DeckKey", MF)!;
        foreach (var p in players)
        {
            seeds[key.Invoke(hub, [p])!] = roll.Invoke(null, null)!;
            shuffled.Remove(p.Id);
        }
    }

    [Fact]
    public void TheSameMatchKeepsOneOrderHoweverOftenItIsAsked()
    {
        var hub = NewHub(out var sessions);
        var a = MakeSession(sessions, "sessA", "room1", "1001");
        StartBattle(hub, a);

        Assert.Equal(Deck(hub, a), Deck(hub, a));
    }

    // the deck has to follow the player back in, or a reconnect deals them a different hand mid-battle
    [Fact]
    public void AReconnectKeepsTheDeckItWasDealt()
    {
        var hub = NewHub(out var sessions);
        var a = MakeSession(sessions, "sessA", "room1", "1001");
        StartBattle(hub, a);
        var before = Deck(hub, a);

        // a reconnect is a new socket after the old one closed, so the dead session is gone from the roster first
        sessions.Remove(a);
        var reconnected = MakeSession(sessions, "sessA-again", "room1", "1001");
        Assert.Equal(before, Deck(hub, reconnected));
    }

    // the header viewer_id is not distinct: a copied install sends one cached value for both players, so keying the
    // shuffle on it collided, the second roll overwrote the first and BOTH players were dealt from one order
    [Fact]
    public void TwoPlayersSharingACachedViewerIdStillGetTheirOwnOrder()
    {
        var hub = NewHub(out var sessions);
        var a = MakeSession(sessions, "sessA", "room1", "837123942");
        var b = MakeSession(sessions, "sessB", "room1", "837123942");
        StartBattle(hub, a, b);

        Assert.NotEqual(Deck(hub, a), Deck(hub, b));
    }

    [Fact]
    public void ARematchInTheSameRoomDealsADifferentOrder()
    {
        var hub = NewHub(out var sessions);
        var a = MakeSession(sessions, "sessA", "room1", "1001");
        StartBattle(hub, a);
        var first = Deck(hub, a);

        var again = MakeSession(sessions, "sessA-2", "room1", "1001");
        StartBattle(hub, again);

        Assert.NotEqual(first, Deck(hub, again));
    }

    [Fact]
    public void TwoPlayersInOneRoomDoNotShareAnOrder()
    {
        var hub = NewHub(out var sessions);
        var a = MakeSession(sessions, "sessA", "room1", "1001");
        var b = MakeSession(sessions, "sessB", "room1", "1002");
        StartBattle(hub, a, b);

        Assert.NotEqual(Deck(hub, a), Deck(hub, b));
    }
}
