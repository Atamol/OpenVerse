using Microsoft.Data.Sqlite;

namespace OpenVerse.Common;

// Self-hosted deck codes. The real portal's deck-code backend is gone, so we mint and resolve
// codes ourselves; a code maps to the deck payload the client's GetDeckDataFromCode expects.
public sealed class DeckCodeStore
{
    public sealed record Entry(int Clan, int SubClan, int Format, int[] CardIds, string? RotationId);

    readonly string _connString;

    public DeckCodeStore(string dbPath)
    {
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        SQLitePCL.Batteries_V2.Init();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS deck_codes (
                code TEXT PRIMARY KEY,
                clan INTEGER NOT NULL,
                sub_clan INTEGER NOT NULL DEFAULT 10,
                format INTEGER NOT NULL,
                card_ids TEXT NOT NULL,
                rotation_id TEXT,
                created_at TEXT NOT NULL
            )
            """;
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    public bool Exists(string code)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM deck_codes WHERE code = $c";
        cmd.Parameters.AddWithValue("$c", code);
        return cmd.ExecuteScalar() is not null;
    }

    public void Save(string code, Entry e)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO deck_codes (code, clan, sub_clan, format, card_ids, rotation_id, created_at)
            VALUES ($code, $clan, $sub, $fmt, $cards, $rot, $at)
            """;
        cmd.Parameters.AddWithValue("$code", code);
        cmd.Parameters.AddWithValue("$clan", e.Clan);
        cmd.Parameters.AddWithValue("$sub", e.SubClan);
        cmd.Parameters.AddWithValue("$fmt", e.Format);
        cmd.Parameters.AddWithValue("$cards", string.Join(',', e.CardIds));
        cmd.Parameters.AddWithValue("$rot", (object?)e.RotationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public Entry? Get(string code)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT clan, sub_clan, format, card_ids, rotation_id FROM deck_codes WHERE code = $c";
        cmd.Parameters.AddWithValue("$c", code);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var cards = r.GetString(3).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        var rot = r.IsDBNull(4) ? null : r.GetString(4);
        return new Entry(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), cards, rot);
    }

    // created_at is ISO 8601 UTC ("O"), so a string compare against the cutoff is chronological.
    public int PurgeOlderThan(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow.Subtract(maxAge).ToString("O");
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM deck_codes WHERE created_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        return cmd.ExecuteNonQuery();
    }
}
