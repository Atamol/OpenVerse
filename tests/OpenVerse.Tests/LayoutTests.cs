using System.Reflection;
using OpenVerse.Common;

namespace OpenVerse.Tests;

// three shapes have to resolve: the released zip (front doors at the top, build under server/), the repo root after a
// build (the same two exes copied up, build under release/server), and the staging copy itself
public class LayoutTests
{
    // Layout reads AppContext.BaseDirectory in a static ctor, so drive the resolver directly rather than the singleton
    static string Server(string from) =>
        (string)typeof(Layout).GetMethod("FindServer", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [from])!;

    static string Sandbox()
    {
        var d = Path.Combine(Path.GetTempPath(), "ov-layout-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    static void PlaceServer(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "OpenVerse.Api.exe"), "");
    }

    [Fact]
    public void TheReleasedZipFindsItsServerFolder()
    {
        var root = Sandbox();
        PlaceServer(Path.Combine(root, "server"));

        Assert.Equal(Path.Combine(root, "server"), Server(root));
    }

    // a developer runs the exe copied to the repo root, where the build sits under release/server
    [Fact]
    public void TheRepoRootReachesThroughRelease()
    {
        var root = Sandbox();
        PlaceServer(Path.Combine(root, "release", "server"));

        Assert.Equal(Path.Combine(root, "release", "server"), Server(root));
    }

    // running the staging copy: server/ is right there
    [Fact]
    public void TheStagingCopyFindsItsOwnServerFolder()
    {
        var rel = Path.Combine(Sandbox(), "release");
        PlaceServer(Path.Combine(rel, "server"));

        Assert.Equal(Path.Combine(rel, "server"), Server(rel));
    }

    // an older flat layout, everything beside the exe
    [Fact]
    public void AFlatFolderIsItsOwnServer()
    {
        var flat = Sandbox();
        PlaceServer(flat);

        Assert.Equal(flat, Server(flat));
    }

    // nothing built yet: answer with where it would go, so the caller reports a missing file not a missing folder
    [Fact]
    public void WithNothingBuiltItStillNamesAPlace()
    {
        var empty = Sandbox();

        Assert.Equal(Path.Combine(empty, "server"), Server(empty));
    }

    // the nesting matters: server/ beside the exe wins over release/server, or the staging copy would reach past itself
    [Fact]
    public void AnAdjacentServerWinsOverOneUnderRelease()
    {
        var root = Sandbox();
        PlaceServer(Path.Combine(root, "server"));
        PlaceServer(Path.Combine(root, "release", "server"));

        Assert.Equal(Path.Combine(root, "server"), Server(root));
    }
}
