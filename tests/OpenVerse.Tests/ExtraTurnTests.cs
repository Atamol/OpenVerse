using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// the turn starts when the relay echoes a Judge back to a client. a client only emits Judge when it is NOT its turn, so
// normally the echo goes to the sender. an extra turn inverts that: Dimension Shift makes the caster end its turn like
// any other, so the OPPONENT emits the Judge and the turn has to go back to the caster. echoing to the sender left the
// caster staring at a greyed-out end-turn button while the opponent's screen showed the caster's turn running
[Collection("Sqlite")]
public class ExtraTurnTests
{
    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static MethodInfo M(string name) => typeof(BattleHub).GetMethod(name, MF)
        ?? throw new MissingMethodException("BattleHub", name);

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

        public void OwnsTheTurn(Session s) =>
            ((System.Collections.IDictionary)typeof(BattleHub).GetField("_turnOwner", MF)!.GetValue(Hub)!)[s.BattleId] = s;

        public Session? TurnGoesTo(Session s) => (Session?)M("TurnGoesTo").Invoke(Hub, [s]);

        public void Play(Session s, JsonObject body) => M("NoteExtraTurn").Invoke(Hub, [s, body]);
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-ex-{Guid.NewGuid():N}.db");
        var hub = new BattleHub(sessions, new BattleDeckStore(db), new(), new());
        return new Rig
        {
            Hub = hub,
            A = MakeSession(sessions, "sessA", "battle1", "1001"),
            B = MakeSession(sessions, "sessB", "battle1", "1002"),
        };
    }

    // the grant rides inside orderList on the play that won it, alongside move/alter, and isSelf is actor-relative
    static JsonObject ExtraTurn(int count, int isSelf = 1) => new()
    {
        ["orderList"] = new JsonArray
        {
            new JsonObject { ["move"] = new JsonObject { ["idx"] = new JsonArray { 4 }, ["to"] = 30 } },
            new JsonObject { ["exTurn"] = new JsonObject { ["count"] = count, ["isSelf"] = isSelf } },
        },
    };

    [Fact]
    public void HandsTheTurnToWhoeverDoesNotHaveIt()
    {
        var r = NewRig();
        r.OwnsTheTurn(r.A);
        Assert.Same(r.B, r.TurnGoesTo(r.B));
    }

    [Fact]
    public void DropsARepeatedJudgeFromTheTurnOwner()
    {
        var r = NewRig();
        r.OwnsTheTurn(r.A);
        Assert.Null(r.TurnGoesTo(r.A));
    }

    // the caster ends its turn, so it is the OPPONENT that emits the Judge for that turn end
    [Fact]
    public void SendsTheTurnBackToTheCasterWhenTheOpponentJudges()
    {
        var r = NewRig();
        r.OwnsTheTurn(r.A);
        r.Play(r.A, ExtraTurn(1));

        Assert.Same(r.A, r.TurnGoesTo(r.B));
        // one grant, one turn: the next Judge from B is an ordinary handoff to B
        Assert.Same(r.B, r.TurnGoesTo(r.B));
    }

    [Fact]
    public void CountsEveryGrantedTurn()
    {
        var r = NewRig();
        r.OwnsTheTurn(r.A);
        r.Play(r.A, ExtraTurn(2));

        Assert.Same(r.A, r.TurnGoesTo(r.B));
        Assert.Same(r.A, r.TurnGoesTo(r.B));
        Assert.Same(r.B, r.TurnGoesTo(r.B));
    }

    // isSelf 0 means the play granted the turn to the other side
    [Fact]
    public void CreditsTheGrantToTheSideItNames()
    {
        var r = NewRig();
        r.OwnsTheTurn(r.A);
        r.Play(r.A, ExtraTurn(1, isSelf: 0));

        Assert.Same(r.B, r.TurnGoesTo(r.A));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void IgnoresAGrantOfNothing(int count)
    {
        var r = NewRig();
        r.OwnsTheTurn(r.A);
        r.Play(r.A, ExtraTurn(count));

        Assert.Null(r.TurnGoesTo(r.A));
    }
}
