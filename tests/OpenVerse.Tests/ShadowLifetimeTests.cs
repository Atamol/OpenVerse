using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// a room plays many battles in one server process, and everything the shadow keeps between them is a battle answered
// off the previous one's board. these skip when the engine is absent, like the rest of the Engine collection
[Collection("Engine")]
public class ShadowLifetimeTests
{
    static string? Csv()
    {
        var gz = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "release", "data", "card_master_full.csv.gz");
        if (!File.Exists(gz)) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "OpenVerse.EngineHost.dll"))) return null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Assembly-CSharp.dll"))) return null;
        using var fs = File.OpenRead(gz);
        using var z = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(z);
        return sr.ReadToEnd();
    }

    static int[] Deck() =>
        Enumerable.Range(0, 40).Select(i => new[] { 100011010, 900011080, 100011020, 100011030 }[i % 4]).ToArray();

    // the host now lives in its own load context, so loading it again by path would hand back a different copy with its
    // own statics. go through the one the bridge actually uses. the host's helpers sit in the global namespace
    static Type Reconciler()
    {
        var host = (Type?)typeof(ShadowMirror).GetField("_host", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ShadowBridge.Primary)
                   ?? throw new InvalidOperationException("the bridge has no host");
        return host.Assembly.GetType("ShadowReconciler")
               ?? throw new InvalidOperationException("ShadowReconciler not found in the host");
    }

    // HashSet<int> carries ICollection<int>, not the non-generic one
    static int Census(Type t, string field) =>
        ((System.Collections.IEnumerable)t.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
            .Cast<object>().Count();

    static void Play(bool isPlayer) =>
        ShadowBridge.Observe("PlayActions",
            JsonNode.Parse("""{"type":30,"playIdx":1,"isSelf":1,"orderList":[{"move":{"idx":[1],"isSelf":1,"from":10,"to":30}}]}""")!.AsObject(),
            isPlayer, _ => { });

    // the census of wire-named indices used to survive Close, so from battle 2 on the reconciler refused evictions the
    // new battle needed. six games in a row is a normal evening
    [Fact]
    public void TheIndexCensusDoesNotSurviveIntoTheNextBattle()
    {
        if (Csv() is not { } csv) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);
        var rec = Reconciler();

        ShadowBridge.Begin(7, playerFirst: true, Deck(), Deck(), [1, 2, 3], [1, 2, 3], _ => { });
        Play(isPlayer: true);
        Play(isPlayer: false);
        Assert.True(ShadowBridge.WaitIdle());
        var seen = Census(rec, "SeenSelf") + Census(rec, "SeenOppo");
        Assert.True(seen > 0, "the first battle never populated the census, so this test proves nothing");

        ShadowBridge.End((_, _) => { });
        Assert.True(ShadowBridge.WaitIdle());

        ShadowBridge.Begin(9, playerFirst: false, Deck(), Deck(), [4, 5, 6], [4, 5, 6], _ => { });
        Assert.True(ShadowBridge.WaitIdle());
        Assert.Equal(0, Census(rec, "SeenSelf") + Census(rec, "SeenOppo"));

        ShadowBridge.End((_, _) => { });
        Assert.True(ShadowBridge.WaitIdle());
    }

    // Begin used to return early on a live handle, before Create, so the guard that would have logged it never ran and
    // the next battle silently kept playing on the previous board
    [Fact]
    public void ABattleLeftOpenIsEvictedAndSaidSoOutLoud()
    {
        if (Csv() is not { } csv) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);

        ShadowBridge.Begin(7, playerFirst: true, Deck(), Deck(), [1, 2, 3], [1, 2, 3], _ => { });
        Assert.True(ShadowBridge.WaitIdle());

        var lines = new List<string>();
        ShadowBridge.Begin(9, playerFirst: false, Deck(), Deck(), [4, 5, 6], [4, 5, 6], lines.Add);
        Assert.True(ShadowBridge.WaitIdle());

        Assert.Contains(lines, l => l.Contains("never closed"));
        Assert.Contains(lines, l => l.Contains("observing"));

        ShadowBridge.End((_, _) => { });
        Assert.True(ShadowBridge.WaitIdle());
    }
}
