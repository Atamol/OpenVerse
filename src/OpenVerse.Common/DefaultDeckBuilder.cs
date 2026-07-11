using System.Text.Json;

namespace OpenVerse.Common;

// Builds one starter deck per class from the shadowverse card data, so the client's
// DefaultDeck fallback (deck/info default_deck_list) has real card_ids that exist in card_master.
public static class DefaultDeckBuilder
{
    static readonly Dictionary<int, string> ClanName = new()
    {
        [1] = "エルフ", [2] = "ロイヤル", [3] = "ウィッチ", [4] = "ドラゴン",
        [5] = "ネクロマンサー", [6] = "ヴァンパイア", [7] = "ビショップ", [8] = "ネメシス",
    };

    public sealed record DefaultDeck(int ClassId, int[] CardIdArray);

    public static List<DefaultDeck> Build(Stream cardJson)
    {
        using var doc = JsonDocument.Parse(cardJson);
        var root = doc.RootElement;

        // group card_ids by craft, followers/spells only, exclude tokens
        var byCraft = new Dictionary<string, List<int>>();
        foreach (var e in root.EnumerateObject())
        {
            var c = e.Value;
            var type = c.GetProperty("type_").GetString() ?? "";
            var exp = c.GetProperty("expansion_").GetString() ?? "";
            if (exp == "トークン") continue;
            if (type != "フォロワー" && type != "スペル" && type != "アミュレット") continue;
            var craft = c.GetProperty("craft_").GetString() ?? "";
            var id = c.GetProperty("id_").GetInt32();
            (byCraft.TryGetValue(craft, out var l) ? l : byCraft[craft] = new()).Add(id);
        }

        var neutral = byCraft.GetValueOrDefault("ニュートラル") ?? new();
        var decks = new List<DefaultDeck>();
        foreach (var (classId, craft) in ClanName)
        {
            var pool = new List<int>();
            pool.AddRange(byCraft.GetValueOrDefault(craft) ?? new());
            pool.AddRange(neutral);
            var cards = new List<int>();
            foreach (var id in pool)
            {
                if (cards.Count >= 40) break;
                for (int k = 0; k < 3 && cards.Count < 40; k++) cards.Add(id);
            }
            while (cards.Count < 40 && pool.Count > 0) cards.Add(pool[0]);
            if (cards.Count == 40) decks.Add(new DefaultDeck(classId, cards.ToArray()));
        }
        return decks;
    }
}
