using System.IO.Compression;
using System.Text.Json.Nodes;
using OpenVerse.Engine;

namespace OpenVerse.Tests;

// the point of these is the net10 -> net48 hop: the server cannot reference the engine host, so everything goes
// through reflection and a compile proves nothing about it. they skip rather than fail when the engine is absent,
// since it is built per host and stays out of the repo.
// each test ends its match before returning: the shadow is a process-global singleton (one match at a time), so a
// left-open match would make the next test's Begin a no-op and read stale state
[Collection("Engine")]
public class ShadowBridgeTests
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

    [Fact]
    public void ReachesTheNet48HostAndStartsAMatch()
    {
        if (Csv() is not { } csv) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);

        var lines = new List<string>();
        ShadowBridge.Begin(7, playerFirst: true, Deck(), Deck(), new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, lines.Add);
        Assert.True(ShadowBridge.WaitIdle(), "the shadow worker never caught up");

        Assert.True(lines.Any(l => l.Contains("observing")), string.Join(" | ", lines));
        ShadowBridge.End((_, _) => { });
        Assert.True(ShadowBridge.WaitIdle());
    }

    // the shadow sits beside live matches, so a message it cannot make sense of has to cost an observation and nothing
    // else. garbage is the case that matters: the engine's receive path is known to throw out of ReceivedMessage on
    // short lists and unguarded First() calls
    [Fact]
    public void SwallowsMessagesItCannotMakeSenseOf()
    {
        if (Csv() is not { } csv) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);
        ShadowBridge.Begin(7, playerFirst: true, Deck(), Deck(), new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, _ => { });
        Assert.True(ShadowBridge.WaitIdle());

        var junk = new[]
        {
            JsonNode.Parse("""{"type":30,"playIdx":1,"isSelf":1}""")!.AsObject(),
            JsonNode.Parse("""{"type":21,"playIdx":1,"isSelf":1,"targetList":[]}""")!.AsObject(),   // short target list
            JsonNode.Parse("""{"type":"not a number","playIdx":null}""")!.AsObject(),
            JsonNode.Parse("""{}""")!.AsObject(),
            JsonNode.Parse("""{"type":999999,"playIdx":-1,"isSelf":7,"targetList":[{"vid":"x"}]}""")!.AsObject(),
        };
        foreach (var j in junk)
        {
            ShadowBridge.Observe("PlayActions", j, isPlayer: true, _ => { });
            ShadowBridge.Observe("NotAUriAtAll", j, isPlayer: false, _ => { });
        }
        Assert.True(ShadowBridge.WaitIdle(), "the worker died on a message it could not parse");

        // still alive and still readable afterwards: the point is that nothing was taken down
        string state = "";
        ShadowBridge.End((_, s) => state = s);
        Assert.True(ShadowBridge.WaitIdle());
        Assert.Contains("life=", state);
    }

    // a board the engine can describe is a board it can adjudicate; an empty or garbled line means the hop half-worked
    [Fact]
    public void ReportsABoardItCanRead()
    {
        if (Csv() is not { } csv) return;
        Assert.True(ShadowBridge.Init(csv), ShadowBridge.Failure);

        ShadowBridge.Begin(7, playerFirst: true, Deck(), Deck(), new[] { 1, 2, 3 }, new[] { 1, 2, 3 }, _ => { });
        Assert.True(ShadowBridge.WaitIdle());

        int verdict = -1;
        string state = "";
        ShadowBridge.End((v, s) => { verdict = v; state = s; });
        Assert.True(ShadowBridge.WaitIdle());

        Assert.Contains("life=20", state);
        // the opening hand was dealt (3 indices asked for): without it the shadow's board never leaves the deck and it
        // silently fails to track the whole match, which is the bug this guards
        Assert.Contains("hand=3", state);
        Assert.Contains("deck=37", state);
        // NotFinish: nobody has won a match that has not been played
        Assert.Equal(0, verdict);
    }
}
