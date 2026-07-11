using System.Text;
using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class CardMasterBuilderTests
{
    static string BuildFromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return CardMasterBuilder.BuildCsv(doc.RootElement);
    }

    [Fact]
    public void RowHas78Columns()
    {
        var json = """
        {"100011010":{"id_":100011010,"name_":"test","craft_":"ロイヤル","rarity_":"ブロンズレア","type_":"フォロワー","pp_":2,"baseAtk_":2,"baseDef_":2,"evoAtk_":4,"evoDef_":4,"trait_":"-","expansion_":"ベーシック","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[]}}
        """;
        var csv = BuildFromJson(json);
        var row = csv.TrimEnd('\n');
        Assert.Equal(79, row.Split(',').Length);
    }

    [Fact]
    public void FollowerMapsAllCoreFields()
    {
        var json = """
        {"100011010":{"id_":100011010,"name_":"test","craft_":"ロイヤル","rarity_":"レジェンド","type_":"フォロワー","pp_":5,"baseAtk_":5,"baseDef_":5,"evoAtk_":7,"evoDef_":7,"trait_":"-","expansion_":"ベーシック","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[]}}
        """;
        var cols = BuildFromJson(json).TrimEnd('\n').Split(',');
        Assert.Equal("100011010", cols[0]);   // card_id
        Assert.Equal("10000", cols[2]);       // card_set_id (100 - 100 + 10000)
        Assert.Equal("CN_100011010", cols[3]);   // card_name id -> text bundle key CN_<id>
        Assert.Equal("1", cols[6]);           // char_type: follower
        Assert.Equal("2", cols[7]);           // clan: royal
        Assert.Equal("5", cols[10]);          // cost
        Assert.Equal("5", cols[11]);          // atk
        Assert.Equal("5", cols[12]);          // life
        Assert.Equal("7", cols[13]);          // evo_atk
        Assert.Equal("7", cols[14]);          // evo_life
        Assert.Equal("4", cols[16]);          // rarity: legend
    }

    [Fact]
    public void SpellMapsCharTypeFour()
    {
        var json = """
        {"111011050":{"id_":111011050,"name_":"s","craft_":"エルフ","rarity_":"シルバーレア","type_":"スペル","pp_":3,"baseAtk_":0,"baseDef_":0,"evoAtk_":0,"evoDef_":0,"trait_":"-","expansion_":"次元歪曲","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":true,"tokens_":[],"alts_":[]}}
        """;
        var cols = BuildFromJson(json).TrimEnd('\n').Split(',');
        Assert.Equal("4", cols[6]);           // char_type: spell
        Assert.Equal("1", cols[7]);           // clan: elf
        Assert.Equal("2", cols[16]);          // rarity: silver
        Assert.Equal("10011", cols[2]);       // card_set_id: 111 -> 10011
    }

    [Fact]
    public void AmuletMapsCharTypeTwo()
    {
        var json = """
        {"120011010":{"id_":120011010,"name_":"a","craft_":"ビショップ","rarity_":"ゴールドレア","type_":"アミュレット","pp_":3,"baseAtk_":0,"baseDef_":0,"evoAtk_":0,"evoDef_":0,"trait_":"-","expansion_":"暗黒のウェルサ","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":true,"tokens_":[],"alts_":[]}}
        """;
        var cols = BuildFromJson(json).TrimEnd('\n').Split(',');
        Assert.Equal("2", cols[6]);           // char_type: amulet
        Assert.Equal("7", cols[7]);           // clan: bishop
        Assert.Equal("3", cols[16]);          // rarity: gold
    }

    [Fact]
    public void TokenCardGetsSpecialSetId()
    {
        var json = """
        {"900334080":{"id_":900334080,"name_":"t","craft_":"ウィッチ","rarity_":"ゴールドレア","type_":"スペル","pp_":0,"baseAtk_":0,"baseDef_":0,"evoAtk_":0,"evoDef_":0,"trait_":"-","expansion_":"トークン","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[]}}
        """;
        var cols = BuildFromJson(json).TrimEnd('\n').Split(',');
        Assert.Equal("90000", cols[2]);
    }

    [Fact]
    public void RealShadowverseJsonProducesReasonablePayload()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "OpenVerse.Api", "data", "shadowverse_ja.json");
        if (!File.Exists(path)) return;
        using var fs = File.OpenRead(path);
        var csv = CardMasterBuilder.BuildCsv(fs);
        var rows = csv.TrimEnd('\n').Split('\n');
        Assert.InRange(rows.Length, 4000, 5000);
        foreach (var r in rows.Take(50))
            Assert.Equal(79, r.Split(',').Length);
        var encoded = CardMasterCodec.Encode(csv);
        Assert.InRange(Convert.FromBase64String(encoded).Length, 50_000, 2_000_000);
    }

    [Fact]
    public void MultipleCardsProduceMultipleRows()
    {
        var json = """
        {
          "100011010":{"id_":100011010,"name_":"a","craft_":"ロイヤル","rarity_":"ブロンズレア","type_":"フォロワー","pp_":2,"baseAtk_":2,"baseDef_":2,"evoAtk_":4,"evoDef_":4,"trait_":"-","expansion_":"ベーシック","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[]},
          "100011020":{"id_":100011020,"name_":"b","craft_":"ロイヤル","rarity_":"ブロンズレア","type_":"フォロワー","pp_":3,"baseAtk_":3,"baseDef_":3,"evoAtk_":5,"evoDef_":5,"trait_":"-","expansion_":"ベーシック","baseEffect_":"-","baseFlair_":"-","evoEffect_":"-","evoFlair_":"-","rotation_":false,"tokens_":[],"alts_":[]}
        }
        """;
        var csv = BuildFromJson(json);
        Assert.Equal(2, csv.TrimEnd('\n').Split('\n').Length);
    }
}
