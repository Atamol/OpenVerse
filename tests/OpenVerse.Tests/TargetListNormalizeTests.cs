using System.Text.Json.Nodes;
using OpenVerse.Battle;

namespace OpenVerse.Tests;

// the client sends targetList but only reads isSelf out of oppoTargetList; the server renamed it per recipient
public class TargetListNormalizeTests
{
    static JsonObject Norm(string json)
    {
        var body = JsonNode.Parse(json)!.AsObject();
        BattleHub.NormalizeTargetList(body);
        return body;
    }

    // a real self-targeting play: isSelf=1 means the target is the SENDER's own follower
    const string SelfTarget = """
        {"playIdx":35,"targetList":[{"targetIdx":12,"isSelf":1,"selectSkillIndex":[1],"skillIndex":[1]}],"type":31}
        """;

    [Fact]
    public void TargetListIsRenamedToOppoTargetList()
    {
        var body = Norm(SelfTarget);
        Assert.Null(body["targetList"]);
        Assert.NotNull(body["oppoTargetList"]);
    }

    // a copy would leave both keys and let dictionary order pick the isSelf-losing parse
    [Fact]
    public void OriginalKeyIsRemovedNotCopied()
    {
        Assert.False(Norm(SelfTarget).ContainsKey("targetList"));
    }

    // this is a rename, never a viewpoint flip: isSelf must survive byte-for-byte
    [Fact]
    public void EntriesSurviveUntouched()
    {
        var e = Norm(SelfTarget)["oppoTargetList"]!.AsArray()[0]!.AsObject();
        Assert.Equal(12, e["targetIdx"]!.GetValue<int>());
        Assert.Equal(1, e["isSelf"]!.GetValue<int>());
        Assert.Equal("[1]", e["selectSkillIndex"]!.ToJsonString());
        Assert.Equal("[1]", e["skillIndex"]!.ToJsonString());
    }

    [Fact]
    public void VidIsNeverInjected()
    {
        var e = Norm(SelfTarget)["oppoTargetList"]!.AsArray()[0]!.AsObject();
        Assert.False(e.ContainsKey("vid"));  // handler is null in live play, so vid would throw in the receiver
    }

    [Fact]
    public void NoTargetListIsANoOp()
    {
        var body = Norm("""{"playIdx":3,"type":30}""");
        Assert.Null(body["oppoTargetList"]);
        Assert.Equal(3, body["playIdx"]!.GetValue<int>());
    }
}
