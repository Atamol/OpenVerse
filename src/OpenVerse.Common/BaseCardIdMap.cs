using System.IO.Compression;
using System.Text;

namespace OpenVerse.Common;

// cardId -> base_card_id. alt-art ids (prefix 701-722) are granted unconditionally and map back to their base, so two
// art variants of one card are the SAME card for a highlander check even though their cardIds differ. no arithmetic
// rule works across builds (the real master's foils are id+1, the reconstruction's are id*10), so it must be a table.
public static class BaseCardIdMap
{
    const int CardIdCol = 0;
    const int BaseCardIdCol = 63;

    // only the real master. the reconstruction fallback invents its own foil convention, and a map that disagreed with
    // the deck ids the API resolved would inject a wrong highlander bit - worse than not injecting. absent -> empty
    public static Dictionary<int, int> Load(string dataDir)
    {
        var map = new Dictionary<int, int>();
        var path = Path.Combine(dataDir, "card_master_full.csv.gz");
        if (!File.Exists(path))
        {
            Console.WriteLine($"BaseCardIdMap: {path} missing, highlander synthesis disabled");
            return map;
        }
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var sr = new StreamReader(gz);
            while (sr.ReadLine() is { } line)
            {
                var f = SplitCsv(line);
                if (f.Count <= BaseCardIdCol) continue;
                if (int.TryParse(f[CardIdCol], out var id) && int.TryParse(f[BaseCardIdCol], out var b)) map[id] = b;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"BaseCardIdMap: load failed ({e.Message}), highlander synthesis disabled");
            return new Dictionary<int, int>();
        }
        Console.WriteLine($"BaseCardIdMap: {map.Count} cards from {path}");
        return map;
    }

    // the voice-cue columns are quoted and contain commas, and base_card_id sits after them, so a plain Split(',')
    // lands on the wrong field (row 930844060 yields "SHURIKEN" instead of the id)
    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"') cur.Append(c);
                else if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        fields.Add(cur.ToString());
        return fields;
    }
}
