using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// projection and spin are the two things only a pair can supply, and both are gated: a host that never built the engine
// must relay exactly as it does today. these pin the gate, since an ungated engine opinion reaches a player
[Collection("Sqlite")]
public class ProjectionTests
{
    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    sealed class Rig
    {
        public BattleHub Hub = null!;
        public Session A = null!;

        public int? Project(int idx, int cardId) =>
            (int?)typeof(BattleHub).GetMethod("Project", MF)!.Invoke(Hub, [A, idx, cardId]);

        public void InjectSpin(JsonObject body) =>
            typeof(BattleHub).GetMethod("InjectSpin", MF)!.Invoke(Hub, [A, body]);
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-proj-{Guid.NewGuid():N}.db");
        var hub = new BattleHub(sessions, new BattleDeckStore(db), new(), new());
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetAuto(s, "Id", "sessA");
        SetAuto(s, "BattleId", "battle1");
        SetAuto(s, "ViewerId", "1001");
        sessions.Add(s);
        return new Rig { Hub = hub, A = s };
    }

    // Observe is the default role and it is supposed to reach no client, so neither of these may answer at it
    [Fact]
    public void ProjectionIsSilentBelowAdviseCost()
    {
        if (Engine.ShadowBridge.Role >= Engine.ShadowBridge.EngineRole.AdviseCost) return;
        Assert.Null(NewRig().Project(1, 101334020));
    }

    [Fact]
    public void SpinIsSilentBelowAnswerBlanks()
    {
        if (Engine.ShadowBridge.Role >= Engine.ShadowBridge.EngineRole.AnswerBlanks) return;
        var body = new JsonObject();
        NewRig().InjectSpin(body);
        Assert.False(body.ContainsKey("spin"));
    }

    // an actor that already stated its own spin owns that number; the relay must not second-guess it
    [Fact]
    public void SpinLeavesAStatedValueAlone()
    {
        var body = new JsonObject { ["spin"] = 3 };
        NewRig().InjectSpin(body);
        Assert.Equal(3, (int)body["spin"]!);
    }

    // with no engine present nothing may be invented: a host that never built the assemblies relays as before
    [Fact]
    public void NothingIsInventedWithoutAnEngine()
    {
        if (Engine.ShadowBridge.Ready) return;
        var r = NewRig();
        var body = new JsonObject();
        r.InjectSpin(body);

        Assert.False(body.ContainsKey("spin"));
        Assert.Null(r.Project(1, 101334020));
    }
}
