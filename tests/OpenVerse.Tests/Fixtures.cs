using System.IO.Compression;

namespace OpenVerse.Tests;

// The card master is not shipped (each host extracts its own) and neither is Assembly-CSharp, so the engine tests skip
// rather than fail when they are absent. that skip is silent, which is why the release layout moving data under
// server/ took every engine test out of the run without turning anything red. TheFixturesResolve below is the guard
static class Fixtures
{
    static string Root => Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    // Release/data was the layout before the zip was restructured. both are listed so an old tree still resolves
    static readonly string[] DataDirs = ["release/server/data", "release/data"];

    public static string? DataDir() =>
        DataDirs.Select(d => Path.Combine(Root, d.Replace('/', Path.DirectorySeparatorChar)))
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "card_master_full.csv.gz")));

    // the engine is a net48 build of the client's own assemblies, copied next to the test binary by the build
    public static bool EngineDlls =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll"));

    public static string EngineHostSrc => Path.Combine(Root, "src", "OpenVerse.EngineHost");

    public static string CopiedEngineHost => Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll");

    public static string? CardMasterCsv()
    {
        if (!EngineDlls || DataDir() is not { } dir) return null;
        using var fs = File.OpenRead(Path.Combine(dir, "card_master_full.csv.gz"));
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }
}

public class FixtureResolutionTests
{
    // without this the whole engine half of the suite can go green while never running. if the DLLs are here, the tree
    // was built, and a card master that cannot be found means the path moved rather than that this host lacks the data
    [Fact]
    public void TheFixturesResolveWhenTheEngineIsBuilt()
    {
        if (!Fixtures.EngineDlls) return;
        Assert.True(Fixtures.DataDir() is not null,
            "the engine DLLs are built but card_master_full.csv.gz resolved nowhere: the release layout moved and "
            + "every engine test is silently skipping. Add the new location to Fixtures.DataDirs");
    }

    // OpenVerse.EngineHost is not in OpenVerse.slnx, so `dotnet build` never compiles it and an edit there is silently
    // dropped: the tests keep running the previous DLL and pass. Cost me a wrong conclusion once already
    [Fact]
    public void TheCopiedEngineHostIsNotOlderThanItsSource()
    {
        if (!Fixtures.EngineDlls || !Directory.Exists(Fixtures.EngineHostSrc)) return;
        var newest = Directory.EnumerateFiles(Fixtures.EngineHostSrc, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty()
            .Max();
        var built = File.GetLastWriteTimeUtc(Fixtures.CopiedEngineHost);

        Assert.True(built >= newest,
            $"OpenVerse.EngineHost.dll is older than its source ({built:HH:mm:ss} < {newest:HH:mm:ss}). The solution "
            + "does not build that project, so your change is not in this run. Build it explicitly:\n"
            + "  dotnet build src/OpenVerse.EngineHost/OpenVerse.EngineHost.csproj -c Release");
    }
}
