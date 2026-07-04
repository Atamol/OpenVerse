using System.Security.Cryptography;
using MessagePack;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenVerse.Common;

namespace OpenVerse.Tests;

public class ReceiverTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Udid = "0123456789abcdef0123456789abcdef";
    readonly WebApplicationFactory<Program> _factory;

    public ReceiverTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task GameStartReturnsClientReadableSuccess()
    {
        var client = _factory.CreateClient();

        var reqJson = "{\"viewer_id\":1001,\"steam_id\":42}";
        var body = WireCrypto.EncryptApi(MessagePackSerializer.ConvertFromJson(reqJson), Udid, RandomNumberGenerator.GetBytes(32));

        var msg = new HttpRequestMessage(HttpMethod.Post, "/shadowverse/check/game_start");
        msg.Headers.Add("udid", Udid);
        msg.Content = new ByteArrayContent(body);

        var res = await client.SendAsync(msg);
        res.EnsureSuccessStatusCode();

        var text = await res.Content.ReadAsStringAsync();
        var back = MessagePackSerializer.ConvertToJson(WireCrypto.DecryptApi(Convert.FromBase64String(text), Udid));

        Assert.Contains("result_code", back);
        Assert.Contains("tos_state", back);
    }
}
