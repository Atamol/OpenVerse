using System.Text;
using System.Text.RegularExpressions;

namespace OpenVerse.Common;

// Builds user_sleeve_list (array of {sleeve_id} objects) granting every sleeve. The client reads
// each element's sleeve_id unconditionally, so the shape must be objects, not bare ids. The id
// universe is the sleeve asset manifest (card_sleeve_<id>[_m].unity3d); DEFAULT_SLEEVE_ID is always
// included. Ids without a client sleeve_master row silently fall back to the default at render time.
public static partial class SleeveListBuilder
{
    public const long DefaultSleeveId = 3000011;

    [GeneratedRegex(@"card_sleeve_(\d+)(?:_m)?\.unity3d")]
    private static partial Regex SleeveIdRegex();

    public static string BuildJson(string manifestPath)
    {
        var ids = new SortedSet<long> { DefaultSleeveId };
        if (File.Exists(manifestPath))
            foreach (var line in File.ReadLines(manifestPath))
            {
                var m = SleeveIdRegex().Match(line);
                if (m.Success && long.TryParse(m.Groups[1].Value, out var id)) ids.Add(id);
            }

        var sb = new StringBuilder("[");
        var first = true;
        foreach (var id in ids)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"sleeve_id\":").Append(id).Append('}');
        }
        sb.Append(']');
        return sb.ToString();
    }
}
