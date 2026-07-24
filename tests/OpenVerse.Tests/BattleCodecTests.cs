using MessagePack;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class BattleCodecTests
{
    static byte[] PackHand(string json, bool withMarker = true)
    {
        var packed = MessagePackSerializer.Serialize(json);
        if (!withMarker) return packed;
        var result = new byte[packed.Length + 1];
        result[0] = 0x04;
        packed.CopyTo(result, 1);
        return result;
    }

    [Fact]
    public void DecodeHandPubSeqReadsIndex3()
    {
        Assert.Equal(7, BattleCodec.DecodeHandPubSeq(PackHand("[2,123,\"udid\",7,10,20]")));
    }

    [Fact]
    public void DecodeHandPubSeqWithoutMarker()
    {
        Assert.Equal(7, BattleCodec.DecodeHandPubSeq(PackHand("[5,123,\"udid\",7,10,20]", withMarker: false)));
    }

    [Fact]
    public void DecodeHandPubSeqAcceptsQuotedNumber()
    {
        Assert.Equal(7, BattleCodec.DecodeHandPubSeq(PackHand("[2,123,\"udid\",\"7\",10,20]")));
    }

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
