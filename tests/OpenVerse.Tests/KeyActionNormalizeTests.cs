using System.Text.Json.Nodes;
using OpenVerse.Battle;

namespace OpenVerse.Tests;

// the sender nests selectCard, the receiver wants it flat. these lock in the shape the peer's ConvertToListInt accepts
public class KeyActionNormalizeTests
{
    static JsonObject Norm(string json)
    {
        var body = JsonNode.Parse(json)!.AsObject();
        BattleHub.NormalizeKeyAction(body);
        return body;
    }

    // the exact payload captured from the stalled match (playSeq 18)
    const string Captured = """
        {"playIdx":30,"keyAction":[{"type":2,"cardId":120031020},{"type":1,"cardId":800034040,"selectCard":{"cardId":[109034010],"open":0}}],"type":30}
        """;

    [Fact]
    public void ChoiceSelectCardBecomesABareArray()
    {
        var e = Norm(Captured)["keyAction"]!.AsArray()[1]!.AsObject();
        Assert.Equal("[109034010]", e["selectCard"]!.ToJsonString());
    }

    [Fact]
    public void OpenIsDroppedWithTheWrapper()
    {
        var e = Norm(Captured)["keyAction"]!.AsArray()[1]!.AsObject();
        Assert.False(e["selectCard"] is JsonObject);
        Assert.Null(e["open"]);
    }

    // entry order feeds transformBeforeCardId: Accelerated must still precede Choice
    [Fact]
    public void EntryOrderAndOtherFieldsSurvive()
    {
        var ka = Norm(Captured)["keyAction"]!.AsArray();
        Assert.Equal(2, ka[0]!["type"]!.GetValue<int>());
        Assert.Equal(120031020, ka[0]!["cardId"]!.GetValue<int>());
        Assert.Equal(1, ka[1]!["type"]!.GetValue<int>());
        Assert.Equal(800034040, ka[1]!["cardId"]!.GetValue<int>());
    }

    // BurialRate: the sender buries cardIdx inside selectCard, the receiver reads it off the entry
    [Fact]
    public void BurialRateCardIdxIsHoistedOntoTheEntry()
    {
        var body = Norm("""{"keyAction":[{"type":6,"selectCard":{"cardIdx":[7,9],"open":1}}]}""");
        var e = body["keyAction"]!.AsArray()[0]!.AsObject();
        Assert.Equal("[7,9]", e["cardIdx"]!.ToJsonString());
        Assert.Null(e["selectCard"]);
    }

    [Fact]
    public void AlreadyFlatIsLeftAlone()
    {
        var body = Norm("""{"keyAction":[{"type":1,"selectCard":[5]}]}""");
        Assert.Equal("[5]", body["keyAction"]!.AsArray()[0]!["selectCard"]!.ToJsonString());
    }

    [Fact]
    public void NoKeyActionIsANoOp()
    {
        Assert.Null(Norm("""{"playIdx":3,"type":30}""")["keyAction"]);
    }
}
