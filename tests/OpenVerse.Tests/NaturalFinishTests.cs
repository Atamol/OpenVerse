using System.Reflection;
using System.Runtime.CompilerServices;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// nobody reports a natural lethal: the winner only closes its turn and the loser's own report rides a handshake the
// passthrough relay never completes, so the relay authors the result off the leader life it tracks. authoring it from
// inside the handler that just relayed the killing blow is what made the loser lose without ever seeing the attack, so
// the verdict is held until the action closes
[Collection("Sqlite")]
public class NaturalFinishTests
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
        sessions.Add(s);
        return s;
    }

    sealed class Rig
    {
        public BattleHub Hub = null!;
        public Session A = null!, B = null!;

        static object Field(BattleHub h, string name) => typeof(BattleHub).GetField(name, MF)!.GetValue(h)!;

        public void Hold(Session s, int reporterCode, int peerCode) =>
            typeof(BattleHub).GetMethod("HoldVerdict", MF)!.Invoke(Hub, [s, reporterCode, peerCode]);

        public int HeldCount => ((System.Collections.IDictionary)Field(Hub, "_pendingFinish")).Count;

        // set only once BattleFinish has actually gone out
        public bool Decided => ((System.Collections.IDictionary)Field(Hub, "_finished")).Count > 0;
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-fin-{Guid.NewGuid():N}.db");
        var hub = new BattleHub(sessions, new BattleDeckStore(db), new(), new());
        return new Rig
        {
            Hub = hub,
            A = MakeSession(sessions, "sessA", "battle1", "1001"),
            B = MakeSession(sessions, "sessB", "battle1", "1002"),
        };
    }

    [Fact]
    public void LethalDoesNotEndTheMatchOnTheSpot()
    {
        var r = NewRig();
        r.Hold(r.A, 101, 102);

        Assert.Equal(1, r.HeldCount);
        Assert.False(r.Decided);
    }

    // a second killing action in the same sequence must not queue a second result
    [Fact]
    public void HoldsOneVerdictPerBattle()
    {
        var r = NewRig();
        r.Hold(r.A, 101, 102);
        r.Hold(r.B, 102, 101);

        Assert.Equal(1, r.HeldCount);
    }
}
