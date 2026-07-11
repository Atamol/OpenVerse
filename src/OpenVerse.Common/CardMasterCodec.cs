using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace OpenVerse.Common;

public static class CardMasterCodec
{
    public static readonly string[] Columns =
    [
        "card_id", "foil_card_id", "card_set_id", "CardNameId", "is_foil", "path",
        "char_type", "clan", "tribe", "TribeNameId", "cost", "atk", "life",
        "evo_atk", "evo_life", "chant_count", "rarity",
        "skill", "skill_timing", "skill_condition", "skill_target", "skill_option",
        "skill_preprocess", "skill_effect_condition", "skill_icon",
        "SkillDescriptionId", "EvoSkillDescriptionId",
        "summon_effect_path", "summon_se_path", "summon_move_type", "summon_effect_type", "summon_time",
        "atk_effect_path", "atk_se", "atk_move_type", "atk_effect_engin_type", "atk_time",
        "skill_effect_path", "skill_se", "skill_move_type", "skill_effect_engin_type", "skill_effect_time", "skill_effect_target_type",
        "evo_skill_effect_path", "evo_skill_se", "evo_skill_move_type", "evo_skill_effect_engin_type", "evo_skill_effect_time",
        "get_red_ether", "use_red_ether",
        "evol_effect_path", "evol_se_path", "evo_effect_type", "evol_time", "destroy_effect_path",
        "play_voice", "evo_voice", "atk_voice", "destroy_voice", "skill_voice",
        "DescriptionId", "EvoDescriptionId", "card_frame_type",
        "base_card_id", "normal_card_id", "resource_card_id",
        "tilling_normal_x", "tilling_normal_y", "offset_normal_x", "offset_normal_y",
        "tilling_evol_x", "tilling_evol_y", "offset_evol_x", "offset_evol_y",
        "CardVoiceId", "IsOverrideSkillDescription", "IsResurgentCard", "TwoPickFoilCardId",
        "CardHashId",
    ];

    public static string Encode(string defaultCsv, string? nextCsv = null)
    {
        var dict = new Dictionary<string, string>
        {
            ["1"] = defaultCsv,
            ["2"] = nextCsv ?? defaultCsv,
        };
        var json = JsonSerializer.Serialize(dict);
        var utf8 = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            gz.Write(utf8, 0, utf8.Length);
        return Convert.ToBase64String(ms.ToArray());
    }
}
