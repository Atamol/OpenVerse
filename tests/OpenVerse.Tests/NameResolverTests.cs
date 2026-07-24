using OpenVerse.Common;

namespace OpenVerse.Tests;

public class NameResolverTests
{
    [Fact]
    public void FileWinsOverEverything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ov-name-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "name.txt"), "# comment\n\n  Kirin  \n");
            Assert.Equal("Kirin", NameResolver.Resolve(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CommentOnlyFileIsIgnored()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ov-name-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "name.txt"), "# put your name here\n");
            Assert.Null(NameResolver.FromFile(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NoFileFallsThrough()
    {
        Assert.Null(NameResolver.FromFile(Path.Combine(Path.GetTempPath(), "ov-no-such-dir")));
    }

    // only meaningful where Steam is installed; the resolver must never throw regardless
    [Fact]
    public void SteamPersonaDoesNotThrow()
    {
        var ex = Record.Exception(() => NameResolver.FromSteam());
        Assert.Null(ex);
    }

    const string TwoAccounts = """
        "users"
        {
        	"76561198438858202"
        	{
        		"AccountName"		"atamol"
        		"PersonaName"		"Atamol"
        		"MostRecent"		"0"
        	}
        	"76561198249749324"
        	{
        		"AccountName"		"fukumitsu1"
        		"PersonaName"		"MaxSignal"
        		"MostRecent"		"1"
        	}
        }
        """;

    [Fact]
    public void PicksTheMostRecentAccountNotTheFirst()
    {
        Assert.Equal("MaxSignal", NameResolver.PersonaFromVdf(TwoAccounts));
    }

    [Fact]
    public void FallsBackToTheOnlyPersonaWhenNoneIsFlagged()
    {
        var vdf = TwoAccounts.Replace("\"MostRecent\"\t\t\"1\"", "\"MostRecent\"\t\t\"0\"");
        Assert.Equal("Atamol", NameResolver.PersonaFromVdf(vdf));
    }
}
