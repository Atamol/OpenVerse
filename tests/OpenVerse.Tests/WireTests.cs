using System.Security.Cryptography;
using System.Text;
using MessagePack;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class WireTests
{
    const string Udid = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ApiRoundTrip()
    {
        var data = Encoding.UTF8.GetBytes("hello api");
        var key = RandomNumberGenerator.GetBytes(32);
        var enc = WireCrypto.EncryptApi(data, Udid, key);
        Assert.Equal(data, WireCrypto.DecryptApi(enc, Udid));
    }

    [Fact]
    public void NodeRoundTrip()
    {
        const string key = "0123456789abcdef0123456789abcdef";
        var enc = WireCrypto.EncryptNode("hello node", key);
        Assert.Equal("hello node", WireCrypto.DecryptNode(enc));
    }

    [Fact]
    public void RequestDecodesToJson()
    {
        var json = "{\"viewer_id\":1001,\"steam_id\":42}";
        var body = WireCrypto.EncryptApi(MessagePackSerializer.ConvertFromJson(json), Udid, RandomNumberGenerator.GetBytes(32));
        var decoded = WireCodec.DecodeRequest(body, Udid);
        Assert.Contains("viewer_id", decoded);
        Assert.Contains("1001", decoded);
    }

    [Fact]
    public void ResponseIsReadableByClient()
    {
        var json = "{\"data_headers\":{\"result_code\":1,\"servertime\":123},\"data\":{}}";
        var enc = Convert.FromBase64String(WireCodec.EncodeResponse(json, Udid));
        var back = MessagePackSerializer.ConvertToJson(WireCrypto.DecryptApi(enc, Udid));
        Assert.Contains("result_code", back);
    }
}
