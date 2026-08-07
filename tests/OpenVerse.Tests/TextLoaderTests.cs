using OpenVerse.Decker.Data;

namespace OpenVerse.Tests;

public class TextLoaderTests
{
    private static readonly string CardNameJsonPath =
        Path.Combine(AppContext.BaseDirectory, "assets", "text_loader_cardnametext.json");

    private static readonly string SkillDescJsonPath =
        Path.Combine(AppContext.BaseDirectory, "assets", "text_loader_skilldesctext.json");

    private static readonly string TextlangsJsonPath =
        Path.Combine(AppContext.BaseDirectory, "assets", "text_loader_textlangs.json");

    [Fact]
    public void LoadAvailableLangsReturnsTheLangArray()
    {
        var langs = TextLoader.LoadAvailableLangs(TextlangsJsonPath);

        Assert.Equal(["Jpn", "Eng"], langs);
    }

    [Fact]
    public void LoadAvailableLangsThrowsWhenFileMissing()
    {
        Assert.Throws<FileNotFoundException>(() => TextLoader.LoadAvailableLangs("does-not-exist.json"));
    }

    [Fact]
    public void ConstructorThrowsWhenCardNameFileMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new TextLoader("does-not-exist.json", SkillDescJsonPath, "Jpn"));
    }

    [Fact]
    public void ConstructorThrowsWhenSkillDescFileMissing()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new TextLoader(CardNameJsonPath, "does-not-exist.json", "Jpn"));
    }

    [Fact]
    public void ConstructorThrowsWhenLangMissingFromEitherFile()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Klingon"));
    }

    [Fact]
    public void LangIsTheRequestedLanguageKey()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        Assert.Equal("Jpn", loader.Lang);
    }

    [Fact]
    public void Id2NameKeepsMarkupForGuiRendering()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        Assert.Equal("[rub<てすと>]試験[/rub]の戦士", loader.Id2Name[900000001]);
        Assert.Equal("呼応の使い魔", loader.Id2Name[900000002]);
        Assert.Equal("無関係カード", loader.Id2Name[900000003]);
    }

    [Fact]
    public void Id2NameSwitchesWithLang()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Eng");

        Assert.Equal("Test Warrior", loader.Id2Name[900000001]);
    }

    [Fact]
    public void RawName2IdIsTheMarkupStrippedReverseOfId2Name()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        Assert.Equal(900000001, loader.RawName2Id["試験の戦士"]);
        Assert.Equal(900000002, loader.RawName2Id["呼応の使い魔"]);
        Assert.Equal(900000003, loader.RawName2Id["無関係カード"]);
    }

    [Fact]
    public void Id2DescLabelsBaseAndEvolvedHalvesForEvolvingCards()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        var desc = loader.Id2Desc[900000001];
        Assert.Equal(
            "進化前\n\n[u][ffcd45]呼応の使い魔[-][/u]を1体出す。[u][ffcd45]秘伝の力[-][/u]を得る。\n---\n進化後\n\n進化後の試験効果。",
            desc);
    }

    [Fact]
    public void Id2DescIsUnlabelledForNonEvolvingCards()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        Assert.Equal("特に効果は無い。", loader.Id2Desc[900000003]);
    }

    [Fact]
    public void Id2RawFullDescIsMarkupStrippedWithNoEvolutionLabels()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        var raw = loader.Id2RawFullDesc[900000003];
        Assert.Equal("無関係カード 特に効果は無い。", raw);
        Assert.DoesNotContain("進化前", loader.Id2RawFullDesc[900000001]);
        Assert.DoesNotContain("[u]", loader.Id2RawFullDesc[900000001]);
    }

    [Fact]
    public void Id2RawFullDescRecursivelyFollowsCardReferences()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        // 900000001 references 900000002 ("呼応の使い魔"), which is expected to be pulled in
        var raw = loader.Id2RawFullDesc[900000001];
        Assert.Contains("試験の戦士", raw); // own (stripped) name
        Assert.Contains("呼応の使い魔", raw); // referenced card's (stripped) name
        Assert.Contains("共鳴する", raw); // referenced card's own description text
    }

    [Fact]
    public void Id2RawFullDescSkipsUnresolvableReferencesWithoutThrowing()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        // checks if there is a hyperlinked unregistered word and skips it without throwing.
        var raw = loader.Id2RawFullDesc[900000001];
        Assert.Equal(1, raw.Split("秘伝の力").Length - 1);
    }

    [Fact]
    public void Id2RawFullDescCycleProtectionStopsAtTheOriginCard()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        // checks if expanding descriptions to linked cards does not loop forever when there is a cycle link.
        var raw = loader.Id2RawFullDesc[900000001];
        Assert.Equal(1, raw.Split("共鳴する").Length - 1);
    }

    [Fact]
    public void Id2AdditionalDescPastesReferencedNameAndDescAsIsWithMarkupPreserved()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        var additional = loader.Id2AdditionalDesc[900000001];
        Assert.Contains("[b]呼応の使い魔[/b]\n\n", additional); // referenced card's name, bold-wrapped GUI-format block
        Assert.Contains(loader.Id2Desc[900000002], additional); // its full unified (labelled) desc pasted as-is
    }

    [Fact]
    public void Id2AdditionalDescIsEmptyWhenNothingIsResolvable()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        Assert.Equal(string.Empty, loader.Id2AdditionalDesc[900000003]);
    }

    [Fact]
    public void Id2AdditionalDescCycleProtectionStopsAtTheOriginCard()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        // checks if additional description(in shadowverse, its detail description for a yellow hyplerlinked text) doesn't have the card info itself.
        var additional = loader.Id2AdditionalDesc[900000001];
        Assert.DoesNotContain("[b]試験の戦士[/b]\n\n", additional);
    }

    // 900000004's evolved-state slot is exactly the "same ability as before evolving" filler
    [Fact]
    public void Id2DescKeepsTheSameAsBeforeEvolutionFillerForDisplay()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        Assert.Contains("進化前と同じ能力。", loader.Id2Desc[900000004]);
    }

    [Fact]
    public void Id2RawFullDescExcludesTheSameAsBeforeEvolutionFiller()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        // the filler is useless as a search term - it should be dropped, while the real
        // unevolved-state text is still there
        var raw = loader.Id2RawFullDesc[900000004];
        Assert.DoesNotContain("進化前と同じ能力。", raw);
        Assert.Contains("自分の場にカードを1枚出す。", raw);
    }

    // 900000005's evolved-state slot is the "excluding Fanfare" variant of the filler
    // ("進化前と同じ能力。（[u][ffcd45]ファンファーレ[-][/u] 能力を除く）") - a different exact
    // string from the plain filler, extracted from real card text the same way
    [Fact]
    public void Id2RawFullDescExcludesTheFanfareExcludedEvolutionFillerToo()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        var raw = loader.Id2RawFullDesc[900000005];
        Assert.DoesNotContain("進化前と同じ能力", raw);
        Assert.Contains("カードを1枚引く", raw);
    }

    // the filler text is language-specific - this asserts filtering actually uses the real
    // English strings (extracted from the same reference cards), not just the Japanese ones
    [Fact]
    public void Id2RawFullDescExcludesTheEvolutionFillerInEnglishToo()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Eng");

        var plainRaw = loader.Id2RawFullDesc[900000004];
        Assert.DoesNotContain("Same as the unevolved form", plainRaw);
        Assert.Contains("Put a card into play", plainRaw);

        var fanfareRaw = loader.Id2RawFullDesc[900000005];
        Assert.DoesNotContain("Same as the unevolved form", fanfareRaw);
        Assert.Contains("Draw a card", fanfareRaw);
    }

    // 900000006 ("カティア"/Katia) mirrors the real "nickname reskin" cards whose evolved-state
    // text is the filler PLUS a trailing "※このカードは「X」として扱う。" annotation of its own -
    // the filler is a prefix to strip, not the whole string to match exactly, so that trailing
    // real text (useful for search) has to survive
    [Fact]
    public void Id2RawFullDescStripsOnlyTheFillerPrefixAndKeepsTrailingRealText()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        var raw = loader.Id2RawFullDesc[900000006];
        Assert.DoesNotContain("進化前と同じ能力", raw);
        Assert.Contains("「テストの再臨」として扱う", raw); // the "[c8c8b0ff]...[-]" annotation, markup-stripped
        Assert.Contains("トークンを1体出す", raw); // the unevolved-state text is untouched
    }

    // Id2Desc is for display - the filler (and the annotation after it) are legitimate content to
    // show the user there, unlike in the search-only Id2RawFullDesc above
    [Fact]
    public void Id2DescKeepsBothTheFillerAndTheTrailingAnnotationForDisplay()
    {
        var loader = new TextLoader(CardNameJsonPath, SkillDescJsonPath, "Jpn");

        var desc = loader.Id2Desc[900000006];
        Assert.Contains("進化前と同じ能力。", desc);
        Assert.Contains("[c8c8b0ff]※このカードは「テストの再臨」として扱う。[-]", desc);
    }
}
