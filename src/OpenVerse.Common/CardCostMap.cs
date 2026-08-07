using System.IO.Compression;

namespace OpenVerse.Common;

public readonly record struct CardCost(int BaseCost, int SpellboostStep);

// The spellboost step is the one cost change the actor never puts on the wire, since
// NetworkSkill_cost_change.IsSend is false while the owner sits face-down in hand
public static class CardCostMap
{
    const int CardIdCol = 0, CostCol = 10, SkillCol = 17, TimingCol = 18, TargetCol = 20, OptionCol = 21;

    public static Dictionary<int, CardCost> Load(string dataDir)
    {
        var map = new Dictionary<int, CardCost>();
        var path = Path.Combine(dataDir, "card_master_full.csv.gz");
        if (!File.Exists(path))
        {
            Console.WriteLine($"CardCostMap: {path} missing, cost synthesis disabled");
            return map;
        }
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            while (sr.ReadLine() is { } line)
            {
                var f = BaseCardIdMap.SplitCsv(line);
                if (f.Count <= OptionCol) continue;
                if (!int.TryParse(f[CardIdCol], out var id) || !int.TryParse(f[CostCol], out var cost)) continue;
                // some rows carry a -1/-99 sentinel instead of a cost, and pinning that on the peer is worse than
                // leaving it out and letting the caller decline
                if (cost < 0) continue;
                map[id] = new CardCost(cost, SpellboostStep(f));
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"CardCostMap: load failed ({e.Message}), cost synthesis disabled");
            return new Dictionary<int, CardCost>();
        }
        Console.WriteLine($"CardCostMap: {map.Count} cards from {path}");
        return map;
    }

    // A charge lands on cards that have no discount at all, so 0 must mean "no such skill" and not "cannot price this"
    public const int UnreadableStep = -1;

    // skill/timing/target/option are parallel comma lists, "//" splitting the normal half from the evolve half
    static int SpellboostStep(List<string> f)
    {
        string[] sk = Normal(f[SkillCol]), tm = Normal(f[TimingCol]), tg = Normal(f[TargetCol]), op = Normal(f[OptionCol]);
        const string key = "add=ADD_CHARGE_COUNT*-";
        var unreadable = false;
        for (int i = 0; i < sk.Length; i++)
        {
            if (sk[i] != "cost_change" || i >= tm.Length || tm[i] != "when_spell_charge") continue;
            if (i >= tg.Length || !tg[i].Contains("target=self")) continue;
            if (i < op.Length && op[i].StartsWith(key) && int.TryParse(op[i][key.Length..], out var n)) return n;
            unreadable = true;
        }
        return unreadable ? UnreadableStep : 0;
    }

    static string[] Normal(string col) => col.Split("//")[0].Split(',');
}
