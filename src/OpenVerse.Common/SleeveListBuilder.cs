using System.Text;
using System.Text.RegularExpressions;

namespace OpenVerse.Common;

// shape must be {sleeve_id} objects, not bare ids (the client reads each element's sleeve_id)
// ids without a client sleeve_master row fall back to the default at render, so granting all is safe
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
