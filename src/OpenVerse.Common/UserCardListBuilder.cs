using System.Text;
using System.Text.Json;

namespace OpenVerse.Common;

// grant every collectible card so the deck editor's ownership filter (GetPossessionCardNum > 0) passes
// alt grants exclude 900-prefix tokens: some alts_ point at summon tokens that share a name but are not collectible
public static class UserCardListBuilder
{
    static bool IsAltGrantable(long id)
    {
        var p = id / 1_000_000;
        return p is >= 100 and <= 129 or >= 701 and <= 720;
    }

    // col0=card_id, col4=is_foil, quote-free so a plain split works. collectible = sets 100-132 + alt 701-722
    public static string BuildFromCsv(string csv, int number = 3)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var line in csv.Split('\n'))
        {
            if (line.Length < 10) continue;
            var c = line.Split(',', 6);
            if (c.Length < 5 || !long.TryParse(c[0], out var id) || c[4] != "0") continue;
            var p = id / 1_000_000;
            if (!(p is >= 100 and <= 132 or >= 701 and <= 722)) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"card_id\":").Append(id).Append(",\"number\":").Append(number).Append(",\"is_protected\":0}");
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static string BuildJson(Stream cardJson, int number = 3, bool premium = false)
    {
        using var doc = JsonDocument.Parse(cardJson);
        var ids = new HashSet<long>();
        var alts = new HashSet<long>();
        foreach (var e in doc.RootElement.EnumerateObject())
        {
            var card = e.Value;
            var id = card.GetProperty("id_").GetInt32();
            var prefix = id / 1_000_000;
            if (prefix is >= 100 and <= 129)
            {
                ids.Add(id);
                if (premium) ids.Add(CardMasterBuilder.FoilId(id));
            }
            if (card.TryGetProperty("alts_", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray())
                {
                    var altId = x.GetInt64();
                    if (IsAltGrantable(altId)) alts.Add(altId);
                }
        }
        ids.UnionWith(alts);

        var sb = new StringBuilder("[");
        var first = true;
        foreach (var id in ids)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"card_id\":").Append(id)
              .Append(",\"number\":").Append(number)
              .Append(",\"is_protected\":0}");
        }
        sb.Append(']');
        return sb.ToString();
    }
}
