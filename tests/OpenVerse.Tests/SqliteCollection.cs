namespace OpenVerse.Tests;

// SqliteConnection.ClearAllPools() (these classes' Dispose) is process-global, so serialize the sqlite tests
[CollectionDefinition("Sqlite", DisableParallelization = true)]
public class SqliteCollection { }
