using System.Linq;
using System.Text.RegularExpressions;

namespace OpenVerse.Decker.Data;

/// <summary>
/// Helper to process shadowverse card text with bbcode bracket notation.<br/>
/// for example: "[u][ffcd45]ツインプリズナー・グラス[-][/u]"
/// </summary>
public static class CardTextMarkup
{
    // hyperlink format is "[u][ffcd45]text[-][/u]" or "[u][524522]text[-][/u]".
    // but sometimes [-] can be repeated 1-3 times.
    private static readonly Regex HyperlinkTag =
        new(@"\[u\]\[(?<color>ffcd45|524522)\](?<text>.*?)(?:\[-\]|\[\d+\])*\[-\]\[/u\]", RegexOptions.Compiled | RegexOptions.Singleline);

    // [b]text[/b] is special bbcode used in decker to render card title in addition desc ui
    private static readonly Regex BoldKeywordTag =
        new(@"\[b\](?<text>.*?)(?:\[-\]|\[\d+\])*\[/b\]", RegexOptions.Compiled | RegexOptions.Singleline);

    // "[rub<furigana>]base[/rub]" - ruby/furigana annotation; base is what's actually displayed
    private static readonly Regex RubyTag =
        new(@"\[rub<(?<ruby>.*?)>\](?<text>.*?)\[/rub\]", RegexOptions.Compiled | RegexOptions.Singleline);

    // any remaining color bbcode  "[RRGGBB]text[-]" or "[RRGGBBAA]text[-]"
    private static readonly Regex ColorTag =
        new(@"\[(?<color>[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)\](?<text>.*?)\[-\]", RegexOptions.Compiled | RegexOptions.Singleline);

    // any remaining bare "[u]text[/u]" underline not consumed by the hyperlink tag above
    private static readonly Regex UnderlineTag =
        new(@"\[u\](?<text>.*?)\[/u\]", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ItalicTag =
        new(@"\[i\](?<text>.*?)\[/i\]", RegexOptions.Compiled | RegexOptions.Singleline);

    // a macro type format used in battle like "<<{me.hand_self.count}+1??>>" or
    // "<<{me.destroyed_card_list.tribe=artifact.unique_base_card_id_card.count}>>"
    private static readonly Regex DynamicValueTemplate =
        new(@"<<[^<>]*>>", RegexOptions.Compiled);

    // strip all dynamic value templates
    public static string StripDynamicValueTemplates(string text)
    {
        var result = text;
        string previous;
        do
        {
            previous = result;
            result = DynamicValueTemplate.Replace(result, "");
        } while (result != previous);

        return result;
    }

    // order matters: ruby first (may sit inside a hyperlink/color wrapper), then the compound
    // hyperlink tag (before the generic color tag would otherwise grab only its color half),
    // then bold, then whatever generic color/underline/italic is left over
    private static readonly Regex[] StripOrder =
    [
        RubyTag, HyperlinkTag, BoldKeywordTag, ColorTag, UnderlineTag, ItalicTag,
    ];

    // returns the plain, in-game-displayed text with every known bracket-tag notation removed.
    // Bare numeric brackets like "[1]"/"[3]" (seen in some token/mode card names, e.g. "収穫祭[1]")
    // are deliberately NOT touched by anything here - they're genuine displayed text, not notation
    // (confirmed by the same full-data bbcode audit that found the ColorTag gap above).
    public static string StripNotation(string text)
    {
        var result = text;
        string previous;
        do
        {
            previous = result;
            foreach (var pattern in StripOrder)
            {
                result = pattern.Replace(result, "${text}");
            }
        } while (result != previous);

        return result;
    }

    // extracts the stripped plain display text of every hyperlink/keyword
    // reference found in raw (markup-preserved) text, in order of appearance. Duplicates are kept
    // as-is - callers doing recursive traversal are expected to dedupe/cycle-protect themselves.
    public static IReadOnlyList<string> ExtractHyperlinkTargets(string rawText)
    {
        var targets = new List<string>();
        foreach (Match match in HyperlinkTag.Matches(rawText))
        {
            targets.Add(StripNotation(match.Groups["text"].Value));
        }
        foreach (Match match in BoldKeywordTag.Matches(rawText))
        {
            targets.Add(StripNotation(match.Groups["text"].Value));
        }
        return targets;
    }

    // abstract way called from Segmentize() to generate ui component from the text data.
    public readonly record struct TextSegment(string Text, bool IsHyperlink, bool IsBold, string? ColorHex);

    // interpret raw text to structured data.
    public static IReadOnlyList<TextSegment> Segmentize(string rawText)
    {
        var text = rawText;

        // HyperlinkTag and ColorTag can match the SAME underlying "[color]...[-]" span (a
        // hyperlink tag's inner color wrap also satisfies the bare ColorTag pattern) - collect
        // all three tag forms, then keep only the outermost/longest match at each position and
        // drop anything fully nested inside an already-kept match
        var candidates = HyperlinkTag.Matches(text).Cast<Match>().Select(m => (Match: m, IsHyperlink: true, IsBold: false))
            .Concat(ColorTag.Matches(text).Cast<Match>().Select(m => (Match: m, IsHyperlink: false, IsBold: false)))
            .Concat(BoldKeywordTag.Matches(text).Cast<Match>().Select(m => (Match: m, IsHyperlink: true, IsBold: true)))
            .OrderBy(x => x.Match.Index)
            .ThenByDescending(x => x.Match.Length)
            .ToList();

        var kept = new List<(Match Match, bool IsHyperlink, bool IsBold)>();
        foreach (var c in candidates)
        {
            if (kept.Any(k => c.Match.Index >= k.Match.Index && c.Match.Index + c.Match.Length <= k.Match.Index + k.Match.Length))
            {
                continue;
            }
            kept.Add(c);
        }
        kept = [.. kept.OrderBy(c => c.Match.Index)];

        var segments = new List<TextSegment>();
        var cursor = 0;
        foreach (var (m, isHyperlink, isBold) in kept)
        {
            if (m.Index > cursor)
            {
                segments.Add(new TextSegment(StripNotation(text[cursor..m.Index]), false, false, null));
            }

            var colorGroup = m.Groups["color"];
            segments.Add(new TextSegment(
                StripNotation(m.Groups["text"].Value), isHyperlink, isBold, colorGroup.Success ? colorGroup.Value : null));
            cursor = m.Index + m.Length;
        }
        if (cursor < text.Length)
        {
            segments.Add(new TextSegment(StripNotation(text[cursor..]), false, false, null));
        }

        return segments;
    }
}
