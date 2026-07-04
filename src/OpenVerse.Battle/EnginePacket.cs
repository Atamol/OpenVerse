using System.Text;

namespace OpenVerse.Battle;

public enum EngineType : byte
{
    Open = (byte)'0',
    Close = (byte)'1',
    Ping = (byte)'2',
    Pong = (byte)'3',
    Message = (byte)'4',
    Upgrade = (byte)'5',
    Noop = (byte)'6',
}

public readonly record struct EnginePacket(EngineType Type, string Text, byte[]? Binary = null)
{
    public static EnginePacket ParseText(string frame)
    {
        if (frame.Length == 0) throw new ArgumentException("empty engine frame");
        var t = (EngineType)(byte)frame[0];
        return new EnginePacket(t, frame.Length > 1 ? frame[1..] : "");
    }

    public string Serialize() => ((char)Type) + Text;
    public byte[] SerializeBytes() => Encoding.UTF8.GetBytes(Serialize());
}
