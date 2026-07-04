using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVerse.Battle;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class BattleServerTests : IClassFixture<WebApplicationFactory<BattleServer>>
{
    readonly WebApplicationFactory<BattleServer> _f;
    public BattleServerTests(WebApplicationFactory<BattleServer> f) => _f = f;

    async Task<(WebSocket, string open, string connect)> ConnectAsync(string battleId, string viewerId)
    {
        var client = _f.Server.CreateWebSocketClient();
        client.ConfigureRequest = req =>
        {
            req.Headers["BattleId"] = battleId;
            req.Headers["viewerId"] = viewerId;
        };
        var ws = await client.ConnectAsync(new Uri(_f.Server.BaseAddress, "/?EIO=4&transport=websocket"), CancellationToken.None);
        var open = await ReceiveText(ws);
        var connect = await ReceiveText(ws);
        return (ws, open, connect);
    }

    static async Task<(WebSocketMessageType, byte[])> ReceiveFrame(WebSocket ws)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, CancellationToken.None);
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return (r.MessageType, ms.ToArray());
    }

    static async Task<string> ReceiveText(WebSocket ws)
    {
        var (type, data) = await ReceiveFrame(ws);
        Assert.Equal(WebSocketMessageType.Text, type);
        return Encoding.UTF8.GetString(data);
    }

    static async Task<byte[]> ReceiveBinary(WebSocket ws)
    {
        var (type, data) = await ReceiveFrame(ws);
        Assert.Equal(WebSocketMessageType.Binary, type);
        return data;
    }

    static Task SendText(WebSocket ws, string s) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, CancellationToken.None);

    static Task SendBinary(WebSocket ws, byte[] data) =>
        ws.SendAsync(data, WebSocketMessageType.Binary, true, CancellationToken.None);

    [Fact]
    public async Task ConnectReceivesOpenAndConnect()
    {
        var (_, open, connect) = await ConnectAsync("b1", "v1");
        Assert.StartsWith("0{", open);
        Assert.Contains("\"sid\":", open);
        Assert.Equal("40", connect);
    }

    [Fact]
    public async Task PingAnswersWithPong()
    {
        var (ws, _, _) = await ConnectAsync("b2", "v2");
        await SendText(ws, "2probe");
        var pong = await ReceiveText(ws);
        Assert.Equal("3probe", pong);
    }

    [Fact]
    public async Task OpenPacketAdvertisesPingInterval()
    {
        var (_, open, _) = await ConnectAsync("b3", "v3");
        Assert.Contains("\"pingInterval\":2000", open);
        Assert.Contains("\"pingTimeout\":5000", open);
    }

    [Fact]
    public async Task InitNetworkAcksAndEchoesBack()
    {
        var (ws, _, _) = await ConnectAsync("b4", "v4");
        await SendText(ws, "451-3[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        await SendBinary(ws, BattleCodec.EncodeMsg("{\"uri\":\"InitNetwork\",\"pubSeq\":7}"));

        var ack = await ReceiveText(ws);
        Assert.Equal("433[7]", ack);

        var push = await ReceiveText(ws);
        Assert.Equal("451-[\"synchronize\",{\"_placeholder\":true,\"num\":0}]", push);

        var chunk = await ReceiveBinary(ws);
        Assert.Equal(0x04, chunk[0]);
        var json = BattleCodec.DecodeMsg(chunk);
        var node = JsonNode.Parse(json);
        Assert.Equal("InitNetwork", node?["uri"]?.GetValue<string>());
        Assert.Equal(1, node?["playSeq"]?.GetValue<int>());
    }

    [Fact]
    public async Task MsgWithoutInitNetworkStillAcksButNoEcho()
    {
        var (ws, _, _) = await ConnectAsync("b5", "v5");
        await SendText(ws, "451-9[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        await SendBinary(ws, BattleCodec.EncodeMsg("{\"uri\":\"Unknown\",\"pubSeq\":42}"));

        var ack = await ReceiveText(ws);
        Assert.Equal("439[42]", ack);
    }

    [Fact]
    public async Task RoomCreateAcksWithResultCode()
    {
        var (ws, _, _) = await ConnectAsync("room-create-b", "1001");
        await SendText(ws, "451-5[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        await SendBinary(ws, BattleCodec.EncodeMsg("{\"uri\":\"RoomCreate\",\"pubSeq\":1}"));

        var ack = await ReceiveText(ws);
        Assert.Equal("435[1,{\"resultCode\":0}]", ack);
    }

    [Fact]
    public async Task RoomEntryTriggersMatchedOnBothPeers()
    {
        var (wsA, _, _) = await ConnectAsync("shared-b", "1001");
        var (wsB, _, _) = await ConnectAsync("shared-b", "1002");

        await SendText(wsA, "451-1[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        await SendBinary(wsA, BattleCodec.EncodeMsg("{\"uri\":\"RoomCreate\",\"pubSeq\":1}"));
        var ackA = await ReceiveText(wsA);
        Assert.Equal("431[1,{\"resultCode\":0}]", ackA);

        await SendText(wsB, "451-2[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        await SendBinary(wsB, BattleCodec.EncodeMsg("{\"uri\":\"RoomEntry\",\"pubSeq\":1}"));
        var ackB = await ReceiveText(wsB);
        Assert.Equal("432[1,{\"resultCode\":0}]", ackB);

        var pushA = await ReceiveText(wsA);
        Assert.StartsWith("451-", pushA);
        var chunkA = await ReceiveBinary(wsA);
        var nodeA = JsonNode.Parse(BattleCodec.DecodeMsg(chunkA));
        Assert.Equal("Matched", nodeA?["uri"]?.GetValue<string>());
        Assert.Equal("shared-b", nodeA?["bid"]?.GetValue<string>());
        Assert.Equal(1002L, nodeA?["oppoInfo"]?["viewerId"]?.GetValue<long>());
        Assert.Equal(1001L, nodeA?["selfInfo"]?["viewerId"]?.GetValue<long>());

        var pushB = await ReceiveText(wsB);
        var chunkB = await ReceiveBinary(wsB);
        var nodeB = JsonNode.Parse(BattleCodec.DecodeMsg(chunkB));
        Assert.Equal("Matched", nodeB?["uri"]?.GetValue<string>());
        Assert.Equal(1001L, nodeB?["oppoInfo"]?["viewerId"]?.GetValue<long>());
        Assert.Equal(1002L, nodeB?["selfInfo"]?["viewerId"]?.GetValue<long>());
    }

    [Fact]
    public async Task LeaveNotifiesPeer()
    {
        var (wsA, _, _) = await ConnectAsync("leave-b", "1003");
        var (wsB, _, _) = await ConnectAsync("leave-b", "1004");

        await SendText(wsB, "451-1[\"msg\",{\"_placeholder\":true,\"num\":0}]");
        await SendBinary(wsB, BattleCodec.EncodeMsg("{\"uri\":\"Leave\",\"pubSeq\":1}"));
        var ackB = await ReceiveText(wsB);
        Assert.Equal("431[1]", ackB);

        var pushA = await ReceiveText(wsA);
        var chunkA = await ReceiveBinary(wsA);
        var nodeA = JsonNode.Parse(BattleCodec.DecodeMsg(chunkA));
        Assert.Equal("Release", nodeA?["uri"]?.GetValue<string>());
    }
}
