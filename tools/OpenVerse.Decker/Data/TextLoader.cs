using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenVerse.Decker.Data;

/// <summary>
/// Prepare card name texts and their description texts and effect texts to use in decker tool.
/// </summary>
public class TextLoader
{
    private const string CardNamePrefix = "CN_";
    private const string SkillDescPrefix = "SD_";

    // the evolved state text is often just "same ability as before evolving" and we remove them from searchable raw description.
    // Ita/Ger/Spa don't have the texts.
    private static readonly IReadOnlyDictionary<string, string[]> EvolutionFillers = new Dictionary<string, string[]>
    {
        ["Jpn"] = ["進化前と同じ能力。", "進化前と同じ能力。（[u][ffcd45]ファンファーレ[-][/u] 能力を除く）"],
        ["Eng"] = ["(Same as the unevolved form.)", "(Same as the unevolved form, excluding [b]Fanfare[/b].)"],
        ["Chs"] = ["能力与进化前相同。", "能力与进化前相同。（[u][ffcd45]入场曲[-][/u]能力除外）"],
        ["Kor"] = ["진화 전과 동일.", "진화 전과 동일. ([u][ffcd45]출격[-][/u] 능력 제외)"],
        ["Cht"] = ["與進化前能力相同。", "與進化前能力相同。（[u][ffcd45]入場曲[-][/u] 能力除外）"],
        ["Fre"] = ["(Agit de la même façon qu'avant l'évolution.)", "Agit de la même façon qu'avant l'évolution (hormis la [b]Fanfare[/b])."],
    };

    /// <summary>
    /// language key like "Jpn" or "Eng".
    /// </summary>
    public string Lang { get; }

    /// <summary>
    /// card id to displayable name for a card.
    /// </summary>
    public IReadOnlyDictionary<int, string> Id2Name { get; }

    /// <summary>
    /// makrup-stripped card name to card id.<br/>
    /// used when resolving hyperlinks in descriptions to the target card's id.<br/>
    /// </summary>
    public IReadOnlyDictionary<string, int> RawName2Id { get; }

    /// <summary>
    /// card id to displayable description for a card with its markup preverved and unevolved/evolved states.
    /// </summary>
    public IReadOnlyDictionary<int, string> Id2Desc { get; }

    /// <summary>
    /// card id to searchable full description for a card include its hyperlinked cards and effects.<br/>
    /// used to build the search blob for decker tool's search utility.
    /// </summary>
    public IReadOnlyDictionary<int, string> Id2RawFullDesc { get; }

    /// <summary>
    /// card id to displayable description for a card used when some hyperlink in the card's own description is cliked to show additional cards or effects' info.
    /// </summary>
    public IReadOnlyDictionary<int, string> Id2AdditionalDesc { get; }

    // reads textlangs.json (written by OpenVerse.Setup/Program.cs) and returns the language keys
    // stored in it (e.g. ["Jpn", "Eng"]).
    public static string[] LoadAvailableLangs(string textlangsJsonPath)
    {
        if (!File.Exists(textlangsJsonPath))
        {
            throw new FileNotFoundException("textlangs.json not found", textlangsJsonPath);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(textlangsJsonPath));
        return doc.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
    }

    // optional: card_master_full.csv.gz path, used only to append "{Type} {Cost} {Power}/{Life}"
    // next to a referenced card's name in Id2AdditionalDesc (e.g. "神器の使者 Fol 5 5/5") - see
    // StatsTextOf below. Null (the default) omits stats annotation entirely, e.g. for tests that
    // don't care about it and don't want to also need a card_master fixture.
    public TextLoader(string cardNameTextJsonPath, string skillDescTextJsonPath, string lang, string? cardMasterCsvGzPath = null)
    {
        if (!File.Exists(cardNameTextJsonPath))
        {
            throw new FileNotFoundException("master_cardnametext.json not found", cardNameTextJsonPath);
        }
        if (!File.Exists(skillDescTextJsonPath))
        {
            throw new FileNotFoundException("master_skilldesctext.json not found", skillDescTextJsonPath);
        }

        using var cardNameDoc = JsonDocument.Parse(File.ReadAllText(cardNameTextJsonPath));
        using var skillDescDoc = JsonDocument.Parse(File.ReadAllText(skillDescTextJsonPath));

        // both files are shaped {"<bundlename>": {"<lang>": {"<KEY>": "<text>", ...}, ...}} - the
        // bundle-name wrapper is the single top-level property, langs sit directly under it
        var cardNameRoot = cardNameDoc.RootElement.EnumerateObject().First().Value;
        var skillDescRoot = skillDescDoc.RootElement.EnumerateObject().First().Value;

        if (!cardNameRoot.TryGetProperty(lang, out var cardNameLangObj))
        {
            throw new InvalidOperationException(
                $"language key '{lang}' not found in {Path.GetFileName(cardNameTextJsonPath)}");
        }
        if (!skillDescRoot.TryGetProperty(lang, out var skillDescLangObj))
        {
            throw new InvalidOperationException(
                $"language key '{lang}' not found in {Path.GetFileName(skillDescTextJsonPath)}");
        }

        Lang = lang;

        // longest-first: the Fanfare-excluded filler starts with the exact same text as the plain
        // filler, so the plain one has to be tried LAST or it would match first and leave the
        // Fanfare-excluded filler's own remainder ("（...ファンファーレ...）") stuck onto the result
        var evolutionFillers = (EvolutionFillers.TryGetValue(lang, out var fillers) ? fillers : [])
            .OrderByDescending(f => f.Length)
            .ToArray();

        var id2Name = new Dictionary<int, string>();
        var rawName2Id = new Dictionary<string, int>();
        foreach (var prop in cardNameLangObj.EnumerateObject())
        {
            if (!prop.Name.StartsWith(CardNamePrefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (!int.TryParse(prop.Name.AsSpan(CardNamePrefix.Length), out var cardId))
            {
                continue;
            }

            var name = prop.Value.GetString() ?? string.Empty;
            id2Name[cardId] = name;
            // last writer wins on duplicate raw names (distinct cards can share a displayed name);
            // this is a best-effort reverse lookup for hyperlink resolution, not a strict inverse
            rawName2Id[CardTextMarkup.StripNotation(name)] = cardId;
        }

        // skilldesctext keys are "<cardId>" (no suffix - non-evolving spells/amulets, exactly one
        // slot) or "<cardId>_01"/"<cardId>_02" (evolving followers - unevolved/evolved state).
        var baseDescByCard = new Dictionary<int, string>();
        var evoDescByCard = new Dictionary<int, string>();
        foreach (var prop in skillDescLangObj.EnumerateObject())
        {
            if (!prop.Name.StartsWith(SkillDescPrefix, StringComparison.Ordinal))
            {
                continue;
            }
            var key = prop.Name[SkillDescPrefix.Length..];
            // strip "<<...>>" battle-time value templates here, at the source - this way every
            // downstream property (Id2Desc, Id2RawFullDesc, and Id2AdditionalDesc, which pastes
            // in Id2Desc's already-cleaned values) never sees them
            var text = CardTextMarkup.StripDynamicValueTemplates(prop.Value.GetString() ?? string.Empty);

            var underscoreIdx = key.IndexOf('_');
            var idPart = underscoreIdx >= 0 ? key[..underscoreIdx] : key;
            if (!int.TryParse(idPart, out var cardId))
            {
                continue;
            }

            var slot = underscoreIdx >= 0 ? key[(underscoreIdx + 1)..] : null;
            if (slot == "02")
            {
                evoDescByCard[cardId] = text;
            }
            else
            {
                baseDescByCard[cardId] = text;
            }
        }

        var id2Desc = new Dictionary<int, string>();
        foreach (var (cardId, baseDesc) in baseDescByCard)
        {
            id2Desc[cardId] = CardTextComposer.BuildDesc(baseDesc, evoDescByCard.GetValueOrDefault(cardId));
        }
        // guard for an evo-only entry with no base counterpart - not expected in real data
        foreach (var (cardId, evoDesc) in evoDescByCard)
        {
            if (!id2Desc.ContainsKey(cardId))
            {
                id2Desc[cardId] = CardTextComposer.BuildDesc(null, evoDesc);
            }
        }

        Id2Name = id2Name;
        RawName2Id = rawName2Id;
        Id2Desc = id2Desc;

        var id2Stats = cardMasterCsvGzPath is null
            ? new Dictionary<int, CardStats>()
            : StatsLoader.LoadUnevolvedStats(cardMasterCsvGzPath);

        // "{Type} {Cost}" for non-Followers, "{Type} {Cost} {Power}/{Life}" for Followers - same
        // -1-means-hide-stats convention as StatsLoader/DeckEditScreen/DescUserControl. Empty
        // string (nothing appended) when there's no stats source or the card isn't in it.
        string StatsTextOf(int cardId)
        {
            if (!id2Stats.TryGetValue(cardId, out var s))
            {
                return string.Empty;
            }
            var abbrev = s.CardType.Abbreviation();
            return s.Power == -1 ? $"{abbrev} {s.Cost}" : $"{abbrev} {s.Cost} {s.Power}/{s.Life}";
        }

        // strips a known filler PREFIX (if present) rather than discarding the whole string, so
        // any real text appended after it (e.g. a "treated as X" nickname annotation) survives
        string StripEvolutionFiller(string text)
        {
            var trimmed = text.Trim();
            foreach (var filler in evolutionFillers)
            {
                if (trimmed.StartsWith(filler, StringComparison.Ordinal))
                {
                    return trimmed[filler.Length..].Trim();
                }
            }
            return trimmed;
        }

        string RawDescOf(int id) =>
            string.Join(' ', new[] { baseDescByCard.GetValueOrDefault(id), evoDescByCard.GetValueOrDefault(id) }
                .OfType<string>()
                .Select(StripEvolutionFiller)
                .Where(d => d.Length > 0));

        (string Name, string Desc)? ResolveCardRaw(string rawName) =>
            rawName2Id.TryGetValue(rawName, out var refId) && id2Name.TryGetValue(refId, out var refName)
                ? (refName, RawDescOf(refId))
                : null;

        (string Name, string Desc, string StatsText)? ResolveCard(string rawName) =>
            rawName2Id.TryGetValue(rawName, out var refId) && id2Name.TryGetValue(refId, out var refName)
                ? (refName, id2Desc.GetValueOrDefault(refId, string.Empty), StatsTextOf(refId))
                : null;

        var id2RawFullDesc = new Dictionary<int, string>();
        var id2AdditionalDesc = new Dictionary<int, string>();
        foreach (var (cardId, name) in id2Name)
        {
            // effect 2 descriptions is not defined yet.
            id2RawFullDesc[cardId] = CardTextComposer.BuildRawFullDesc(name, RawDescOf(cardId), ResolveCardRaw);
            id2AdditionalDesc[cardId] = CardTextComposer.BuildAdditionalDesc(
                name, id2Desc.GetValueOrDefault(cardId, string.Empty), ResolveCard);
        }

        Id2RawFullDesc = id2RawFullDesc;
        Id2AdditionalDesc = id2AdditionalDesc;
    }
}
