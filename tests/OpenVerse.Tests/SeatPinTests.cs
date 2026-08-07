using System.Reflection;
using System.Runtime.CompilerServices;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// the client tears the battle socket down between games, so from game 2 on the seat cannot be read off connect order:
// two remote players race to reconnect and the loser gets handed the other one's class, deck, sleeve, name and score
[Collection("Sqlite")]
public class SeatPinTests
{
    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    sealed class Rig
    {
        public BattleHub Hub = null!;
        public SessionManager Sessions = null!;

        public Session Join(string id, string viewerId, string battleId = "room1", string ip = "")
        {
            var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
            SetAuto(s, "Id", id);
            SetAuto(s, "BattleId", battleId);
            SetAuto(s, "ViewerId", viewerId);
            SetAuto(s, "RemoteIp", ip);
            Sessions.Add(s);
            return s;
        }

        public void WriteDeck(string battleId, bool isOwner, string ip) =>
            Decks.Set(new BattleDeck { RoomId = battleId, IsOwner = isOwner, SourceIp = ip, ClassId = 1, CharaId = 1 });

        public BattleDeckStore Decks = null!;

        public void Leave(Session s) => Sessions.Remove(s);

        public void Pin(Session s, bool isOwner) =>
            typeof(BattleHub).GetMethod("PinSeat", MF)!.Invoke(Hub, [s, isOwner]);

        public bool IsOwner(Session s) =>
            (bool)typeof(BattleHub).GetMethod("IsOwner", MF)!.Invoke(Hub, [s])!;
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-seat-{Guid.NewGuid():N}.db");
        var decks = new BattleDeckStore(db);
        return new Rig { Hub = new BattleHub(sessions, decks, new(), new()), Sessions = sessions, Decks = decks };
    }

    [Fact]
    public void PinsTheOwnerFromRoomCreate()
    {
        var r = NewRig();
        var a = r.Join("sessA", "1001");
        var b = r.Join("sessB", "1002");
        r.Pin(a, isOwner: true);

        Assert.True(r.IsOwner(a));
        Assert.False(r.IsOwner(b));
    }

    // RoomEntry names the visitor, which pins the other seat just as well
    [Fact]
    public void PinsTheOwnerFromRoomEntryAlone()
    {
        var r = NewRig();
        var a = r.Join("sessA", "1001");
        var b = r.Join("sessB", "1002");
        r.Pin(b, isOwner: false);

        Assert.True(r.IsOwner(a));
        Assert.False(r.IsOwner(b));
    }

    // the regression: game 2 reconnects in the opposite order, and connect order would hand each player the other's row
    [Fact]
    public void HoldsTheSeatWhenTheVisitorReconnectsFirst()
    {
        var r = NewRig();
        var a = r.Join("sessA", "1001");
        var b = r.Join("sessB", "1002");
        r.Pin(a, isOwner: true);
        r.Leave(a);
        r.Leave(b);

        var b2 = r.Join("sessB2", "1002");
        var a2 = r.Join("sessA2", "1001");

        Assert.Same(b2, r.Sessions.ByBattle("room1").First());
        Assert.True(r.IsOwner(a2));
        Assert.False(r.IsOwner(b2));
    }

    [Fact]
    public void SeatsAreKeptPerRoom()
    {
        var r = NewRig();
        var a = r.Join("sessA", "1001");
        var c = r.Join("sessC", "1001", battleId: "room2");
        r.Pin(a, isOwner: true);
        r.Join("sessD", "1002", battleId: "room2");
        r.Pin(c, isOwner: false);

        Assert.True(r.IsOwner(a));
        Assert.False(r.IsOwner(c));
    }

    // two clients have been seen sending the same cached viewer id. The pin then matches everyone, so both players get
    // the owner's class, deck and name - worse than the race it was meant to fix
    [Fact]
    public void IgnoresThePinWhenBothSocketsClaimTheSameViewer()
    {
        var r = NewRig();
        var a = r.Join("sessA", "1001");
        var b = r.Join("sessB", "1001");
        r.Pin(a, isOwner: true);

        Assert.True(r.IsOwner(a));
        Assert.False(r.IsOwner(b));
    }

    // the source IP is the only discriminator neither client picks for itself, so it settles the seat even when both
    // sockets claim the same viewer and reconnect in the wrong order
    [Fact]
    public void TheSourceIpSettlesItWhenTheViewerIdCannot()
    {
        var r = NewRig();
        r.WriteDeck("room1", isOwner: true, ip: "10.0.0.1");
        r.WriteDeck("room1", isOwner: false, ip: "10.0.0.2");
        var guest = r.Join("sessB", "1001", ip: "10.0.0.2");
        var owner = r.Join("sessA", "1001", ip: "10.0.0.1");

        Assert.Same(guest, r.Sessions.ByBattle("room1").First());
        Assert.True(r.IsOwner(owner));
        Assert.False(r.IsOwner(guest));
    }

    // two players behind one address, so the IP proves nothing and the older rules take over rather than guessing
    [Fact]
    public void OneAddressForBothFallsThrough()
    {
        var r = NewRig();
        r.WriteDeck("room1", isOwner: true, ip: "10.0.0.1");
        r.WriteDeck("room1", isOwner: false, ip: "10.0.0.1");
        var a = r.Join("sessA", "1001", ip: "10.0.0.1");
        r.Join("sessB", "1002", ip: "10.0.0.1");
        r.Pin(a, isOwner: true);

        Assert.True(r.IsOwner(a));
    }

    // with nothing pinned the old rule still applies, so an unpinned room degrades instead of throwing
    [Fact]
    public void FallsBackToConnectOrder()
    {
        var r = NewRig();
        var a = r.Join("sessA", "1001");
        r.Join("sessB", "1002");

        Assert.True(r.IsOwner(a));
    }
}
