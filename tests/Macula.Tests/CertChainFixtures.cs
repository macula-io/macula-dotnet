using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Macula.Tests;

/// <summary>
/// Test certificates for cert-chain authorization: a realm CA, a leaf it
/// issues for an advertiser key and org, and a leaf-first PEM bundle.
/// Shared by <see cref="CertChainTests"/> and
/// <see cref="DirectDialCandidatesTests"/>; mirrors macula-go's own
/// dht/cert_chain_test.go fixtures.
/// </summary>
internal static class CertChainFixtures
{
    internal static (byte[] Pem, X509Certificate Cert, AsymmetricKeyParameter Priv) TestCa()
    {
        var kpGen = new Ed25519KeyPairGenerator();
        kpGen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var kp = kpGen.GenerateKeyPair();

        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.One);
        var subject = new Org.BouncyCastle.Asn1.X509.X509Name("CN=Test Realm CA, O=Test Realm CA");
        gen.SetIssuerDN(subject);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(DateTime.UtcNow.AddHours(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddHours(24));
        gen.SetPublicKey(kp.Public);
        gen.AddExtension(Org.BouncyCastle.Asn1.X509.X509Extensions.BasicConstraints, true, new Org.BouncyCastle.Asn1.X509.BasicConstraints(true));

        var signatureFactory = new Asn1SignatureFactory("Ed25519", kp.Private);
        var cert = gen.Generate(signatureFactory);
        var pem = System.Text.Encoding.ASCII.GetBytes(
            "-----BEGIN CERTIFICATE-----\n" +
            Convert.ToBase64String(cert.GetEncoded(), Base64FormattingOptions.InsertLineBreaks) +
            "\n-----END CERTIFICATE-----\n");
        return (pem, cert, kp.Private);
    }

    internal static byte[] TestLeaf(X509Certificate ca, AsymmetricKeyParameter caPriv, byte[] advertiserPub, string org, DateTime notAfter)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.Two);
        gen.SetIssuerDN(ca.SubjectDN);
        gen.SetSubjectDN(new Org.BouncyCastle.Asn1.X509.X509Name($"CN=test-service, O={org}"));
        gen.SetNotBefore(DateTime.UtcNow.AddHours(-1));
        gen.SetNotAfter(notAfter);
        var pub = new Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters(advertiserPub, 0);
        gen.SetPublicKey(pub);
        var signatureFactory = new Asn1SignatureFactory("Ed25519", caPriv);
        var cert = gen.Generate(signatureFactory);
        return cert.GetEncoded();
    }

    internal static byte[] PemBundle(params byte[][] ders)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var der in ders)
        {
            sb.Append("-----BEGIN CERTIFICATE-----\n");
            sb.Append(Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks));
            sb.Append("\n-----END CERTIFICATE-----\n");
        }
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
