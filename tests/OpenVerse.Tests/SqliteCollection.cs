namespace OpenVerse.Tests;

// SqliteConnection.ClearAllPools() (called in these classes' Dispose to unlock temp db files) is
// process-global, so it must not fire while another class is mid-DB-operation. Serialize them.
[CollectionDefinition("Sqlite", DisableParallelization = true)]
public class SqliteCollection { }
