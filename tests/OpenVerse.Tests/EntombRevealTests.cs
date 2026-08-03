using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// knownList is a list and the peer looks entries up by index, but the relay only ever put the played card in it. a play
// that also moves ANOTHER card somewhere public left that card unnamed, so the peer resolved it against its own forty
// dummies and drew a Goblin. 葬送 entombing a follower chosen from hand is the case that surfaced it
[Collection("Sqlite")]
public class EntombRevealTests
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
        var db = Path.Combine(Path.GetTempPath(), $"ov-entomb-{Guid.NewGuid():N}.db");
        var s = (Session)RuntimeHelpers.GetUninitializedObject(typeof(Session));
        SetAuto(s, "Id", "sessA");
        SetAuto(s, "BattleId", "battle1");
        SetAuto(s, "ViewerId", "1001");
        SetAuto(s, "RemoteIp", "");
        sessions.Add(s);
        return new Rig { Hub = new BattleHub(sessions, new BattleDeckStore(db), new(), new()), A = s };
    }

    // the shape captured from a real play: idx 24 is the spell, idx 14 is the follower it entombs out of hand
    const string Entomb = """
        {"uri":"PlayActions","type":30,"playIdx":24,
         "orderList":[{"move":{"idx":[24],"isSelf":1,"from":10,"to":30}},
                      {"move":{"idx":[14],"isSelf":1,"from":10,"to":30}}]}
        """;

    static Rig Loaded()
    {
        var r = NewRig();
        r.Ledger[24] = 124334010;
        r.Ledger[14] = 113131030;
        return r;
    }

    [Fact]
    public void TheEntombedFollowerIsNamedAlongsideThePlay()
    {
        var list = Loaded().Reveal(Entomb);

        Assert.NotNull(list);
        Assert.Equal(2, list!.Count);
        Assert.Equal(24, list[0]!["idx"]!.GetValue<int>());
        Assert.Equal(14, list[1]!["idx"]!.GetValue<int>());
        Assert.Equal(113131030, list[1]!["cardId"]!.GetValue<int>());
    }

    // the peer reads these as the opponent's cards, same as the played one
    [Fact]
    public void TheExtraEntryLooksLikeTheOneThatAlreadyWorked()
    {
        var list = Loaded().Reveal(Entomb)!;

        Assert.Equal(0, list[1]!["isSelf"]!.GetValue<int>());
        Assert.Equal(1, list[1]!["is_open"]!.GetValue<int>());
    }

    // the played card is already in the list; naming it twice would put one index in two entries
    [Fact]
    public void ThePlayedCardIsNotListedTwice()
    {
        var list = Loaded().Reveal(Entomb)!;
        Assert.Single(list, e => e!["idx"]!.GetValue<int>() == 24);
    }

    // a draw is private, so a play that also draws must not reveal what it drew
    [Fact]
    public void ADrawAlongsideThePlayStaysHidden()
    {
        var r = Loaded();
        var list = r.Reveal("""
            {"uri":"PlayActions","type":30,"playIdx":24,
             "orderList":[{"move":{"idx":[7],"isSelf":1,"from":0,"to":10}}]}
            """)!;

        Assert.Single(list);
    }

    // the opponent's own moves are not ours to reveal
    [Fact]
    public void APeerSideMoveIsLeftAlone()
    {
        var r = Loaded();
        var list = r.Reveal("""
            {"uri":"PlayActions","type":30,"playIdx":24,
             "orderList":[{"move":{"idx":[14],"isSelf":0,"from":10,"to":30}}]}
            """)!;

        Assert.Single(list);
    }

    // an index nothing can name keeps the old behaviour rather than inventing a card
    [Fact]
    public void AnUnnameableIndexIsSkipped()
    {
        var r = NewRig();
        r.Ledger[24] = 124334010;
        var list = r.Reveal(Entomb)!;

        Assert.Single(list);
        Assert.Equal(24, list[0]!["idx"]!.GetValue<int>());
    }

    // a deck the relay never resolved is padded with the filler, which is the very card the peer would have drawn
    // anyway. stating it turns "I do not know" into "it is a Goblin"
    [Fact]
    public void TheFillerIsNeverStatedAsAnIdentity()
    {
        var r = NewRig();
        r.Ledger[24] = 124334010;
        r.Ledger[14] = 100111010;
        var list = r.Reveal(Entomb)!;

        Assert.Single(list);
    }
}
