using System.Text;
using System.Text.Json;

namespace OpenVerse.Common;

public static class CardMasterBuilder
{
    static readonly Dictionary<string, int> ClanMap = new()
    {
        ["ニュートラル"] = 0, ["エルフ"] = 1, ["ロイヤル"] = 2, ["ウィッチ"] = 3,
        ["ドラゴン"] = 4, ["ネクロマンサー"] = 5, ["ヴァンパイア"] = 6, ["ビショップ"] = 7, ["ネメシス"] = 8,
    };

    static readonly Dictionary<string, int> RarityMap = new()
    {
        ["ブロンズレア"] = 1, ["シルバーレア"] = 2, ["ゴールドレア"] = 3, ["レジェンド"] = 4,
    };

    static readonly Dictionary<string, int> CharTypeMap = new()
    {
        ["フォロワー"] = 1, ["アミュレット"] = 2, ["スペル"] = 4,
    };

    // *10 so the client's resource_card_id / 10 art recovery still resolves the normal art
    public static long FoilId(int normalId) => normalId * 10L;

    static bool IsBuildable(int id) => id / 1_000_000 is >= 100 and <= 129;

    public static string BuildCsv(Stream shadowverseJson, IReadOnlyDictionary<int, string[]>? textIds = null,
        HashSet<int>? cvIds = null, HashSet<string>? tnKeys = null, bool premium = false,
        IReadOnlyDictionary<int, string>? voiceCues = null)
    {
        using var doc = JsonDocument.Parse(shadowverseJson);
        return BuildCsv(doc.RootElement, textIds, cvIds, tnKeys, premium, voiceCues);
    }

    public static string BuildCsv(JsonElement root, IReadOnlyDictionary<int, string[]>? textIds = null,
        HashSet<int>? cvIds = null, HashSet<string>? tnKeys = null, bool premium = false,
        IReadOnlyDictionary<int, string>? voiceCues = null)
    {
        textIds ??= new Dictionary<int, string[]>();
        cvIds ??= [];
        tnKeys ??= [];
        voiceCues ??= new Dictionary<int, string>();
        var rows = new List<string[]>();
        foreach (var entry in root.EnumerateObject())
        {
            var vals = BuildVals(entry.Value, textIds, cvIds, tnKeys, voiceCues);
            var id = int.Parse(vals[0]);
            if (premium && IsBuildable(id)) vals[1] = FoilId(id).ToString();
            rows.Add(vals);
            if (premium && IsBuildable(id)) rows.Add(FoilVals(vals, id));
        }
        // row order IS the collection order (client sorts by CSV position), so cost-first mixes classes per band
        rows.Sort((a, b) =>
        {
            int c = int.Parse(a[10]).CompareTo(int.Parse(b[10])); if (c != 0) return c;   // cost
            c = int.Parse(a[64]).CompareTo(int.Parse(b[64])); if (c != 0) return c;        // normal_card_id
            return int.Parse(a[4]).CompareTo(int.Parse(b[4]));                             // is_foil
        });
        var sb = new StringBuilder();
        foreach (var vals in rows) sb.Append(string.Join(',', vals)).Append('\n');
        return sb.ToString();
    }

    static string[] FoilVals(string[] normal, int id)
    {
        var f = (string[])normal.Clone();
        f[0] = FoilId(id).ToString();  // card_id
        f[1] = "0";                    // foil_card_id (a foil has no further foil)
        f[4] = "1";                    // is_foil
        f[63] = id.ToString();         // base_card_id -> normal
        f[64] = id.ToString();         // normal_card_id -> normal
        f[65] = id.ToString();         // resource_card_id -> normal
        f[78] = FoilId(id).ToString(); // CardHashId
        return f;
    }

    // skip multi-trait (・) traits: their comma-joined TN ids would break the CSV row
    static string TribeNameId(string trait, HashSet<string> tnKeys)
    {
        if (string.IsNullOrEmpty(trait) || trait == "-" || trait.Contains('・')) return "";
        var key = "TN_" + trait;
        return tnKeys.Contains(key) ? key : "";
    }

    // cue number: 1=play, 2=atk, 3=evo, 4=death, 5=skill (others -> play)
    static string[] ClassifyVoiceCues(string cues)
    {
        var play = new List<string>();
        var by = new Dictionary<int, List<string>>();
        foreach (var t in cues.Split(','))
        {
            var parts = t.Split('_');
            if (parts.Length == 2 && int.TryParse(parts[1], out var n) && n >= 2 && n <= 5)
                (by.TryGetValue(n, out var l) ? l : by[n] = []).Add(t);
            else
                play.Add(t);
        }
        string Col(int n) => by.TryGetValue(n, out var l) ? string.Join(",", l) : "";
        return [string.Join(",", play), Col(3), Col(2), Col(4), Col(5)];
    }

    static string[] BuildVals(JsonElement card, IReadOnlyDictionary<int, string[]> textIds, HashSet<int> cvIds,
        HashSet<string> tnKeys, IReadOnlyDictionary<int, string> voiceCues)
    {
        var id = card.GetProperty("id_").GetInt32();
        var clan = ClanMap.GetValueOrDefault(card.GetProperty("craft_").GetString() ?? "", 0);
        var rarity = RarityMap.GetValueOrDefault(card.GetProperty("rarity_").GetString() ?? "", 1);
        var charType = CharTypeMap.GetValueOrDefault(card.GetProperty("type_").GetString() ?? "", 1);
        var cost = card.GetProperty("pp_").GetInt32();
        var atk = card.GetProperty("baseAtk_").GetInt32();
        var life = card.GetProperty("baseDef_").GetInt32();
        var evoAtk = card.GetProperty("evoAtk_").GetInt32();
        var evoLife = card.GetProperty("evoDef_").GetInt32();
        var setId = DeriveSetId(id);

        var vals = new string[CardMasterCodec.Columns.Length];
        Array.Fill(vals, "0");
        vals[0] = id.ToString();
        vals[2] = setId.ToString();
        vals[3] = $"CN_{id}";
        vals[6] = charType.ToString();
        vals[7] = clan.ToString();
        vals[10] = cost.ToString();
        vals[11] = atk.ToString();
        vals[12] = life.ToString();
        vals[13] = evoAtk.ToString();
        vals[14] = evoLife.ToString();
        vals[16] = rarity.ToString();
        vals[9] = TribeNameId(card.GetProperty("trait_").GetString() ?? "", tnKeys);
        if (card.TryGetProperty("tokens_", out var toks) && toks.ValueKind == JsonValueKind.Array && toks.GetArrayLength() > 0)
            vals[22] = string.Join(" ", toks.EnumerateArray().Select(e => e.GetInt32().ToString()));
        var t = textIds.GetValueOrDefault(id);
        vals[25] = t is not null ? t[0] : "";
        vals[26] = t is not null ? t[1] : "";
        vals[60] = t is not null ? t[2] : "";
        vals[61] = t is not null ? t[3] : "";
        // only mark CV when the card has playable cues, else a voice mark with nothing to play
        vals[74] = cvIds.Contains(id) && voiceCues.ContainsKey(id) ? $"CV_{id}" : "";
        // quote each so the cue comma-list survives the CSV parse
        if (voiceCues.TryGetValue(id, out var vc) && vc.Length > 0)
        {
            var v = ClassifyVoiceCues(vc);
            for (var i = 0; i < 5; i++) vals[55 + i] = $"\"{v[i]}\"";
        }
        vals[63] = id.ToString();
        vals[64] = id.ToString();
        vals[65] = id.ToString();
        vals[78] = id.ToString();
        // alt rows: remap only base_card_id (shares the base's 3-copy limit), keep normal/resource self
        // else the art bundle path (from normal_card_id) loads the base and the alt renders black
        if (id / 1_000_000 is >= 701 and <= 720
            && card.TryGetProperty("alts_", out var altsArr) && altsArr.ValueKind == JsonValueKind.Array)
            foreach (var a in altsArr.EnumerateArray())
            {
                var b = a.GetInt32();
                if (b / 1_000_000 is >= 100 and <= 129) { vals[63] = b.ToString(); break; }
            }
        return vals;
    }

    static int DeriveSetId(int cardId)
    {
        var prefix = cardId / 1_000_000;
        if (prefix >= 100 && prefix <= 199) return 10000 + (prefix - 100);
        if (prefix == 900) return 90000;
        if (prefix == 700) return 70000;
        return 0;
    }
}
