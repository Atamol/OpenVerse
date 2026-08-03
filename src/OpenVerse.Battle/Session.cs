using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenVerse.Common;

namespace OpenVerse.Battle;

public sealed class Session
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string BattleId { get; }
    public string ViewerId { get; }
    public string RemoteIp { get; init; } = "";
    public int PingIntervalMs { get; init; } = 2000;
    public int PingTimeoutMs { get; init; } = 5000;
    public bool Loaded { get; set; }
    public bool MulliganDone { get; set; }
    public bool Ready { get; set; }
    public bool InitBattleSent { get; set; }
    public bool MatchedSent { get; set; }
    // a pulled cable does not close a TCP socket, so PeerClosed can be minutes late or never. this is the only evidence
    // the relay has that a peer is still there
    public DateTimeOffset LastHeard { get; set; } = DateTimeOffset.UtcNow;
    public bool BattleStartSent { get; set; }
    public bool DealSent { get; set; }
    // deck cards are Indexed 1..40 (client uses slot+1), so the opening hand is 1-3 and redraws pull slot 4 and up
    public int NextDeckIdx { get; set; } = 4;
    // mulligan redraws: hand position -> replacement deck slot
    public Dictionary<int, int> Redraws { get; } = new();

    public static readonly bool Chatty =
        Environment.GetEnvironmentVariable("OPENVERSE_LOG_FRAMES")?.Trim().ToLowerInvariant() is "1" or "true" or "on";

    static bool Keepalive(string text) =>
        text.StartsWith('2') || text.StartsWith('3') || text.Contains("\"alive\"", StringComparison.Ordinal);

    readonly WebSocket _ws;
    readonly Channel<Frame> _outgoing = Channel.CreateUnbounded<Frame>();
    int _playSeq;

    public event Action<Session, SocketPacket, byte[][]>? OnEvent;
    public event Action<Session, string, JsonNode?, int?>? OnMsg;
    public event Action<Session>? OnAliveEmit;

    readonly record struct Frame(WebSocketMessageType Type, byte[] Data);

    public Session(WebSocket ws, string battleId, string viewerId)
    {
        _ws = ws;
        BattleId = battleId;
        ViewerId = viewerId;
    }

    public async Task Run(CancellationToken ct)
    {
        await SendText($"0{{\"sid\":\"{Id}\",\"upgrades\":[],\"pingInterval\":{PingIntervalMs},\"pingTimeout\":{PingTimeoutMs},\"maxPayload\":1000000}}", ct);
        await SendText("40", ct);

        var writer = WriteLoop(ct);
        try { await ReadLoop(ct); }
        finally { _outgoing.Writer.Complete(); await writer; }
    }

    async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[65536];
        var pending = new List<byte[]>();
        int expected = 0;
        SocketPacket? pendingPacket = null;

        while (_ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            } while (!r.EndOfMessage);

            var frame = ms.ToArray();
            if (r.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(frame);
                if (Chatty || !Keepalive(text)) Console.WriteLine($"[{Id}] << {text}");
                var ep = EnginePacket.ParseText(text);
                switch (ep.Type)
                {
                    case EngineType.Ping:
                        await SendText("3" + ep.Text, ct);
                        break;
                    case EngineType.Message:
                        var sp = SocketPacket.ParseText(ep.Text);
                        if (sp.Attachments == 0)
                            Dispatch(sp, Array.Empty<byte[]>());
                        else
                        {
                            pendingPacket = sp;
                            expected = sp.Attachments;
                            pending.Clear();
                        }
                        break;
                }
            }
            else
            {
                if (Chatty) Console.WriteLine($"[{Id}] << <binary {frame.Length}B>");
                pending.Add(frame);
                if (pendingPacket is not null && pending.Count == expected)
                {
                    Dispatch(pendingPacket, pending.ToArray());
                    pendingPacket = null;
                    pending.Clear();
                }
            }
        }
    }

    void Dispatch(SocketPacket sp, byte[][] binaries)
    {
        // every inbound frame, not just the ones the hub routes: the Gungnir heartbeat leaves here through OnAliveEmit
        // and never reaches the hub's dispatch, so counting only those had a live peer look silent within 20s
        LastHeard = DateTimeOffset.UtcNow;
        OnEvent?.Invoke(this, sp, binaries);
        if (sp.EventName == "alive")
        {
            if (sp.AckId is int aliveAckId) _ = SendAck(aliveAckId, 0);
            OnAliveEmit?.Invoke(this);
            return;
        }
        if (sp.EventName == "hand")
        {
            if (sp.AckId is int handAckId && binaries.Length > 0)
            {
                try { _ = SendAck(handAckId, BattleCodec.DecodeHandPubSeq(binaries[0])); }
                catch (Exception e) { Console.WriteLine($"[{Id}] hand decode failed: {e.Message}"); }
            }
            return;
        }
        if (sp.EventName == "msg" && binaries.Length > 0)
        {
            try
            {
                var json = BattleCodec.DecodeMsg(binaries[0]);
                var node = JsonNode.Parse(json);
                var uri = node?["uri"]?.GetValue<string>() ?? "";
                OnMsg?.Invoke(this, uri, node, sp.AckId);
            }
            catch (Exception e) { Console.WriteLine($"[{Id}] msg decode failed: {e.Message}"); }
        }
    }

    async Task WriteLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var f in _outgoing.Reader.ReadAllAsync(ct))
                await _ws.SendAsync(f.Data, f.Type, true, ct);
        }
        catch (OperationCanceledException) { }
    }

    Task SendText(string s, CancellationToken ct)
    {
        if (Chatty || !Keepalive(s)) Console.WriteLine($"[{Id}] >> {s}");
        return _outgoing.Writer.WriteAsync(new Frame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(s)), ct).AsTask();
    }

    Task SendBinary(byte[] data, CancellationToken ct)
    {
        if (Chatty) Console.WriteLine($"[{Id}] >> <binary {data.Length}B>");
        return _outgoing.Writer.WriteAsync(new Frame(WebSocketMessageType.Binary, data), ct).AsTask();
    }

    public async Task SendMsg(string uri, object payload, bool withPlaySeq = true, CancellationToken ct = default)
    {
        var envelope = new JsonObject { ["uri"] = uri };
        var body = JsonNode.Parse(JsonSerializer.Serialize(payload)) as JsonObject;
        if (body is not null) foreach (var (k, v) in body) if (k != "uri") envelope[k] = v?.DeepClone();
        // non-matching URIs (RoomCreate/RoomEntry/etc.) get stocked and never reach OnReceivedEvent when playSeq is set,
        // because IsMatchingURI only bypasses stock for InitBattle/InitRoomBattle/Matched/InitNetwork
        if (withPlaySeq) envelope["playSeq"] = Interlocked.Increment(ref _playSeq);
        Console.WriteLine($"[{Id}] send {uri}: {envelope.ToJsonString()}");
        var chunk = BattleCodec.EncodeMsg(envelope.ToJsonString());
        var packet = new SocketPacket
        {
            Type = SocketType.BinaryEvent,
            Attachments = 1,
            Payload = "[\"synchronize\",{\"_placeholder\":true,\"num\":0}]",
        };
        await SendText("4" + packet.Serialize(), ct);
        await SendBinary(chunk, ct);
    }

    public Task SendAck(int ackId, int pubSeq, CancellationToken ct = default)
    {
        var packet = SocketPacket.Ack(ackId, [pubSeq]);
        return SendText("4" + packet.Serialize(), ct);
    }

    public Task SendAckResult(int ackId, int pubSeq, object result, CancellationToken ct = default)
    {
        var packet = SocketPacket.Ack(ackId, [pubSeq, result]);
        return SendText("4" + packet.Serialize(), ct);
    }

    public async Task SendAlive(bool peerOnline, bool peerGone = false, CancellationToken ct = default)
    {
        var envelope = new JsonObject
        {
            ["uri"] = "Gungnir",
            // scs must stay ONLINE: the client reads scs first and an OFFLINE there means "I am disconnected",
            // returning before it ever looks at ocs
            ["scs"] = "ONLINE",
            ["ocs"] = peerGone ? "OFFLINE" : peerOnline ? "ONLINE" : "WAITING",
        };
        var chunk = BattleCodec.EncodeMsg(envelope.ToJsonString());
        var packet = new SocketPacket
        {
            Type = SocketType.BinaryEvent,
            Attachments = 1,
            Payload = "[\"alive\",{\"_placeholder\":true,\"num\":0}]",
        };
        await SendText("4" + packet.Serialize(), ct);
        await SendBinary(chunk, ct);
    }
}
