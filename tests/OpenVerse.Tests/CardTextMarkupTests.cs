using System.Linq;
using OpenVerse.Decker.Data;

namespace OpenVerse.Tests;

public class CardTextMarkupTests
{
    [Fact]
    public void SegmentizeSplitsPlainAndHyperlinkSegmentsInOrder()
    {
        var raw = "自分の場に[u][ffcd45]呼応の使い魔[-][/u]を1体出す。";

        var segments = CardTextMarkup.Segmentize(raw);

        Assert.Equal(3, segments.Count);
        Assert.Equal("自分の場に", segments[0].Text);
        Assert.False(segments[0].IsHyperlink);
        Assert.Equal("呼応の使い魔", segments[1].Text);
        Assert.True(segments[1].IsHyperlink);
        Assert.Equal("を1体出す。", segments[2].Text);
        Assert.False(segments[2].IsHyperlink);
    }

    [Fact]
    public void SegmentizeHandlesTextWithNoHyperlinksAtAll()
    {
        var segments = CardTextMarkup.Segmentize("特に効果は無い。");

        Assert.Single(segments);
        Assert.Equal("特に効果は無い。", segments[0].Text);
        Assert.False(segments[0].IsHyperlink);
    }

    [Fact]
    public void SegmentizeConcatenatedTextEqualsStripNotation()
    {
        var raw = "[u][ffcd45]攻撃時[-][/u] 相手のリーダーに2ダメージ。[u][ffcd45]ツインプリズナー・グラス[-][/u]と合体する。";

        var segments = CardTextMarkup.Segmentize(raw);
        var reassembled = string.Concat(segments.Select(s => s.Text));

        Assert.Equal(CardTextMarkup.StripNotation(raw), reassembled);
    }

    [Fact]
    public void SegmentizeStripsNestedNotationInsidePlainSegments()
    {
        var raw = "[rub<るろう>]流浪[/rub]の傭兵";

        var segments = CardTextMarkup.Segmentize(raw);

        Assert.Single(segments);
        Assert.Equal("流浪の傭兵", segments[0].Text);
        Assert.False(segments[0].IsHyperlink);
    }

    [Fact]
    public void SegmentizeCapturesTheHyperlinkTagsColor()
    {
        var raw = "自分の場に[u][ffcd45]呼応の使い魔[-][/u]を1体出す。";

        var segments = CardTextMarkup.Segmentize(raw);

        Assert.Equal(3, segments.Count);
        Assert.Null(segments[0].ColorHex);
        Assert.Equal("呼応の使い魔", segments[1].Text);
        Assert.Equal("ffcd45", segments[1].ColorHex);
        Assert.False(segments[1].IsBold);
        Assert.Null(segments[2].ColorHex);
    }

    [Fact]
    public void SegmentizeConcatenatedTextWithColorsEqualsStripNotation()
    {
        var raw = "[u][524522]グレー[-][/u]の効果と[u][ffcd45]通常[-][/u]の効果。";

        var segments = CardTextMarkup.Segmentize(raw);
        var reassembled = string.Concat(segments.Select(s => s.Text));

        Assert.Equal(CardTextMarkup.StripNotation(raw), reassembled);
    }

    [Fact]
    public void SegmentizeMarksBareColorTagAsNonHyperlink()
    {
        // "[c8c8b0ff]" is the real "※このカードは「X」として扱う。" annotation's color - a bare
        // color wrap, never inside a "[u]...[/u]" hyperlink wrapper
        var raw = "進化前と同じ能力。\n\n[c8c8b0ff]※このカードは「クイックブレーダー」として扱う。[-]";

        var segments = CardTextMarkup.Segmentize(raw);

        var colored = segments.Single(s => s.ColorHex is not null);
        Assert.Equal("c8c8b0ff", colored.ColorHex);
        Assert.Equal("※このカードは「クイックブレーダー」として扱う。", colored.Text);
        Assert.False(colored.IsHyperlink);
        Assert.False(colored.IsBold);
    }

    [Fact]
    public void SegmentizeMarksBoldTagAsBoldHyperlink()
    {
        var raw = "[b]呼応の使い魔[/b]の効果。";

        var segments = CardTextMarkup.Segmentize(raw);

        Assert.Equal(2, segments.Count);
        Assert.Equal("呼応の使い魔", segments[0].Text);
        Assert.True(segments[0].IsHyperlink);
        Assert.True(segments[0].IsBold);
        Assert.False(segments[1].IsBold);
    }

    [Fact]
    public void SegmentizeDoesNotMarkPlainHyperlinkAsBold()
    {
        var raw = "[u][ffcd45]呼応の使い魔[-][/u]の効果。";

        var segments = CardTextMarkup.Segmentize(raw);

        Assert.True(segments[0].IsHyperlink);
        Assert.False(segments[0].IsBold);
    }

    // real example from master_skilldesctext.json (SD_810844020) - the game's own authoring tool
    // sometimes leaves a redundant extra "[-]" right before the real "[/u]" closer
    [Fact]
    public void SegmentizeAbsorbsARedundantDashBeforeTheHyperlinkCloser()
    {
        var raw = "さらに、[u][ffcd45]神器の使者[-][-][/u]1体を出す。";

        var segments = CardTextMarkup.Segmentize(raw);

        var link = segments.Single(s => s.IsHyperlink);
        Assert.Equal("神器の使者", link.Text);
        Assert.DoesNotContain("[-]", string.Concat(segments.Select(s => s.Text)));
    }

    // real example (SD_810441070_01, English) - up to 3 redundant dashes observed in practice
    [Fact]
    public void SegmentizeAbsorbsMultipleRedundantDashesBeforeTheHyperlinkCloser()
    {
        var raw = "[u][ffcd45]ディフェンスモード[-][-][-][/u]に進化する。";

        var segments = CardTextMarkup.Segmentize(raw);

        var link = segments.Single(s => s.IsHyperlink);
        Assert.Equal("ディフェンスモード", link.Text);
    }

    // real example (SD_810844060) - a bare tier digit ("[2]"/"[3]") can also sit right before the
    // real closer, and isn't part of the target card's name either
    [Fact]
    public void SegmentizeAbsorbsATierDigitBeforeTheHyperlinkCloser()
    {
        var raw = "さらに、[u][ffcd45]神器の使者[2][-][/u]1体を出す。";

        var segments = CardTextMarkup.Segmentize(raw);

        var link = segments.Single(s => s.IsHyperlink);
        Assert.Equal("神器の使者", link.Text);
    }

    // real example (SD_810441070_01, English) - the same decoy-token issue affects the "[b]...[/b]"
    // form too, even though a decoy-free bold tag has no "[-]" of its own
    [Fact]
    public void SegmentizeAbsorbsARedundantDashBeforeTheBoldCloser()
    {
        var raw = "Evolve into [b]Lævateinn Dragon, Defense Form[-][-][/b].";

        var segments = CardTextMarkup.Segmentize(raw);

        var link = segments.Single(s => s.IsBold);
        Assert.Equal("Lævateinn Dragon, Defense Form", link.Text);
    }

    [Fact]
    public void ExtractHyperlinkTargetsAbsorbsDecoyTokensTooSoNameLookupsResolve()
    {
        var raw = "・[u][ffcd45]神器の使者[-][-][/u]1体を出す。\n・[u][ffcd45]神器の番人[2][-][/u]1体を出す。";

        var targets = CardTextMarkup.ExtractHyperlinkTargets(raw);

        Assert.Equal(["神器の使者", "神器の番人"], targets);
    }

    [Fact]
    public void StripDynamicValueTemplatesRemovesASimpleBlock()
    {
        var raw = "自分のフォロワー1体を+2/+2する。<<{me.destroyed_card_list.tribe=artifact.unique_base_card_id_card.count}>>/6種類";

        var result = CardTextMarkup.StripDynamicValueTemplates(raw);

        Assert.Equal("自分のフォロワー1体を+2/+2する。/6種類", result);
        Assert.DoesNotContain("<<", result);
        Assert.DoesNotContain(">>", result);
    }

    // real example from master_skilldesctext.json (SD_100214020) - the "?"-ternary form lets its
    // branches contain further "<<...>>" blocks
    [Fact]
    public void StripDynamicValueTemplatesRemovesNestedBlocksEntirely()
    {
        var raw = "<<{me.hand_self.count}+1??（連携は<<{me.inplay.class.rally_count}>>/10体）\n>>自分のフォロワー1体を+2/+2する。";

        var result = CardTextMarkup.StripDynamicValueTemplates(raw);

        Assert.Equal("自分のフォロワー1体を+2/+2する。", result);
        Assert.DoesNotContain("<<", result);
        Assert.DoesNotContain(">>", result);
    }

    [Fact]
    public void StripDynamicValueTemplatesLeavesTextWithoutTemplatesUntouched()
    {
        var raw = "特に効果は無い。";

        Assert.Equal(raw, CardTextMarkup.StripDynamicValueTemplates(raw));
    }
}
