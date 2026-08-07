using System.Reflection;
using System.Runtime.CompilerServices;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// The client takes its own id from Matched's selfInfo.viewerId, not from its cache, so the relay owns it. anything the
// client receives shaped {vid, value} - spin among them - is dropped unless the vid is that client's own, so two seats
// sharing one id silently discard every such value. A copied install sends the same cached id for both players
[Collection("Sqlite")]
public class ViewerIdTests
{
    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    sealed class Rig
    {
        public BattleHub Hub = null!;
        public SessionManager Sessions = null!;
        public BattleDeckStore Decks = null!;

        public Session Join(string id, string viewerId, string ip = "")
        {
            var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
            SetAuto(s, "Id", id);
            SetAuto(s, "BattleId", "room1");
            SetAuto(s, "ViewerId", viewerId);
            SetAuto(s, "RemoteIp", ip);
            Sessions.Add(s);
            return s;
        }

        public void WriteDeck(bool isOwner, string ip) =>
            Decks.Set(new BattleDeck { RoomId = "room1", IsOwner = isOwner, SourceIp = ip, ClassId = 1, CharaId = 1 });

        public long Vid(Session s) => (long)typeof(BattleHub).GetMethod("VidOf", MF)!.Invoke(Hub, [s])!;
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-vid-{Guid.NewGuid():N}.db");
        var decks = new BattleDeckStore(db);
        return new Rig { Hub = new BattleHub(sessions, decks, new(), new()), Sessions = sessions, Decks = decks };
    }

    [Fact]
    public void DistinctClientsKeepTheirOwnIds()
    {
        var r = NewRig();
        var a = r.Join("sessA", "837123942");
        var b = r.Join("sessB", "648872311");

        Assert.Equal(837123942, r.Vid(a));
        Assert.Equal(648872311, r.Vid(b));
    }

    // the copied-install case: one cached id for both, so the visitor is moved off it and the owner keeps its own
    [Fact]
    public void ACopiedInstallStillGetsTwoDistinctIds()
    {
        var r = NewRig();
        r.WriteDeck(isOwner: true, ip: "10.0.0.1");
        r.WriteDeck(isOwner: false, ip: "10.0.0.2");
        var owner = r.Join("sessA", "837123942", ip: "10.0.0.1");
        var guest = r.Join("sessB", "837123942", ip: "10.0.0.2");

        Assert.NotEqual(r.Vid(owner), r.Vid(guest));
        Assert.Equal(837123942, r.Vid(owner));
    }

    // whichever seat asks, the pair has to agree on who is who, or the two clients disagree about each other's id
    [Fact]
    public void BothSeatsAgreeOnTheSamePairOfIds()
    {
        var r = NewRig();
        r.WriteDeck(isOwner: true, ip: "10.0.0.1");
        r.WriteDeck(isOwner: false, ip: "10.0.0.2");
        var owner = r.Join("sessA", "555", ip: "10.0.0.1");
        var guest = r.Join("sessB", "555", ip: "10.0.0.2");

        Assert.Equal(r.Vid(owner), r.Vid(owner));
        Assert.Equal(r.Vid(guest), r.Vid(guest));
        Assert.NotEqual(r.Vid(owner), r.Vid(guest));
    }

    // a client that sends nothing usable must still get a positive id: 0 would match nothing and drop every {vid,value}
    [Fact]
    public void AnUnparseableIdStillBecomesSomethingUsable()
    {
        var r = NewRig();
        var a = r.Join("sessA", "not-a-number");

        Assert.True(r.Vid(a) > 0);
    }
}
