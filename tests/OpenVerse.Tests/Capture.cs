using System.Text.Json.Nodes;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// assets/battle_capture.tsv is a real match between two real clients, recorded as this server relayed it: session id,
// uri, body JSON per row. Board data only, no account fields
static class Capture
{
    public const string SelfSession = "8ec5668bd6ed";
    public const int DeckSize = 40;

    // what the relay put on the wire for a card it could not name AT THE TIME THIS WAS RECORDED. The live placeholder
    // is MirrorPair.Dummy now, but the bytes on disk still carry the old value, so reading the capture needs this one
    public const int WireFiller = 100111010;

    public sealed record Row(int Line, string To, string Uri, JsonObject Body)
    {
        public bool Relayed => Uri is "TurnStart" or "PlayActions" or "TurnEndActions" or "TurnEnd" or "TurnEndFinal"
                                  or "SelectSkill" or "SelectObject";
        // a relayed message is authored by the peer of whoever it was sent to
        public bool IsPlayer => (Relayed ? (To == SelfSession ? "peer" : SelfSession) : To) == SelfSession;
    }

    public static string? Csv() => Fixtures.CardMasterCsv();

    public static List<Row>? Rows()
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

    public static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    // the wire never lists a deck, so recover idx -> cardId from every reveal the capture carries and fill the rest
    public static (int[] self, int[] oppo) Decks(List<Row> rows)
    {
        Dictionary<int, int> self = new(), oppo = new();

        static void Put(Dictionary<int, int> d, int idx, int cardId)
        {
            if (idx <= 0 || idx > DeckSize || cardId <= 0 || cardId == WireFiller) return;
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

        // a slot the capture never revealed gets the dummy rather than the old wire filler: that filler is Water Fairy,
        // and forty Last Words the client never had is exactly the drift this rig exists to measure
        static int[] Build(Dictionary<int, int> known) =>
            [.. Enumerable.Range(1, DeckSize).Select(i => known.GetValueOrDefault(i, MirrorPair.Dummy))];

        return (Build(self), Build(oppo));
    }

    // Deal names an idx per position, Swap replaces the redrawn ones
    public static (int[] self, int[] oppo) Hands(List<Row> rows)
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
}
