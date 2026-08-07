using OpenVerse.Common;

namespace OpenVerse.Tests;

// Name.txt said nothing about what it named, so it became username.txt. anyone who already filled in the old one is
// still using it, and a rename that silently stops reading their file is worse than the ambiguity it fixed
public class SettingFileNameTests
{
    static string Sandbox()
    {
        var d = Path.Combine(Path.GetTempPath(), "ov-name-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void TheNewNameIsRead()
    {
        var d = Sandbox();
        File.WriteAllText(Path.Combine(d, "username.txt"), "Atamol");

        Assert.Equal("Atamol", NameResolver.FromFile(d));
    }

    [Fact]
    public void TheOldNameStillWorks()
    {
        var d = Sandbox();
        File.WriteAllText(Path.Combine(d, "name.txt"), "Atamol");

        Assert.Equal("Atamol", NameResolver.FromFile(d));
    }

    // both present: the one the build writes today wins, so a stale leftover cannot override a fresh edit
    [Fact]
    public void TheNewNameWinsOverTheOld()
    {
        var d = Sandbox();
        File.WriteAllText(Path.Combine(d, "username.txt"), "current");
        File.WriteAllText(Path.Combine(d, "name.txt"), "stale");

        Assert.Equal("current", NameResolver.FromFile(d));
    }

    // the shipped file is all comment, which has to read as "unset" rather than as a name
    [Fact]
    public void ACommentOnlyFileIsUnset()
    {
        var d = Sandbox();
        File.WriteAllText(Path.Combine(d, "username.txt"), "# put your in-game name here\n");

        Assert.Null(NameResolver.FromFile(d));
    }

    // a new file left as the shipped comment must not shadow an old one someone actually filled in
    [Fact]
    public void AnEmptyNewFileFallsThroughToTheOld()
    {
        var d = Sandbox();
        File.WriteAllText(Path.Combine(d, "username.txt"), "# put your in-game name here\n");
        File.WriteAllText(Path.Combine(d, "name.txt"), "Atamol");

        Assert.Equal("Atamol", NameResolver.FromFile(d));
    }

    [Fact]
    public void NeitherFileIsUnset() => Assert.Null(NameResolver.FromFile(Sandbox()));
}
