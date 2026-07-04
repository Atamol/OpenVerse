using OpenVerse.Battle;

namespace OpenVerse.Tests;

public class BattlePacketTests
{
    [Fact]
    public void EnginePacketParsesOpen()
    {
        var p = EnginePacket.ParseText("0{\"sid\":\"abc\"}");
        Assert.Equal(EngineType.Open, p.Type);
        Assert.Equal("{\"sid\":\"abc\"}", p.Text);
    }

    [Fact]
    public void EnginePacketRoundtrips()
    {
        var original = new EnginePacket(EngineType.Message, "42[\"hi\"]");
        var parsed = EnginePacket.ParseText(original.Serialize());
        Assert.Equal(original.Type, parsed.Type);
        Assert.Equal(original.Text, parsed.Text);
    }

    [Fact]
    public void SocketPacketParsesEvent()
    {
        var p = SocketPacket.ParseText("2[\"msg\",\"hello\"]");
        Assert.Equal(SocketType.Event, p.Type);
        Assert.Null(p.AckId);
        Assert.Equal(0, p.Attachments);
        Assert.Equal("msg", p.EventName);
    }

    [Fact]
    public void SocketPacketParsesEventWithAck()
    {
        var p = SocketPacket.ParseText("221[\"msg\",\"hello\"]");
        Assert.Equal(SocketType.Event, p.Type);
        Assert.Equal(21, p.AckId);
        Assert.Equal("msg", p.EventName);
    }

    [Fact]
    public void SocketPacketParsesBinaryEvent()
    {
        var p = SocketPacket.ParseText("52-3[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        Assert.Equal(SocketType.BinaryEvent, p.Type);
        Assert.Equal(2, p.Attachments);
        Assert.Equal(3, p.AckId);
        Assert.Equal("msg", p.EventName);
    }

    [Fact]
    public void SocketPacketParsesNamespace()
    {
        var p = SocketPacket.ParseText("2/chat,[\"hi\"]");
        Assert.Equal("/chat", p.Namespace);
        Assert.Null(p.AckId);
    }

    [Fact]
    public void SocketPacketEventFactory()
    {
        var p = SocketPacket.Event("msg", ["hello"]);
        Assert.Equal(SocketType.Event, p.Type);
        Assert.Equal("[\"msg\",\"hello\"]", p.Payload);
    }

    [Fact]
    public void SocketPacketAckFactory()
    {
        var p = SocketPacket.Ack(42, [1]);
        Assert.Equal(SocketType.Ack, p.Type);
        Assert.Equal(42, p.AckId);
        Assert.Equal("[1]", p.Payload);
    }

    [Fact]
    public void SocketPacketSerializesEvent()
    {
        var s = new SocketPacket { Type = SocketType.Event, Payload = "[\"msg\"]" }.Serialize();
        Assert.Equal("2[\"msg\"]", s);
    }

    [Fact]
    public void SocketPacketSerializesBinaryEventWithAckAndNamespace()
    {
        var s = new SocketPacket { Type = SocketType.BinaryEvent, Attachments = 1, Namespace = "/foo", AckId = 3, Payload = "[\"msg\"]" }.Serialize();
        Assert.Equal("51-/foo,3[\"msg\"]", s);
    }
}
