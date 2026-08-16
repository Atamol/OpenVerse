using Microsoft.Data.Sqlite;

namespace OpenVerse.Common;

// Per-user switch for the Unlimited relaxations the guild button flips. The client reads the card master and
// load/index once at login, so `served` holds what the running session got: while it disagrees with is_on the
// client is on stale rules and has to go back to the title
public sealed class UnlockStore
{
    readonly string _connString;

    public UnlockStore(string dbPath)
    {
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        SQLitePCL.Batteries_V2.Init();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS unlocks (
                user_key TEXT PRIMARY KEY,
                is_on INTEGER NOT NULL DEFAULT 0,
                served INTEGER NOT NULL DEFAULT 0
            )
            """);
        // CREATE IF NOT EXISTS leaves a db that predates this column alone
        try { Exec(c, "ALTER TABLE unlocks ADD COLUMN served INTEGER NOT NULL DEFAULT 0"); }
        catch (SqliteException) { }
    }

    SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        return c;
    }

    static void Exec(SqliteConnection c, string sql, params (string, object?)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    bool Flag(string userKey, string column)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {column} FROM unlocks WHERE user_key = $u";
        cmd.Parameters.AddWithValue("$u", userKey);
        return cmd.ExecuteScalar() is long v && v != 0;
    }

    public bool IsOn(string userKey) => Flag(userKey, "is_on");

    public bool Toggle(string userKey)
    {
        var next = !IsOn(userKey);
        Set(userKey, next);
        return next;
    }

    public void Set(string userKey, bool on)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO unlocks (user_key, is_on, served) VALUES ($u, $v, 0)
            ON CONFLICT(user_key) DO UPDATE SET is_on = excluded.is_on
            """,
            ("$u", userKey), ("$v", on ? 1 : 0));
    }

    // called when load/index goes out: from here on the client is running on this value
    public void MarkServed(string userKey)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO unlocks (user_key, is_on, served) VALUES ($u, 0, 0)
            ON CONFLICT(user_key) DO UPDATE SET served = unlocks.is_on
            """,
            ("$u", userKey));
    }

    public bool ReloadPending(string userKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT is_on <> served FROM unlocks WHERE user_key = $u";
        cmd.Parameters.AddWithValue("$u", userKey);
        return cmd.ExecuteScalar() is long v && v != 0;
    }
}
