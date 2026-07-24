using System.Security.Cryptography;
using System.Text.Json;
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

    // "hand" emits are plaintext MessagePack(JSON array), no AES. pubSeq sits at array index 3
    // ([uri, viewerId, udid, pubSeq, ...params]); echoing it back is what unblocks the client emit queue.
    public static int DecodeHandPubSeq(byte[] chunk)
    {
        var data = chunk.Length > 0 && chunk[0] == 0x04 ? chunk[1..] : chunk;
        var json = MessagePackSerializer.Deserialize<string>(data);
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement[3];
        return e.ValueKind == JsonValueKind.String ? int.Parse(e.GetString()!) : e.GetInt32();
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
