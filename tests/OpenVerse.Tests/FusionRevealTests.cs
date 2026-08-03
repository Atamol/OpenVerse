using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// naming by DESTINATION was the wrong axis. a fusion consumes its ingredients into FusionIngredient(60), emits no
// `move` record at all, and the destination pass cannot see it. left unnamed, the peer resolves the index against its
// own placeholders, IsFusionable finds nothing matching {tribe=lord} against a TribeType.ALL filler, the fusion
// no-ops on one machine, and every later condition reading fusion_ingrediented_card_list is false there
[Collection("Sqlite")]
public class FusionRevealTests
{
    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    sealed class Rig
    {
        public BattleHub Hub = null!;
        public Session A = null!;

        public Dictionary<int, int> Ledger =>
            (Dictionary<int, int>)typeof(BattleHub).GetMethod("LedgerFor", MF)!.Invoke(Hub, [A])!;

        public JsonArray? Reveal(string json)
        {
            var body = JsonNode.Parse(json)!.AsObject();
            typeof(BattleHub).GetMethod("InjectKnownCard", MF)!.Invoke(Hub, [A, "PlayActions", body]);
            return body["knownList"] as JsonArray;
        }
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-fuse-{Guid.NewGuid():N}.db");
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetAuto(s, "Id", "sessA");
        SetAuto(s, "BattleId", "battle1");
        SetAuto(s, "ViewerId", "1001");
        SetAuto(s, "RemoteIp", "");
        sessions.Add(s);
        var r = new Rig { Hub = new BattleHub(sessions, new BattleDeckStore(db), new(), new()), A = s };
        r.Ledger[5] = 119241030;
        r.Ledger[1] = 121231010;
        r.Ledger[2] = 114241010;
        return r;
    }

    // the message as the relay actually saw it, minus the knownList it will rebuild
    const string Fusion = """
        {"uri":"PlayActions","type":40,"playIdx":5,
         "keyAction":[{"type":4,"cardId":119241030,"selectCard":[121231010]}],
         "orderList":[{"fusion":{"idx":[5],"isSelf":1,"ingredients":[1],"attachTarget":"119241030|2|0"}}],
         "oppoTargetList":[{"targetIdx":1,"isSelf":1}]}
        """;

    [Fact]
    public void TheFusionIngredientIsNamed()
    {
        var list = NewRig().Reveal(Fusion);

        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        Assert.Contains(list, e => e!["idx"]!.GetValue<int>() == 1 && e["cardId"]!.GetValue<int>() == 121231010);
    }

    // the destination pass cannot reach it: a fusion emits no move record at all
    [Fact]
    public void TheDestinationPassAloneWouldHaveMissedIt()
    {
        var body = JsonNode.Parse(Fusion)!.AsObject();
        Assert.DoesNotContain(body["orderList"]!.AsArray(), o => o!.AsObject().ContainsKey("move"));
    }

    // isSelf is sender-relative: 0 is the peer's own card, which it already knows in full
    [Fact]
    public void APeerSideTargetIsNotRevealed()
    {
        var list = NewRig().Reveal("""
            {"uri":"PlayActions","type":31,"playIdx":5,
             "oppoTargetList":[{"targetIdx":2,"isSelf":0,"selectSkillIndex":[1]}]}
            """)!;

        Assert.Single(list);
    }

    // the same rule covers a targeted play, not just fusion: any actor-side index the peer must look up
    [Fact]
    public void AnActorSideTargetOnAnyPlayIsNamed()
    {
        var list = NewRig().Reveal("""
            {"uri":"PlayActions","type":31,"playIdx":5,
             "oppoTargetList":[{"targetIdx":2,"isSelf":1}]}
            """)!;

        Assert.Equal(2, list.Count);
        Assert.Equal(114241010, list[1]!["cardId"]!.GetValue<int>());
    }

    [Fact]
    public void ThePlayedCardIsNotNamedTwice()
    {
        var list = NewRig().Reveal("""
            {"uri":"PlayActions","type":40,"playIdx":5,
             "oppoTargetList":[{"targetIdx":5,"isSelf":1}],
             "orderList":[{"fusion":{"idx":[5],"isSelf":1,"ingredients":[5]}}]}
            """)!;

        Assert.Single(list);
    }

    // the filler is what an unresolved deck is padded with, so stating it would claim the peer's own placeholder
    [Fact]
    public void TheFillerIsNeverStated()
    {
        var r = NewRig();
        r.Ledger[1] = 100111010;
        var list = r.Reveal(Fusion)!;

        Assert.Single(list);
    }
}
