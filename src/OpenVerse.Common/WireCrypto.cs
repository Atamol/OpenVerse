using System.Security.Cryptography;
using System.Text;

namespace OpenVerse.Common;

public static class WireCrypto
{
    public static byte[] DecryptApi(ReadOnlySpan<byte> payload, string udid)
    {
        var key = payload[^32..].ToArray();
        var body = payload[..^32].ToArray();
        return AesCbc(body, key, Iv(udid), encrypt: false);
    }

    public static byte[] EncryptApi(byte[] data, string udid, byte[] key)
    {
        var body = AesCbc(data, key, Iv(udid), encrypt: true);
        return [.. body, .. key];
    }

    public static string DecryptNode(string payload)
    {
        var key = Encoding.UTF8.GetBytes(payload[..32]);
        var body = Convert.FromBase64String(payload[32..]);
        return Encoding.UTF8.GetString(AesCbc(body, key, key[..16], encrypt: false));
    }

    public static string EncryptNode(string plain, string key)
    {
        var k = Encoding.UTF8.GetBytes(key);
        var body = AesCbc(Encoding.UTF8.GetBytes(plain), k, k[..16], encrypt: true);
        return key + Convert.ToBase64String(body);
    }

    static byte[] Iv(string udid) => Encoding.UTF8.GetBytes(udid.Replace("-", "")[..16]);

    static byte[] AesCbc(byte[] data, byte[] key, byte[] iv, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;
        using var t = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return t.TransformFinalBlock(data, 0, data.Length);
    }
}
