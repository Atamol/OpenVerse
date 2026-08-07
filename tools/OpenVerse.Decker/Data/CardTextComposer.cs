using System.Text;

namespace OpenVerse.Decker.Data;

/// <summary>
/// String composition helper for card text.
/// </summary>
public static class CardTextComposer
{

    /// <summary>
    /// build a unified description text for a card.<br/>
    /// meanwhile,<br/>
    /// <br/>
    /// 進化前<br/>
    /// <br/>
    /// (unevolved description)<br/>
    /// ---<br/>
    /// 進化後<br/>
    /// <br/>
    /// (evolved description)<br/>
    /// TODO i18n for 進化前/進化後
    /// </summary>
    /// <param name="baseDesc"></param>
    /// <param name="evoDesc"></param>
    /// <returns></returns>
    public static string BuildDesc(string? baseDesc, string? evoDesc)
    {
        if (evoDesc is null)
        {
            return baseDesc ?? string.Empty;
        }
        if (baseDesc is null)
        {
            return $"進化後\n\n{evoDesc}";
        }
        return $"進化前\n\n{baseDesc}\n---\n進化後\n\n{evoDesc}";
    }

    /// <summary>
    /// build a search blob string for a card including its own name, description and all hyperlinked cards and effects.
    /// TODO add effect desc
    /// </summary>
    /// <param name="ownRawName"></param>
    /// <param name="ownRawDesc"></param>
    /// <param name="resolveCard"></param>
    /// <param name="resolveEffect"></param>
    /// <returns></returns>
    public static string BuildRawFullDesc(
        string ownRawName,
        string ownRawDesc,
        Func<string, (string Name, string Desc)?> resolveCard,
        Func<string, (string Name, string Desc)?>? resolveEffect = null)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<string> { CardTextMarkup.StripNotation(ownRawName) };
        sb.Append(CardTextMarkup.StripNotation(ownRawName));
        sb.Append(' ');
        sb.Append(CardTextMarkup.StripNotation(ownRawDesc));
        AppendReferencesFlat(ownRawDesc, sb, visited, resolveCard, resolveEffect);
        return sb.ToString();
    }

    /// <summary>
    /// used to build the additional description text of a hyperlinked card in the parent's card description.
    /// </summary>
    /// <param name="ownRawName"></param>
    /// <param name="ownRawDesc"></param>
    /// <param name="resolveCard"></param>
    /// <param name="resolveEffect"></param>
    /// <returns></returns>
    public static string BuildAdditionalDesc(
        string ownRawName,
        string ownRawDesc,
        Func<string, (string Name, string Desc, string StatsText)?> resolveCard,
        Func<string, (string Name, string Desc, string StatsText)?>? resolveEffect = null)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<string> { CardTextMarkup.StripNotation(ownRawName) };
        AppendReferences(ownRawDesc, sb, visited, resolveCard, resolveEffect);
        return sb.ToString();
    }

    private static void AppendReferences(
        string rawDesc,
        StringBuilder sb,
        HashSet<string> visited,
        Func<string, (string Name, string Desc, string StatsText)?> resolveCard,
        Func<string, (string Name, string Desc, string StatsText)?>? resolveEffect)
    {
        foreach (var target in CardTextMarkup.ExtractHyperlinkTargets(rawDesc))
        {
            // if already appended (or is the origin card itself) then stops registering
            if (!visited.Add(target))
            {
                continue;
            }

            var resolved = resolveCard(target) ?? resolveEffect?.Invoke(target);
            // if can't resolve yet (e.g. an effect name, pending a future Effect2Desc) then skip
            if (resolved is not { } entry)
            {
                continue;
            }

            // add [b]...[/b] to render the name in bold in additional desc ui
            sb.Append("[b]").Append(entry.Name).Append("[/b]");
            if (entry.StatsText.Length > 0)
            {
                sb.Append(' ').Append(entry.StatsText);
            }
            sb.Append("\n\n").Append(entry.Desc).Append("\n\n");
            AppendReferences(entry.Desc, sb, visited, resolveCard, resolveEffect);
        }
    }

    private static void AppendReferencesFlat(
        string rawDesc,
        StringBuilder sb,
        HashSet<string> visited,
        Func<string, (string Name, string Desc)?> resolveCard,
        Func<string, (string Name, string Desc)?>? resolveEffect)
    {
        foreach (var target in CardTextMarkup.ExtractHyperlinkTargets(rawDesc))
        {
            // if already appended (or is the origin card itself) then skip
            if (!visited.Add(target))
            {
                continue;
            }

            var resolved = resolveCard(target) ?? resolveEffect?.Invoke(target);
            // if can't resolve yet (e.g. an effect name, pending a future Effect2Desc) then skip
            if (resolved is not { } entry)
            {
                continue;
            }

            sb.Append(' ').Append(CardTextMarkup.StripNotation(entry.Name));
            sb.Append(' ').Append(CardTextMarkup.StripNotation(entry.Desc));
            AppendReferencesFlat(entry.Desc, sb, visited, resolveCard, resolveEffect);
        }
    }
}
