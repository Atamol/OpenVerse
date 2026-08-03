using System.Text.Json.Nodes;
using OpenVerse.Battle;
using Xunit.Abstractions;

namespace OpenVerse.Tests;

// the capture is a real match relayed by OpenVerse, and it desyncs partway through without anyone noticing at the time.
// the comparator has to find that, so the assertion is not "clean" but "clean until boundary 9"
public class ConsistencyWatchTests
{
    readonly ITestOutputHelper _out;
    public ConsistencyWatchTests(ITestOutputHelper o) => _out = o;

    const string Battle = "b1";

    static JsonObject Code(string a, string b, string c, string d, string e, string f) => new()
    {
        ["battleCode"] = new JsonObject
        {
            ["key1"] = a, ["key2"] = b, ["key3"] = c,
            ["key4"] = d, ["key5"] = e, ["key6"] = f,
        },
    };

    [Fact]
    public void MirroredCodesAgree()
    {
        var w = new ConsistencyWatch();
        w.NoteTurnEnd(Battle, "A", Code("140", "56", "0", "143", "17", "0"));
        Assert.Null(w.NoteJudge(Battle, "B", Code("143", "17", "0", "140", "56", "0")));
        Assert.Equal(1, w.Boundaries(Battle));
    }

    [Fact]
    public void NamesTheThirdThatBroke()
    {
        var w = new ConsistencyWatch();
        w.NoteTurnEnd(Battle, "A", Code("140", "56", "0", "143", "17", "0"));
        var div = w.NoteJudge(Battle, "B", Code("143", "17", "999", "140", "56", "0"));

        Assert.NotNull(div);
        Assert.Equal([CodePart.InPlay], div!.Parts);
    }

    [Fact]
    public void ReportsEveryBrokenThird()
    {
        var w = new ConsistencyWatch();
        w.NoteTurnEnd(Battle, "A", Code("140", "56", "0", "143", "17", "0"));
        var div = w.NoteJudge(Battle, "B", Code("1", "17", "999", "140", "56", "0"));

        Assert.Equal([CodePart.Totals, CodePart.InPlay], div!.Parts);
    }

    // a client repeats Judge; only the one answering the peer's TurnEnd is a boundary
    [Fact]
    public void IgnoresAJudgeFromTheSameSideAsTheTurnEnd()
    {
        var w = new ConsistencyWatch();
        w.NoteTurnEnd(Battle, "A", Code("140", "56", "0", "143", "17", "0"));
        Assert.Null(w.NoteJudge(Battle, "A", Code("9", "9", "9", "9", "9", "9")));
        Assert.Equal(0, w.Boundaries(Battle));
    }

    // the point of the window: the break localises to the messages since the last agreement, not to a turn number
    [Fact]
    public void CarriesWhatWasRelayedSinceTheLastAgreement()
    {
        var w = new ConsistencyWatch();
        w.NoteRelayed(Battle, "A PlayActions idx=2 card=101334020");
        w.NoteTurnEnd(Battle, "A", Code("140", "56", "0", "143", "17", "0"));
        Assert.Null(w.NoteJudge(Battle, "B", Code("143", "17", "0", "140", "56", "0")));

        w.NoteRelayed(Battle, "B PlayActions idx=7 card=900000000");
        w.NoteRelayed(Battle, "B TurnEnd");
        w.NoteTurnEnd(Battle, "B", Code("140", "56", "0", "143", "17", "0"));
        var div = w.NoteJudge(Battle, "A", Code("143", "17", "999", "140", "56", "0"));

        // the agreed boundary cleared the window, so only the suspect half of the match is carried
        Assert.Equal(["B PlayActions idx=7 card=900000000", "B TurnEnd"], div!.Window);
    }

    // every later break is downstream of the first, so only the first one is worth a window
    [Fact]
    public void CarriesTheWindowOnlyOnTheFirstBreak()
    {
        var w = new ConsistencyWatch();
        w.NoteRelayed(Battle, "A PlayActions");
        w.NoteTurnEnd(Battle, "A", Code("1", "1", "1", "1", "1", "1"));
        Assert.NotEmpty(w.NoteJudge(Battle, "B", Code("1", "1", "9", "1", "1", "1"))!.Window);

        w.NoteRelayed(Battle, "B PlayActions");
        w.NoteTurnEnd(Battle, "B", Code("1", "1", "1", "1", "1", "1"));
        Assert.Empty(w.NoteJudge(Battle, "A", Code("1", "1", "9", "1", "1", "1"))!.Window);
    }

    // one break can be a straggler, a run of them is a match that is not repairing itself
    [Fact]
    public void CountsConsecutiveBreaksAndForgetsThemOnAgreement()
    {
        var w = new ConsistencyWatch();
        var bad = () => { w.NoteTurnEnd(Battle, "A", Code("1", "1", "1", "1", "1", "1")); return w.NoteJudge(Battle, "B", Code("1", "1", "9", "1", "1", "1")); };
        var good = () => { w.NoteTurnEnd(Battle, "A", Code("1", "1", "1", "1", "1", "1")); return w.NoteJudge(Battle, "B", Code("1", "1", "1", "1", "1", "1")); };

        Assert.Equal(1, bad()!.Consecutive);
        Assert.Equal(2, bad()!.Consecutive);
        Assert.Null(good());
        Assert.Equal(1, bad()!.Consecutive);
    }

    [Fact]
    public void ForgetsTheBattleOnReset()
    {
        var w = new ConsistencyWatch();
        w.NoteTurnEnd(Battle, "A", Code("140", "56", "0", "143", "17", "0"));
        w.Reset(Battle);
        Assert.Null(w.NoteJudge(Battle, "B", Code("1", "1", "1", "1", "1", "1")));
        Assert.Equal(0, w.Boundaries(Battle));
    }

    // TurnEnd is relayed so its author is the peer of the session it was sent to, while Judge is authored by that session
    // (the same attribution ShadowCostFidelityTests uses)
    [Fact]
    public void FindsTheDesyncInTheShippedCapture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "assets", "battle_capture.tsv");
        Assert.True(File.Exists(path), path);

        var w = new ConsistencyWatch();
        var clean = new List<int>();
        var broke = new List<Divergence>();

        foreach (var line in File.ReadLines(path))
        {
            var col = line.Split('\t');
            if (col.Length < 3) continue;
            var body = JsonNode.Parse(col[2]);
            if (body?["battleCode"] is null) continue;
            switch (col[1])
            {
                case "TurnEnd":
                case "TurnEndFinal":
                    w.NoteTurnEnd(Battle, $"peer-of-{col[0]}", body);
                    break;
                case "Judge":
                    var before = w.Boundaries(Battle);
                    var div = w.NoteJudge(Battle, col[0], body);
                    if (w.Boundaries(Battle) == before) break;
                    if (div is null) clean.Add(w.Boundaries(Battle)); else broke.Add(div);
                    break;
            }
        }

        foreach (var d in broke) _out.WriteLine(d.ToString());

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], clean);
        Assert.Equal([9, 10, 11], broke.Select(d => d.Boundary));
        // the first break is the peer's board vanishing from the actor's view, which is "the card they played never showed up"
        Assert.Contains(CodePart.InPlay, broke[0].Parts);
    }
}
