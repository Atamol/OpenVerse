using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenVerse.Common;

// resolved deck for a battle participant, written by the API at do_matching and read by the Battle process at Matched.
// the two run as separate processes sharing openverse.db. keyed by (room_id, is_owner) — the room_id is what the client
// sends as the battle-socket "BattleId" header, and owner/visitor is stable on both sides (API knows room.OwnerUdid,
// Battle knows the first session in the battle group). viewer_id is unusable: a cached client viewer_id can diverge
// from the API's per-udid assignment
public sealed class BattleDeck
{
    public string RoomId { get; set; } = "";
    public bool IsOwner { get; set; }
    public int ClassId { get; set; }
    public int SubClassId { get; set; } = 10;
    public int CharaId { get; set; }
    public long SleeveId { get; set; } = 3000011L;
    public int LeaderSkinId { get; set; }
    public int[] CardIds { get; set; } = [];
    // the API owns the name (only it sees the udid and the per-machine registration), so it rides along here rather
    // than the Battle process inventing a second one
    public string UserName { get; set; } = "";
}

public sealed class BattleDeckStore
{
    readonly string _connString;

    public BattleDeckStore(string dbPath)
    {
        _connString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        SQLitePCL.Batteries_V2.Init();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DROP TABLE IF EXISTS battle_deck;
            CREATE TABLE IF NOT EXISTS battle_deck (
                room_id TEXT NOT NULL,
                is_owner INTEGER NOT NULL,
                class_id INTEGER NOT NULL,
                sub_class_id INTEGER NOT NULL DEFAULT 10,
                chara_id INTEGER NOT NULL,
                sleeve_id INTEGER NOT NULL DEFAULT 3000011,
                leader_skin_id INTEGER NOT NULL DEFAULT 0,
                card_ids TEXT NOT NULL DEFAULT '[]',
                user_name TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (room_id, is_owner)
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

    public void Set(BattleDeck d)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO battle_deck
            (room_id, is_owner, class_id, sub_class_id, chara_id, sleeve_id, leader_skin_id, card_ids, user_name)
            VALUES ($r, $o, $c, $s, $ch, $sl, $lk, $cards, $name)
            """;
        cmd.Parameters.AddWithValue("$r", d.RoomId);
        cmd.Parameters.AddWithValue("$o", d.IsOwner ? 1 : 0);
        cmd.Parameters.AddWithValue("$c", d.ClassId);
        cmd.Parameters.AddWithValue("$s", d.SubClassId);
        cmd.Parameters.AddWithValue("$ch", d.CharaId);
        cmd.Parameters.AddWithValue("$sl", d.SleeveId);
        cmd.Parameters.AddWithValue("$lk", d.LeaderSkinId);
        cmd.Parameters.AddWithValue("$cards", JsonSerializer.Serialize(d.CardIds));
        cmd.Parameters.AddWithValue("$name", d.UserName);
        cmd.ExecuteNonQuery();
    }

    public BattleDeck? Get(string roomId, bool isOwner)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM battle_deck WHERE room_id = $r AND is_owner = $o";
        cmd.Parameters.AddWithValue("$r", roomId);
        cmd.Parameters.AddWithValue("$o", isOwner ? 1 : 0);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new BattleDeck
        {
            RoomId = r.GetString(r.GetOrdinal("room_id")),
            IsOwner = r.GetInt32(r.GetOrdinal("is_owner")) != 0,
            ClassId = r.GetInt32(r.GetOrdinal("class_id")),
            SubClassId = r.GetInt32(r.GetOrdinal("sub_class_id")),
            CharaId = r.GetInt32(r.GetOrdinal("chara_id")),
            SleeveId = r.GetInt64(r.GetOrdinal("sleeve_id")),
            LeaderSkinId = r.GetInt32(r.GetOrdinal("leader_skin_id")),
            CardIds = JsonSerializer.Deserialize<int[]>(r.GetString(r.GetOrdinal("card_ids"))) ?? [],
            UserName = r.GetString(r.GetOrdinal("user_name")),
        };
    }
}
