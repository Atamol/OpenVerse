using System.Security.Cryptography;
using MessagePack;

namespace OpenVerse.Common;

public static class WireCodec
{
    public static string DecodeRequest(ReadOnlySpan<byte> body, string udid)
    {
        var msgpack = WireCrypto.DecryptApi(body, udid);
        return MessagePackSerializer.ConvertToJson(msgpack);
    }

    public static string EncodeResponse(string json, string udid)
    {
        var msgpack = MessagePackSerializer.ConvertFromJson(json);
        var key = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(WireCrypto.EncryptApi(msgpack, udid, key));
    }
}
