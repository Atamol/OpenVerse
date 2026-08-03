using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// a token minted mid-match has no deck slot, so the ledger built from the deck can never name it and its index runs
// past 40. the actor does say what it is, in orderList's `add` record, but orderList has no readers on the client, so
// the identity dies on arrival unless the relay moves it somewhere the client reads
[Collection("Sqlite")]
public class TokenRevealTests
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

        public void Learn(JsonObject body) =>
            typeof(BattleHub).GetMethod("LearnAddedCards", MF)!.Invoke(Hub, [A, body]);

        public JsonArray? Name(JsonObject body)
        {
            Learn(body);
            typeof(BattleHub).GetMethod("InjectKnownCard", MF)!.Invoke(Hub, [A, "PlayActions", body]);
            return body["knownList"] as JsonArray;
        }
    }

    static Rig NewRig()
    {
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-token-{Guid.NewGuid():N}.db");
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetAuto(s, "Id", "sessA");
        SetAuto(s, "BattleId", "battle1");
        SetAuto(s, "ViewerId", "1001");
        SetAuto(s, "RemoteIp", "");
        sessions.Add(s);
        var r = new Rig { Hub = new BattleHub(sessions, new BattleDeckStore(db), new(), new()), A = s };
        r.Ledger[2] = 122241010;
        return r;
    }

    // the message exactly as the relay saw it: playing idx 2 mints two tokens at 41 and 42
    static JsonObject Summon() => JsonNode.Parse("""
        {"uri":"PlayActions","type":30,"playIdx":2,
         "orderList":[{"metamorphose":{"idx":[2],"isSelf":1,"after":{"cardId":800244120}}},
                      {"move":{"idx":[2],"isSelf":1,"from":10,"to":30}},
                      {"add":{"idx":[41],"isSelf":1,"card":{"cardId":900211020}}},
                      {"move":{"idx":[41],"isSelf":1,"from":50,"to":20}},
                      {"add":{"idx":[42],"isSelf":1,"card":{"cardId":900211010}}},
                      {"move":{"idx":[42],"isSelf":1,"from":50,"to":20}}]}
        """)!.AsObject();

    [Fact]
    public void TheLedgerLearnsATokenFromItsAddRecord()
    {
        var r = NewRig();
        r.Learn(Summon());

        Assert.Equal(900211020, r.Ledger[41]);
        Assert.Equal(900211010, r.Ledger[42]);
    }

    // and once it is learned, the ordinary naming pass carries it to the peer
    [Fact]
    public void ASummonedTokenIsNamedForThePeer()
    {
        var list = NewRig().Name(Summon())!;

        Assert.Contains(list, e => e!["idx"]!.GetValue<int>() == 41 && e["cardId"]!.GetValue<int>() == 900211020);
        Assert.Contains(list, e => e!["idx"]!.GetValue<int>() == 42 && e["cardId"]!.GetValue<int>() == 900211010);
    }

    // the peer's own tokens are not ours to relabel
    [Fact]
    public void APeerSideAddIsIgnored()
    {
        var r = NewRig();
        r.Learn(JsonNode.Parse("""
            {"orderList":[{"add":{"idx":[41],"isSelf":0,"card":{"cardId":900211020}}}]}
            """)!.AsObject());

        Assert.False(r.Ledger.ContainsKey(41));
    }

    // a later message about the same index must resolve without another add record
    [Fact]
    public void TheIdentityOutlivesTheMessageThatMintedIt()
    {
        var r = NewRig();
        r.Learn(Summon());

        var later = JsonNode.Parse("""
            {"uri":"PlayActions","type":10,"playIdx":41,
             "orderList":[{"move":{"idx":[41],"isSelf":1,"from":20,"to":30}}]}
            """)!.AsObject();
        typeof(BattleHub).GetMethod("InjectKnownCard", MF)!.Invoke(r.Hub, [r.A, "PlayActions", later]);

        Assert.Null(later["knownList"]);   // an attack is not a hand play, so no knownList - but the ledger knows it
        Assert.Equal(900211020, r.Ledger[41]);
    }

    [Fact]
    public void AnAddWithNoCardIdIsIgnored()
    {
        var r = NewRig();
        r.Learn(JsonNode.Parse("""{"orderList":[{"add":{"idx":[41],"isSelf":1,"card":{}}}]}""")!.AsObject());

        Assert.False(r.Ledger.ContainsKey(41));
    }
}
