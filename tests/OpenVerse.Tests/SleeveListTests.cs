using System.Text.Json;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class SleeveListTests : IDisposable
{
    readonly string _manifest = Path.Combine(Path.GetTempPath(), $"ov-sleeve-{Guid.NewGuid():N}.txt");

    public void Dispose() { try { File.Delete(_manifest); } catch { } }

    static long[] Ids(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("sleeve_id").GetInt64())];
    }

    [Fact]
    public void ExtractsDistinctIdsIncludingDefault()
    {
        File.WriteAllLines(_manifest,
        [
            "card_sleeve_1000110400.unity3d,hash,common,0.6,hash,0.6",
            "card_sleeve_1000110400_m.unity3d,hash,common,0.0,hash,0.0", // dup of same id
            "card_sleeve_101021010.unity3d,hash,common,0.6,hash,0.6",
            "unrelated_line.unity3d,hash",
        ]);
        var ids = Ids(SleeveListBuilder.BuildJson(_manifest));
        Assert.Contains(1000110400L, ids);
        Assert.Contains(101021010L, ids);
        Assert.Contains(SleeveListBuilder.DefaultSleeveId, ids);
        Assert.Equal(3, ids.Length); // two sleeves + default, dedup on _m
    }

    [Fact]
    public void ElementsAreObjectsNotBareIds()
    {
        File.WriteAllLines(_manifest, ["card_sleeve_101021010.unity3d,h,c,0,h,0"]);
        using var doc = JsonDocument.Parse(SleeveListBuilder.BuildJson(_manifest));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, e.ValueKind);
            Assert.True(e.TryGetProperty("sleeve_id", out var v) && v.ValueKind == JsonValueKind.Number);
        }
    }

    [Fact]
    public void MissingManifestStillYieldsDefault()
    {
        var ids = Ids(SleeveListBuilder.BuildJson(Path.Combine(Path.GetTempPath(), "nope-does-not-exist.txt")));
        Assert.Equal([SleeveListBuilder.DefaultSleeveId], ids);
    }
}
