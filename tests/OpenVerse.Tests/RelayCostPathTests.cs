using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// drives BattleHub's real cost path (UpdateCostState / TryFinalCost / InjectKnownCard) via reflection, so a regression
// in the spellboost cost synthesis fails here with the measured price rather than only surfacing in a live match
[Collection("Sqlite")]
public class RelayCostPathTests
{

    const int DimShift = 101334020;   // base 18, spellboostStep 1
    const int Everyday = 102311050;   // base 3, spellboostStep 1
    const int Rirara = 125211020;     // base 2, Royal, spellboostStep 0
    const int Filler = 100111010;

    static string DataDir() => Fixtures.DataDir() ?? Path.Combine(Path.GetTempPath(), "openverse-no-master");

    const BindingFlags MF = BindingFlags.NonPublic | BindingFlags.Instance;

    static MethodInfo M(string name) => typeof(BattleHub).GetMethod(name, MF)
        ?? throw new MissingMethodException("BattleHub", name);

    static object? GetField(object obj, string name) =>
        obj.GetType().GetField(name, MF)!.GetValue(obj);

    static void SetAuto(object obj, string prop, object val) =>
        obj.GetType().GetField($"<{prop}>k__BackingField", MF)!.SetValue(obj, val);

    // an uninitialized Session (its ctor needs a WebSocket) with the three id fields set and registered in the manager
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
        public SessionManager Sessions = null!;
        public Session A = null!;
        public Session? B;

        public void SeedDeck(Session s, params (int idx, int cardId)[] slots)
        {
            var deck = Enumerable.Repeat(Filler, 40).ToArray();
            foreach (var (idx, cardId) in slots) deck[idx - 1] = cardId;
            var shuffled = (System.Collections.IDictionary)GetField(Hub, "_shuffled")!;
            shuffled[s.Id] = deck;
        }

        public void Charge(Session s, JsonObject body) =>
            M("UpdateCostState").Invoke(Hub, new object[] { s, body });

        public (bool ok, int cost) FinalCost(Session s, int idx, int cardId)
        {
            var args = new object?[] { s, idx, cardId, 0 };
            var ok = (bool)M("TryFinalCost").Invoke(Hub, args)!;
            return (ok, (int)args[3]!);
        }

        // returns the pinned knownList cost, or null when the relay declined to state one (peer keeps master base cost)
        public int? PlayPin(Session s, int playIdx, int type = 30)
        {
            var body = new JsonObject { ["type"] = type, ["playIdx"] = playIdx, ["orderList"] = new JsonArray() };
            M("InjectKnownCard").Invoke(Hub, new object[] { s, "PlayActions", body });
            if (body["knownList"] is not JsonArray kl || kl.Count == 0 || kl[0] is not JsonObject e0) return null;
            return e0["cost"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : null;
        }

        public bool CostBlind(Session s) =>
            ((HashSet<string>)GetField(Hub, "_costBlind")!).Contains(s.Id);

        public bool AsksTheEngine(Session s, int idx, int cardId, int? relayCost)
        {
            var args = new object?[] { s, idx, cardId, relayCost, 0 };
            return (bool)M("TryEngineCostAdvice").Invoke(Hub, args)!;
        }
    }

    Rig NewRig(bool withPeer = false)
    {
        var dir = DataDir();
        var sessions = new SessionManager();
        var db = Path.Combine(Path.GetTempPath(), $"ov-cost-{Guid.NewGuid():N}.db");
        var decks = new BattleDeckStore(db);
        var baseIds = BaseCardIdMap.Load(dir);
        var costs = CardCostMap.Load(dir);
        var hub = new BattleHub(sessions, decks, baseIds, costs);
        var a = MakeSession(sessions, "sessA", "battle1", "1001");
        var rig = new Rig { Hub = hub, Sessions = sessions, A = a };
        if (withPeer) rig.B = MakeSession(sessions, "sessB", "battle1", "1002");
        return rig;
    }

    // an index-list spellboost charge, freshly built each call (nodes can't be re-parented)
    static JsonObject ChargeIdxList(int[] idxs, int isSelf, string? spellboost = "a1", string type = "add", string? cost = null)
    {
        var idxArr = new JsonArray();
        foreach (var i in idxs) idxArr.Add(i);
        var alter = new JsonObject { ["idx"] = idxArr, ["isSelf"] = isSelf, ["type"] = type };
        if (spellboost is not null) alter["spellboost"] = spellboost;
        if (cost is not null) alter["cost"] = cost;
        return new JsonObject { ["orderList"] = new JsonArray { new JsonObject { ["alter"] = alter } } };
    }

    // headline assertions so a regression shows the measured value in the failure message
    [Fact]
    public void DimensionShift_18Charges_PinsZero()
    {
        if (!File.Exists(Path.Combine(DataDir(), "card_master_full.csv.gz"))) return;
        var r = NewRig();
        r.SeedDeck(r.A, (1, DimShift));
        for (int k = 0; k < 18; k++) r.Charge(r.A, ChargeIdxList(new[] { 1 }, isSelf: 1));
        var (ok, cost) = r.FinalCost(r.A, 1, DimShift);
        var pin = r.PlayPin(r.A, 1);
        Assert.True(ok, "TryFinalCost declined for a fully-boosted Dimension Shift");
        Assert.Equal(0, cost);
        Assert.Equal(0, pin);
    }

    // Battle 78719 in openverse_20260804-183834.log: Rirara was drawn with a -1 discount and later caught a charge from
    // an unrelated effect. The relay dropped the discount, stated base 2, and the peer refused the play at
    // "IsPlayCard PPover Pp1useCost2cardId125211020idx36" - the play simply never happened on the other screen.
    // A charge lands on every card in the target set, so having one says nothing about whether a card discounts on it
    [Fact]
    public void ACostModSurvivesACharge_OnACardWithNoSpellboostDiscount()
    {
        if (!File.Exists(Path.Combine(DataDir(), "card_master_full.csv.gz"))) return;
        var r = NewRig();
        r.SeedDeck(r.A, (36, Rirara));
        r.Charge(r.A, ChargeIdxList(new[] { 36 }, isSelf: 1, spellboost: null, cost: "a-1"));
        r.Charge(r.A, ChargeIdxList(new[] { 36 }, isSelf: 1));

        var (ok, cost) = r.FinalCost(r.A, 36, Rirara);
        Assert.True(ok, "TryFinalCost declined a card that carries a real discount and no spellboost rule");
        Assert.Equal(1, cost);
    }

    // a price the relay proved itself is never handed to the engine: the engine is there for the silence, not to
    // second-guess arithmetic that already works
    [Fact]
    public void NeverOverrulesAPriceTheRelayProved()
    {
        if (!File.Exists(Path.Combine(DataDir(), "card_master_full.csv.gz"))) return;
        var r = NewRig();
        r.SeedDeck(r.A, (5, Everyday));
        r.Charge(r.A, ChargeIdxList(new[] { 5 }, isSelf: 1));

        Assert.False(r.AsksTheEngine(r.A, 5, Everyday, relayCost: 2));
    }

    // The peer subtracts the master base cost from the actor's Pp whenever no cost is stated, so silence on a
    // discounted card drains it until plays start being refused. The engine is asked for every silence, not just the
    // group-idx one, and declines here only because no engine is running in this fixture
    [Fact]
    public void AsksTheEngineWheneverItHasNoPriceOfItsOwn()
    {
        if (!File.Exists(Path.Combine(DataDir(), "card_master_full.csv.gz"))) return;
        var r = NewRig();
        r.SeedDeck(r.A, (5, Everyday));

        Assert.False(r.CostBlind(r.A));
        Assert.False(r.AsksTheEngine(r.A, 5, Everyday, relayCost: null));

        var alter = new JsonObject { ["idx"] = "g1", ["isSelf"] = 1, ["type"] = "add", ["cost"] = "a-2" };
        r.Charge(r.A, new JsonObject { ["orderList"] = new JsonArray { new JsonObject { ["alter"] = alter } } });
        Assert.True(r.CostBlind(r.A));
        Assert.Null(r.PlayPin(r.A, 5));
    }

    [Fact]
    public void EverydayCard_2Charges_PinsOne()
    {
        if (!File.Exists(Path.Combine(DataDir(), "card_master_full.csv.gz"))) return;
        var r = NewRig();
        r.SeedDeck(r.A, (5, Everyday));
        for (int k = 0; k < 2; k++) r.Charge(r.A, ChargeIdxList(new[] { 5 }, isSelf: 1));
        var (ok, cost) = r.FinalCost(r.A, 5, Everyday);
        var pin = r.PlayPin(r.A, 5);
        Assert.True(ok, "TryFinalCost declined for a 2x-boosted everyday card");
        Assert.Equal(1, cost);
        Assert.Equal(1, pin);
    }
}
