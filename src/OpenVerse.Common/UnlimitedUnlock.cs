using System.Text;

namespace OpenVerse.Common;

// What the guild toggle changes about the data the client is served. Unlimited only: the deck size and the built-in
// copy cap are constants in the client, and the only per-card overrides it reads are the two touched here
public static class UnlimitedUnlock
{
    public const int MaxCopies = 40;

    static readonly int ResurgentCol = Array.IndexOf(CardMasterCodec.Columns, "IsResurgentCard");
    static readonly int BaseCardIdCol = Array.IndexOf(CardMasterCodec.Columns, "base_card_id");
    static readonly int ClanCol = Array.IndexOf(CardMasterCodec.Columns, "clan");

    // IsAvailableFormat(Unlimited) rejects a resurgent card and GetSameKindNumMaxInFormat hands it 0, both read
    // straight off this column, so clearing it is the entire unlock
    public static string ClearResurgent(string csv) => ZeroColumn(csv, ResurgentCol);

    static string ZeroColumn(string csv, int column)
    {
        if (column < 0) return csv;
        var sb = new StringBuilder(csv.Length);
        var first = true;
        foreach (var line in csv.Split('\n'))
        {
            if (!first) sb.Append('\n');
            first = false;
            var (start, end) = FieldRange(line, column);
            if (start < 0 || (end - start == 1 && line[start] == '0')) sb.Append(line);
            else sb.Append(line, 0, start).Append('0').Append(line, end, line.Length - end);
        }
        return sb.ToString();
    }

    // The editor's mask is fixed to neutral plus the deck's own class, and neutral is clan 0, so a master where
    // every card reads 0 puts every class in front of every deck. Only safe because the battle runs on the other
    // master: the editor is hardwired to Default and card_master_id points the battle at the untouched one
    public static string ClearClan(string csv) => ZeroColumn(csv, ClanCol);

    // CardParameter reads this per card and lets it overwrite the built-in 3. It exists for nerfs, but nothing in the
    // client keeps the count below that 3, so the same door opens upward
    public static string RestrictedListJson(string csv, int copies = MaxCopies)
    {
        if (BaseCardIdCol < 0) return "{}";
        var ids = new SortedSet<int>();
        foreach (var line in csv.Split('\n'))
        {
            var (start, end) = FieldRange(line, BaseCardIdCol);
            if (start >= 0 && int.TryParse(line.AsSpan(start, end - start), out var id) && id > 0) ids.Add(id);
        }
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var id in ids)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(id).Append("\":").Append(copies);
        }
        return sb.Append('}').ToString();
    }

    // The voice columns are quoted and hold commas, so a plain Split lands on the wrong field. Returns the raw span of
    // one field so a caller can splice it without having to requote everything around it
    public static (int Start, int End) FieldRange(string line, int index)
    {
        var field = 0;
        var start = 0;
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted)
            {
                if (field == index) return (start, i);
                field++;
                start = i + 1;
            }
        }
        return field == index && start < line.Length ? (start, line.Length) : (-1, -1);
    }
}
