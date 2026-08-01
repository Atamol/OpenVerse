using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenVerse.Engine;
using Xunit.Abstractions;

namespace OpenVerse.Tests;

// The offline replay rig that found the shadow's drift lives outside the repo and carries its own copies of the ingest
// path, so a green run there says nothing about the code that actually ships. This drives the same captured match
// through ShadowBridge, which is the seam the relay uses, and asks the price the relay would ask for
//
// assets/battle_capture.tsv is a real match: session id, uri, body JSON per row. board data only, no account fields
[Collection("Engine")]
public class ShadowCostFidelityTests
{
    readonly ITestOutputHelper _out;
    public ShadowCostFidelityTests(ITestOutputHelper o) => _out = o;

    const string SelfSession = "8ec5668bd6ed";
    const int DeckSize = 40;
    const int Filler = 100111010;

    // 18 base cost, and the capture charges it 16 times, so the actor played it for 2
    const int DimensionShift = 101334020;
    const int DimensionShiftIdx = 2;
    const int WirePrice = 2;

    sealed record Row(int Line, string To, string Uri, JsonObject Body)
    {
        public bool Relayed => Uri is "TurnStart" or "PlayActions" or "TurnEndActions" or "TurnEnd" or "TurnEndFinal"
                                  or "SelectSkill" or "SelectObject";
        // a relayed message is authored by the peer of whoever it was sent to
        public bool IsPlayer => (Relayed ? (To == SelfSession ? "peer" : SelfSession) : To) == SelfSession;
    }

    static string? Csv()
    {
        var gz = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release", "data", "card_master_full.csv.gz");
        if (!File.Exists(gz)) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll"))) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll"))) return null;
        using var fs = File.OpenRead(gz);
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }

    static List<Row>? Capture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "battle_capture.tsv");
        if (!File.Exists(path)) return null;
        var rows = new List<Row>();
        var line = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            line++;
            var p = raw.Split('\t');
            if (p.Length < 3 || p[1].Length == 0 || p[2].Length == 0) continue;
            if (JsonNode.Parse(p[2]) is JsonObject body) rows.Add(new Row(line, p[0], p[1], body));
        }
        return rows;
    }

    static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    // the wire never lists a deck, so recover idx -> cardId from every reveal the capture carries and fill the rest
    static (int[] self, int[] oppo) Decks(List<Row> rows)
    {
        Dictionary<int, int> self = new(), oppo = new();

        static void Put(Dictionary<int, int> d, int idx, int cardId)
        {
            if (idx <= 0 || idx > DeckSize || cardId <= 0 || cardId == Filler) return;
            d.TryAdd(idx, cardId);
        }

        foreach (var r in rows)
        {
            // Deal / Swap / Ready are replies to the recipient, so isSelf 1 is the recipient's own card
            var owner = r.To == SelfSession ? self : oppo;
            if (r.Body["cards"] is JsonArray cards)
                foreach (var c in cards.OfType<JsonObject>())
                    if (Int(c["isSelf"]) == 1) Put(owner, Int(c["idx"]), Int(c["cardId"]));

            var side = r.IsPlayer ? self : oppo;
            if (r.Body["knownList"] is JsonArray kl)
                foreach (var e in kl.OfType<JsonObject>()) Put(side, Int(e["idx"]), Int(e["cardId"]));
            if (r.Body["uList"] is JsonArray ul)
                foreach (var e in ul.OfType<JsonObject>())
                    if (e["cardId"] is not null && e["idxList"] is JsonArray idxs && idxs.Count > 0)
                        Put(side, Int(idxs[0]), Int(e["cardId"]));
        }

        static int[] Build(Dictionary<int, int> known) =>
            [.. Enumerable.Range(1, DeckSize).Select(i => known.GetValueOrDefault(i, Filler))];

        return (Build(self), Build(oppo));
    }

    // Deal names an idx per position, Swap replaces the redrawn ones
    static (int[] self, int[] oppo) Hands(List<Row> rows)
    {
        int[] self = [1, 2, 3], oppo = [1, 2, 3];
        foreach (var r in rows.Where(r => r.Uri is "Deal" or "Swap"))
        {
            var h = r.To == SelfSession ? self : oppo;
            if (r.Body["cards"] is not JsonArray cards) continue;
            foreach (var c in cards.OfType<JsonObject>())
            {
                if (Int(c["isSelf"]) != 1) continue;
                var pos = Int(c["pos"]);
                if (pos is >= 0 and < 3) h[pos] = Int(c["idx"]);
            }
        }
        return (self, oppo);
    }

    [Fact]
    public void PricesTheCapturedSpellboostPlayExactlyAsTheWireDid()
    {
        if (Csv() is not { } csv || Capture() is not { } rows) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);

        var (selfDeck, oppoDeck) = Decks(rows);
        var (selfHand, oppoHand) = Hands(rows);
        Assert.Equal(DimensionShift, selfDeck[DimensionShiftIdx - 1]);

        // no deck mirror: installing one from the capture's idxChangeSeed leaves the shadow 3 charges short here, so the
        // relay does not install it either until a capture says which way round is in step with the players
        var log = new List<string>();
        ShadowBridge.Begin(7, playerFirst: true, selfDeck, oppoDeck, selfHand, oppoHand, log.Add);
        Assert.True(ShadowBridge.WaitIdle(), "the shadow never finished starting");

        try
        {
            int? priced = null;
            foreach (var r in rows)
            {
                // read before ingesting: this is where the relay asks, with the play still in hand
                if (priced is null && r.Uri == "PlayActions" && r.IsPlayer
                    && Int(r.Body["playIdx"]) == DimensionShiftIdx
                    && ShadowBridge.TryCostOf(isSelfPlayer: true, DimensionShiftIdx, out var live))
                    priced = live;

                ShadowBridge.Observe(r.Uri, r.Body, r.IsPlayer, log.Add);
                Assert.True(ShadowBridge.WaitIdle(), $"the shadow stalled on row {r.Line} ({r.Uri})");
            }

            foreach (var l in log.TakeLast(20)) _out.WriteLine(l);
            Assert.Equal(WirePrice, priced);

            // the reveal path the relay uses at Observe: if the trust gate silences this, summoned cards stop
            // resolving for the peer, which is worse than the blank it was added to avoid
            Assert.True(ShadowBridge.TryCardIdOf(isSelfPlayer: true, DimensionShiftIdx, out var revealed),
                "the shadow declined to name a card it holds");
            _out.WriteLine($"CardIdOf(idx {DimensionShiftIdx}) = {revealed}");
        }
        finally
        {
            ShadowBridge.End((_, _) => { });
            ShadowBridge.WaitIdle();
        }
    }
}
