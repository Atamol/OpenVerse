using OpenVerse.Common;

namespace OpenVerse.Tests;

public class BattleCodecTests
{
    [Fact]
    public void RoundtripJson()
    {
        var json = "{\"uri\":\"InitNetwork\",\"viewerId\":42,\"pubSeq\":1}";
        var back = BattleCodec.DecodeMsg(BattleCodec.EncodeMsg(json));
        Assert.Equal(json, back);
    }

    [Fact]
    public void EncodedChunkStartsWithMessageMarker()
    {
        var chunk = BattleCodec.EncodeMsg("{}");
        Assert.Equal(0x04, chunk[0]);
    }

    [Fact]
    public void DecodeStripsMessageMarker()
    {
        var encoded = BattleCodec.EncodeMsg("{\"x\":1}");
        var trimmed = encoded[1..];
        Assert.Equal("{\"x\":1}", BattleCodec.DecodeMsg(trimmed));
    }
}
