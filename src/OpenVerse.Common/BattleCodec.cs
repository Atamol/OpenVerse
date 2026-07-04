using System.Security.Cryptography;
using MessagePack;

namespace OpenVerse.Common;

public static class BattleCodec
{
    public static string DecodeMsg(byte[] chunk)
    {
        var data = chunk.Length > 0 && chunk[0] == 0x04 ? chunk[1..] : chunk;
        var encrypted = MessagePackSerializer.Deserialize<string>(data);
        return WireCrypto.DecryptNode(encrypted);
    }

    public static byte[] EncodeMsg(string json)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var encrypted = WireCrypto.EncryptNode(json, key);
        var packed = MessagePackSerializer.Serialize(encrypted);
        var result = new byte[packed.Length + 1];
        result[0] = 0x04;
        packed.CopyTo(result, 1);
        return result;
    }
}
