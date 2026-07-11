using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OpenVerse.Common;

public static class CertGen
{
    public static X509Certificate2 EnsureSelfSigned(string path, string password)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=OpenVerse", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("utoongaize.shadowverse.jp");
            san.AddDnsName("shadowverse.akamaized.net");
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        }
        return X509CertificateLoader.LoadPkcs12FromFile(path, password);
    }
}
