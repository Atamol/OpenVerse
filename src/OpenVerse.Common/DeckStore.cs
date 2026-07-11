using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenVerse.Common;

public sealed class DeckStore
{
    readonly string _connString;

    public DeckStore(string dbPath)
    {
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        SQLitePCL.Batteries_V2.Init();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS decks (
                user_key TEXT NOT NULL,
                deck_no INTEGER NOT NULL,
                format INTEGER NOT NULL,
                class_id INTEGER NOT NULL,
                sub_class_id INTEGER NOT NULL DEFAULT 10,
                deck_name TEXT NOT NULL,
                sleeve_id INTEGER NOT NULL DEFAULT 3000011,
                leader_skin_id INTEGER NOT NULL DEFAULT 0,
                is_random_leader_skin INTEGER NOT NULL DEFAULT 0,
                leader_skin_id_list TEXT NOT NULL DEFAULT '[]',
                rotation_id TEXT,
                card_id_array TEXT NOT NULL DEFAULT '[]',
                display_order INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                is_deleted INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (user_key, deck_no)
            )
            """);
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

    public List<Deck> List(string userKey, int? format = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = format is null
            ? "SELECT * FROM decks WHERE user_key = $u AND is_deleted = 0 ORDER BY format, display_order, deck_no"
            : "SELECT * FROM decks WHERE user_key = $u AND format = $f AND is_deleted = 0 ORDER BY display_order, deck_no";
        cmd.Parameters.AddWithValue("$u", userKey);
        if (format is int f) cmd.Parameters.AddWithValue("$f", f);
        using var r = cmd.ExecuteReader();
        var list = new List<Deck>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public Deck? Get(string userKey, int deckNo)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM decks WHERE user_key = $u AND deck_no = $n AND is_deleted = 0";
        cmd.Parameters.AddWithValue("$u", userKey);
        cmd.Parameters.AddWithValue("$n", deckNo);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    // deck_no is the (user_key, deck_no) primary key, so it must be unique per user across all formats
    // scoping MAX to a format would collide format 1 and 2 at deck_no 1, and the second save overwrites the first
    public int NextDeckNo(string userKey, int format)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(deck_no), 0) + 1 FROM decks WHERE user_key = $u";
        cmd.Parameters.AddWithValue("$u", userKey);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void Save(Deck d)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO decks (user_key, deck_no, format, class_id, sub_class_id, deck_name, sleeve_id, leader_skin_id, is_random_leader_skin, leader_skin_id_list, rotation_id, card_id_array, display_order, created_at, is_deleted)
            VALUES ($u, $n, $f, $c, $sc, $name, $sl, $lsk, $irs, $lskl, $rot, $cards, $ord, $created, 0)
            ON CONFLICT(user_key, deck_no) DO UPDATE SET
                format = excluded.format,
                class_id = excluded.class_id,
                sub_class_id = excluded.sub_class_id,
                deck_name = excluded.deck_name,
                sleeve_id = excluded.sleeve_id,
                leader_skin_id = excluded.leader_skin_id,
                is_random_leader_skin = excluded.is_random_leader_skin,
                leader_skin_id_list = excluded.leader_skin_id_list,
                rotation_id = excluded.rotation_id,
                card_id_array = excluded.card_id_array,
                is_deleted = 0
            """,
            ("$u", d.UserKey), ("$n", d.DeckNo), ("$f", d.Format),
            ("$c", d.ClassId), ("$sc", d.SubClassId), ("$name", d.DeckName),
            ("$sl", d.SleeveId), ("$lsk", d.LeaderSkinId), ("$irs", d.IsRandomLeaderSkin ? 1 : 0),
            ("$lskl", JsonSerializer.Serialize(d.LeaderSkinIdList)), ("$rot", d.RotationId),
            ("$cards", JsonSerializer.Serialize(d.CardIdArray)), ("$ord", d.DisplayOrder),
            ("$created", d.CreatedAt.ToString("o")));
    }

    public void Delete(string userKey, int deckNo)
    {
        using var c = Open();
        Exec(c, "UPDATE decks SET is_deleted = 1 WHERE user_key = $u AND deck_no = $n",
            ("$u", userKey), ("$n", deckNo));
    }

    public void DeleteMany(string userKey, int[] deckNos)
    {
        if (deckNos.Length == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var n in deckNos)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE decks SET is_deleted = 1 WHERE user_key = $u AND deck_no = $n";
            cmd.Parameters.AddWithValue("$u", userKey);
            cmd.Parameters.AddWithValue("$n", n);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void UpdateOrder(string userKey, int format, int[] order)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        for (int i = 0; i < order.Length; i++)
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE decks SET display_order = $ord WHERE user_key = $u AND deck_no = $n AND format = $f";
            cmd.Parameters.AddWithValue("$ord", i);
            cmd.Parameters.AddWithValue("$u", userKey);
            cmd.Parameters.AddWithValue("$n", order[i]);
            cmd.Parameters.AddWithValue("$f", format);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    static Deck Read(SqliteDataReader r) => new()
    {
        UserKey = r.GetString(r.GetOrdinal("user_key")),
        DeckNo = r.GetInt32(r.GetOrdinal("deck_no")),
        Format = r.GetInt32(r.GetOrdinal("format")),
        ClassId = r.GetInt32(r.GetOrdinal("class_id")),
        SubClassId = r.GetInt32(r.GetOrdinal("sub_class_id")),
        DeckName = r.GetString(r.GetOrdinal("deck_name")),
        SleeveId = r.GetInt64(r.GetOrdinal("sleeve_id")),
        LeaderSkinId = r.GetInt32(r.GetOrdinal("leader_skin_id")),
        IsRandomLeaderSkin = r.GetInt32(r.GetOrdinal("is_random_leader_skin")) != 0,
        LeaderSkinIdList = JsonSerializer.Deserialize<int[]>(r.GetString(r.GetOrdinal("leader_skin_id_list"))) ?? [],
        RotationId = r.IsDBNull(r.GetOrdinal("rotation_id")) ? null : r.GetString(r.GetOrdinal("rotation_id")),
        CardIdArray = JsonSerializer.Deserialize<int[]>(r.GetString(r.GetOrdinal("card_id_array"))) ?? [],
        DisplayOrder = r.GetInt32(r.GetOrdinal("display_order")),
        CreatedAt = DateTime.Parse(r.GetString(r.GetOrdinal("created_at"))),
    };
}
