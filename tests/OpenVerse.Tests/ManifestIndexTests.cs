using System.Text;
using OpenVerse.Api;

namespace OpenVerse.Tests;

// a manifest row the client can't satisfy is what wedges it at the title, so the rewrite has to answer for every row:
// real fingerprint when the bundle is here, out of the pre-fetch categories when it isn't
public class ManifestIndexTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), $"ov-mi-{Guid.NewGuid():N}");

    public ManifestIndexTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "v"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    string Sidecar => Path.Combine(_dir, "openverse-bundles.csv");

    void Bundle(string sub, string name, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(_dir, sub, name), bytes);

    static byte[] Manifest(params string[] lines) => Encoding.UTF8.GetBytes(string.Join('\n', lines));

    static string[] Lines(byte[] b) => Encoding.UTF8.GetString(b).Split('\n');

    // the real card_shader_common is 297286 bytes and reads as 0.284 in the manifest, so sizes are MiB to three places
    [Fact]
    public void RewritesHashAndSizeFromTheFileOnDisk()
    {
        Bundle("a", "card_shader_common.unity3d", new byte[1048576 * 2]);
        var index = ManifestIndex.Build(_dir, Sidecar);

        var outp = index.Rewrite(
            Manifest("card_shader_common.unity3d,deadbeef,common,9.999,deadbeef,9.999"),
            out var rewritten, out var deferred);

        var cols = Lines(outp)[0].Split(',');
        Assert.Equal(1, rewritten);
        Assert.Equal(0, deferred);
        // md5 of 2 MiB of zeroes, and both the normal and small-resource columns carry it
        Assert.Equal("b2d1236c286a3c0704224fe4105eca49", cols[1]);
        Assert.Equal(cols[1], cols[4]);
        Assert.Equal("2", cols[3]);
        Assert.Equal("common", cols[2]);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("tutorial")]
    public void MovesRowsItCannotServeOutOfThePrefetchCategories(string category)
    {
        var index = ManifestIndex.Build(_dir, Sidecar);

        var outp = index.Rewrite(
            Manifest($"missing.unity3d,deadbeef,{category},1.5,deadbeef,1.5"),
            out var rewritten, out var deferred);

        var cols = Lines(outp)[0].Split(',');
        Assert.Equal(0, rewritten);
        Assert.Equal(1, deferred);
        Assert.NotEqual(category, cols[2]);
        // the hash stays as-is: it is still the only thing identifying the bundle if it ever shows up
        Assert.Equal("deadbeef", cols[1]);
    }

    [Fact]
    public void LeavesRowsItCannotServeAloneWhenTheyWereNeverPrefetched()
    {
        var index = ManifestIndex.Build(_dir, Sidecar);
        var line = "missing.unity3d,deadbeef,everytmp,1.5,deadbeef,1.5";

        var outp = index.Rewrite(Manifest(line), out _, out var deferred);

        Assert.Equal(0, deferred);
        Assert.Equal(line, Lines(outp)[0]);
    }

    [Fact]
    public void MatchesAudioRowsThatCarryTheirCacheSubdir()
    {
        Bundle("v", "voice_001.unity3d", [1, 2, 3]);
        var index = ManifestIndex.Build(_dir, Sidecar);

        index.Rewrite(Manifest("v/voice_001.unity3d,deadbeef,common,0.1,deadbeef,0.1"),
            out var rewritten, out _);

        Assert.Equal(1, rewritten);
    }

    [Fact]
    public void SecondBuildReusesTheSidecarInsteadOfRehashing()
    {
        Bundle("a", "one.unity3d", [1, 2, 3]);
        var first = ManifestIndex.Build(_dir, Sidecar);
        Assert.Equal(1, first.Hashed);

        var second = ManifestIndex.Build(_dir, Sidecar);
        Assert.Equal(1, second.Count);
        Assert.Equal(0, second.Hashed);
    }

    [Fact]
    public void RehashesAFileThatChanged()
    {
        var path = Path.Combine(_dir, "a", "one.unity3d");
        File.WriteAllBytes(path, [1, 2, 3]);
        ManifestIndex.Build(_dir, Sidecar);

        File.WriteAllBytes(path, [4, 5, 6, 7]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(1, ManifestIndex.Build(_dir, Sidecar).Hashed);
    }

    [Fact]
    public void PassesThroughLinesThatAreNotManifestRows()
    {
        var index = ManifestIndex.Build(_dir, Sidecar);
        var outp = index.Rewrite(Manifest("", "not,a,row"), out var rewritten, out var deferred);

        Assert.Equal(0, rewritten);
        Assert.Equal(0, deferred);
        Assert.Equal(["", "not,a,row"], Lines(outp));
    }
}
