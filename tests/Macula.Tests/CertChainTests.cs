using Macula.Dht;
using Macula.Identity;
using static Macula.Tests.CertChainFixtures;

namespace Macula.Tests;

/// <summary>
/// Mirrors macula-go's own dht/cert_chain_test.go fixtures and cases
/// exactly (valid, absent, bad signature, key mismatch, org mismatch,
/// expired, wrong CA, undecodable) -- same algorithm, same five failure
/// modes, ported test-for-test. The certificate fixtures live in
/// <see cref="CertChainFixtures"/>.
/// </summary>
public class CertChainTests
{
    private const string Uri = "0000/acme-corp/widget.build_v1";

    [Fact]
    public void Valid_chain_verifies()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"); // does not throw
    }

    [Fact]
    public void Absent_chain_throws()
    {
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var rec = RecordFactory.NewProcedureAdvertisement(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1));
        rec = RecordFactory.Sign(rec, advertiser);

        var (caPem, _, _) = TestCa();
        Assert.Throws<CertChain.CertChainAbsentException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }

    [Fact]
    public void Bad_envelope_signature_throws()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);
        var tamperedSig = (byte[])rec.Signature!.Clone();
        tamperedSig[0] ^= 0xFF;
        var tampered = new Macula.Dht.Record { Type = rec.Type, Key = rec.Key, Version = rec.Version, CreatedAt = rec.CreatedAt, ExpiresAt = rec.ExpiresAt, Payload = rec.Payload, Signature = tamperedSig };

        Assert.Throws<CertChain.CertChainBadSignatureException>(() => CertChain.VerifyAdvertisementCertChain(caPem, tampered, "acme-corp"));
    }

    [Fact]
    public void Leaf_key_mismatch_throws()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var otherKey = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, otherKey.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainKeyMismatchException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }

    [Fact]
    public void Org_mismatch_throws()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), "0000/other-org/widget.build_v1", station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainOrgMismatchException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "other-org"));
    }

    [Fact]
    public void Expired_leaf_throws_untrusted()
    {
        var (caPem, caCert, caPriv) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(-1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainUntrustedException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }

    [Fact]
    public void Wrong_ca_throws_untrusted()
    {
        var (_, caCert, caPriv) = TestCa();
        var (otherCaPem, _, _) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var leafDer = TestLeaf(caCert, caPriv, advertiser.PublicBytes(), "acme-corp", DateTime.UtcNow.AddHours(1));

        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), PemBundle(leafDer));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainUntrustedException>(() => CertChain.VerifyAdvertisementCertChain(otherCaPem, rec, "acme-corp"));
    }

    [Fact]
    public void Undecodable_chain_throws()
    {
        var (caPem, _, _) = TestCa();
        var advertiser = KeyPair.GenerateWithDefaultPuzzle();
        var station = KeyPair.GenerateWithDefaultPuzzle();
        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(advertiser.PublicBytes(), Uri, station.PublicBytes(), TimeSpan.FromHours(1), System.Text.Encoding.ASCII.GetBytes("not a pem cert bundle"));
        rec = RecordFactory.Sign(rec, advertiser);

        Assert.Throws<CertChain.CertChainUndecodableException>(() => CertChain.VerifyAdvertisementCertChain(caPem, rec, "acme-corp"));
    }
}
