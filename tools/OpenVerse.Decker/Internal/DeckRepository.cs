using System.IO;
using Microsoft.Data.Sqlite;
using OpenVerse.Common;

namespace OpenVerse.Decker.Internal;

/// <summary>
/// Wrapper for deck and deck store to implement loose coupling between the decker tool and the underlying database.
/// decker can touch only this class, not database.
/// </summary>
/// <param name="dbPath"></param>
public sealed class DeckRepository(string dbPath)
{
    // same to default location in OpenVerse.Launcher
    public static string DefaultDbPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Cygames", "Shadowverse", "openverse.db");

    public bool Exists() => File.Exists(dbPath);

    public List<string> ListUserKeys()
    {
        var keys = new List<string>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT user_key FROM decks";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            keys.Add(r.GetString(0));
        }
        return keys;
    }

    public List<(string UserKey, Deck Deck)> ListAllDecks()
    {
        var store = new DeckStore(dbPath);
        var result = new List<(string, Deck)>();
        foreach (var userKey in ListUserKeys())
        {
            foreach (var deck in store.List(userKey))
            {
                result.Add((userKey, deck));
            }
        }
        return result;
    }
}
