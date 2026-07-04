using System.Text;
using System.Text.Json;

namespace OpenVerse.Battle;

public enum SocketType : byte
{
    Connect = (byte)'0',
    Disconnect = (byte)'1',
    Event = (byte)'2',
    Ack = (byte)'3',
    ConnectError = (byte)'4',
    BinaryEvent = (byte)'5',
    BinaryAck = (byte)'6',
}

public sealed class SocketPacket
{
    public SocketType Type { get; init; }
    public int Attachments { get; init; }
    public string Namespace { get; init; } = "/";
    public int? AckId { get; init; }
    public string? Payload { get; init; }

    public string? EventName => TryEventName();

    string? TryEventName()
    {
        if (Payload is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(Payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            var first = doc.RootElement[0];
            return first.ValueKind == JsonValueKind.String ? first.GetString() : null;
        }
        catch { return null; }
    }

    public static SocketPacket ParseText(string frame)
    {
        if (frame.Length == 0) throw new ArgumentException("empty socket frame");
        var type = (SocketType)(byte)frame[0];
        int i = 1;
        int attachments = 0;
        if (type is SocketType.BinaryEvent or SocketType.BinaryAck)
        {
            int dash = frame.IndexOf('-', i);
            if (dash < 0) throw new FormatException("binary packet missing '-'");
            attachments = int.Parse(frame.AsSpan(i, dash - i));
            i = dash + 1;
        }
        string ns = "/";
        if (i < frame.Length && frame[i] == '/')
        {
            int comma = frame.IndexOf(',', i);
            if (comma < 0) { ns = frame[i..]; i = frame.Length; }
            else { ns = frame[i..comma]; i = comma + 1; }
        }
        int? ackId = null;
        int digitStart = i;
        while (i < frame.Length && frame[i] >= '0' && frame[i] <= '9') i++;
        if (i > digitStart) ackId = int.Parse(frame.AsSpan(digitStart, i - digitStart));
        var payload = i < frame.Length ? frame[i..] : null;
        return new SocketPacket { Type = type, Attachments = attachments, Namespace = ns, AckId = ackId, Payload = payload };
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append((char)Type);
        if (Attachments > 0) sb.Append(Attachments).Append('-');
        if (Namespace != "/") sb.Append(Namespace).Append(',');
        if (AckId is int id) sb.Append(id);
        if (Payload is not null) sb.Append(Payload);
        return sb.ToString();
    }

    public static SocketPacket Event(string name, object[] args, int? ackId = null)
    {
        var arr = new object?[args.Length + 1];
        arr[0] = name;
        Array.Copy(args, 0, arr, 1, args.Length);
        return new SocketPacket { Type = SocketType.Event, AckId = ackId, Payload = JsonSerializer.Serialize(arr) };
    }

    public static SocketPacket Ack(int ackId, object[] args) => new()
    {
        Type = SocketType.Ack,
        AckId = ackId,
        Payload = JsonSerializer.Serialize(args),
    };
}
