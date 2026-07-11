using System.Text;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class OwnershipTests
{
    // fixture: a buildable card, its alt illustration (7xx), a same-name token (900) in its alts_, and a standalone token
    const string Cards = """
    {
      "100411010":{"id_":100411010,"name_":"a","craft_":"ドラゴン","rarity_":"レジェンド","type_":"フォロワー","pp_":5,"baseAtk_":5,"baseDef_":5,"evoAtk_":7,"evoDef_":7,"trait_":"-","expansion_":"x","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[705411010,900411010]},
      "705411010":{"id_":705411010,"name_":"a","craft_":"ドラゴン","rarity_":"レジェンド","type_":"フォロワー","pp_":5,"baseAtk_":5,"baseDef_":5,"evoAtk_":7,"evoDef_":7,"trait_":"-","expansion_":"x","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[100411010,715411010]},
      "900411010":{"id_":900411010,"name_":"a","craft_":"ドラゴン","rarity_":"レジェンド","type_":"フォロワー","pp_":3,"baseAtk_":3,"baseDef_":3,"evoAtk_":3,"evoDef_":3,"trait_":"-","expansion_":"トークン","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[100411010]},
      "900111010":{"id_":900111010,"name_":"t","craft_":"ドラゴン","rarity_":"ブロンズレア","type_":"スペル","pp_":0,"baseAtk_":0,"baseDef_":0,"evoAtk_":0,"evoDef_":0,"trait_":"-","expansion_":"x","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[]}
    }
    """;

    static HashSet<long> CardIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("card_id").GetInt64())];
    }

    [Fact]
    public void GrantsBuildableAndAltNotToken()
    {
        var ids = CardIds(UserCardListBuilder.BuildJson(new MemoryStream(Encoding.UTF8.GetBytes(Cards))));
        Assert.Contains(100411010L, ids);   // buildable
        Assert.Contains(705411010L, ids);   // alt illustration granted as its own id
        Assert.DoesNotContain(900411010L, ids); // same-name token in alts_ is dropped (not collectible)
        Assert.DoesNotContain(900111010L, ids); // token, not referenced as an alt
    }

    [Fact]
    public void AltRowGroupsUnderBuildableBaseKeepsOwnArt()
    {
        using var doc = JsonDocument.Parse(Cards);
        var alt = CardMasterBuilder.BuildCsv(doc.RootElement).TrimEnd('\n').Split('\n')
            .Select(r => r.Split(',')).Single(r => r[0] == "705411010");
        Assert.Equal("100411010", alt[63]); // base_card_id -> buildable base (shares 3-copy limit)
        Assert.Equal("705411010", alt[64]); // normal_card_id stays self: spell art bundle loads from it
        Assert.Equal("705411010", alt[65]); // resource_card_id stays self so alt art renders
    }

    [Fact]
    public void PremiumGrantsFoilIdOfBuildableOnly()
    {
        var plain = CardIds(UserCardListBuilder.BuildJson(new MemoryStream(Encoding.UTF8.GetBytes(Cards))));
        Assert.DoesNotContain(1004110100L, plain);

        var prem = CardIds(UserCardListBuilder.BuildJson(new MemoryStream(Encoding.UTF8.GetBytes(Cards)), premium: true));
        Assert.Contains(1004110100L, prem);              // FoilId(100411010)
        Assert.Equal(1004110100L, CardMasterBuilder.FoilId(100411010));
        Assert.DoesNotContain(CardMasterBuilder.FoilId(705411010), prem); // no foil for alt cards
    }

    [Fact]
    public void EveryUserCardEntryHasAllThreeKeys()
    {
        using var doc = JsonDocument.Parse(UserCardListBuilder.BuildJson(
            new MemoryStream(Encoding.UTF8.GetBytes(Cards)), premium: true));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            Assert.True(e.TryGetProperty("card_id", out _));
            Assert.True(e.TryGetProperty("number", out var n) && n.GetInt32() == 3);
            Assert.True(e.TryGetProperty("is_protected", out var p) && p.GetInt32() == 0);
        }
    }

    [Fact]
    public void PremiumEmitsFoilRowWithMatchingFoilId()
    {
        using var doc = JsonDocument.Parse(Cards);
        var rows = CardMasterBuilder.BuildCsv(doc.RootElement, premium: true)
            .TrimEnd('\n').Split('\n').Select(r => r.Split(',')).ToList();

        var normal = rows.Single(r => r[0] == "100411010");
        var foil = rows.Single(r => r[0] == "1004110100");
        Assert.Equal("1004110100", normal[1]); // normal.foil_card_id -> foil id
        Assert.Equal("0", normal[4]);          // normal not foil
        Assert.Equal("1", foil[4]);            // foil.is_foil
        Assert.Equal("100411010", foil[63]);   // foil.base_card_id -> normal
        Assert.Equal("100411010", foil[64]);   // foil.normal_card_id -> normal
        Assert.Equal("0", foil[1]);            // foil has no further foil
        Assert.Equal(CardMasterCodec.Columns.Length, foil.Length);
    }

    // mirrors the client's ConvertCSV_Array: quoted fields keep internal commas, "" is an escaped quote
    static string[] SplitCsvRow(string row)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var q = false;
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (q)
            {
                if (c == '"') { if (i + 1 < row.Length && row[i + 1] == '"') { sb.Append('"'); i++; } else q = false; }
                else sb.Append(c);
            }
            else if (c == '"') q = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return [.. fields];
    }

    [Fact]
    public void VoiceCuesSplitAcrossEventColumns()
    {
        using var doc = JsonDocument.Parse(Cards);
        var vc = new Dictionary<int, string> { [100411010] = "100411010_1,100411010_2,100411010_3" };
        var rows = CardMasterBuilder.BuildCsv(doc.RootElement, null, null, null, true, vc)
            .TrimEnd('\n').Split('\n').Select(SplitCsvRow).ToList();
        // the RFC4180 parser the client uses keeps every row at the full column count
        Assert.All(rows, r => Assert.Equal(CardMasterCodec.Columns.Length, r.Length));
        var card = rows.Single(r => r[0] == "100411010");
        Assert.Equal("100411010_1", card[55]);
        Assert.Equal("100411010_3", card[56]);   // evolve = cue _3
        Assert.Equal("100411010_2", card[57]);   // attack = cue _2
        Assert.Equal("100411010_1", rows.Single(r => r[0] == "1004110100")[55]); // foil inherits
        Assert.Equal("0", rows.Single(r => r[0] == "900411010")[55]);            // no cue -> silent
    }

    [Fact]
    public void NoPremiumMeansNoFoilRowsAndZeroFoilColumn()
    {
        using var doc = JsonDocument.Parse(Cards);
        var rows = CardMasterBuilder.BuildCsv(doc.RootElement).TrimEnd('\n').Split('\n')
            .Select(r => r.Split(',')).ToList();
        Assert.DoesNotContain(rows, r => r[0] == "1004110100");
        Assert.Equal("0", rows.Single(r => r[0] == "100411010")[1]);
    }
}
