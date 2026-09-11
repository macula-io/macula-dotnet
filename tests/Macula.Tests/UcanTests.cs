using Macula.Identity;
using Macula.Ucan;

namespace Macula.Tests;

public class UcanTests
{
    /// <summary>
    /// A real UCAN token minted by macula-go's own ucan.Create,
    /// captured from a real `go run` against a fixed 32-byte seed (bytes
    /// 0x00..0x1F) -- not hand-constructed. Verifying this here proves
    /// actual cross-language wire compatibility (same header shape, same
    /// signing input, same signature algorithm), not just a doc-comment
    /// claim, matching the same discipline macula-rust's own UCAN port
    /// used this session.
    /// </summary>
    private const string CrossLangPubkeyHex = "03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8";
    private const string CrossLangToken =
        "eyJhbGciOiJFZERTQSIsInR5cCI6IkpXVCIsInVjdiI6IjAuMTAuMCJ9." +
        "eyJpc3MiOiJkaWQ6bWFjdWxhOnRlc3QtaXNzdWVyIiwiYXVkIjoiZGlkOm1hY3VsYTp0ZXN0LWF1ZGllbmNlIiwiZXhwIjo0MTAyNDQ0ODAwLCJjYXAiOlt7IndpdGgiOiJtcmk6bWFpbGJveDp0ZXN0IiwiY2FuIjoicmVhZCJ9XSwicHJmIjpbXX0." +
        "ZH-2Nbx0C35xKjymkCINHShhzx8MPeyCXuTt1-l0LK6iNNVChoM7V3vL7_K1A_EEVL5U0VA7z1JFu_6TSu1RCQ";

    [Fact]
    public void Cross_language_token_from_go_sdk_verifies_here()
    {
        var pubkey = Convert.FromHexString(CrossLangPubkeyHex);
        var token = System.Text.Encoding.UTF8.GetBytes(CrossLangToken);

        var payload = UcanToken.Verify(token, pubkey);

        Assert.Equal("did:macula:test-issuer", payload.Issuer);
        Assert.Equal("did:macula:test-audience", payload.Audience);
        Assert.Equal(4102444800L, payload.ExpiresAt);
        Assert.Single(payload.Capabilities);
        Assert.Equal("mri:mailbox:test", payload.Capabilities[0].With);
        Assert.Equal("read", payload.Capabilities[0].Can);
    }

    [Fact]
    public void Create_then_verify_round_trips()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var caps = new[] { new UcanToken.Capability("mri:mailbox:mine", "write") };
        var token = UcanToken.Create("did:macula:issuer", "did:macula:audience", caps, id);

        var payload = UcanToken.Verify(token, id.PublicBytes());

        Assert.Equal("did:macula:issuer", payload.Issuer);
        Assert.Equal("did:macula:audience", payload.Audience);
        Assert.Equal("mri:mailbox:mine", payload.Capabilities[0].With);
        Assert.Null(payload.ExpiresAt);
    }

    [Fact]
    public void Verify_rejects_a_tampered_signature()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var token = UcanToken.Create("did:macula:issuer", "did:macula:audience", Array.Empty<UcanToken.Capability>(), id);
        var parts = System.Text.Encoding.UTF8.GetString(token).Split('.');
        var tampered = System.Text.Encoding.UTF8.GetBytes(parts[0] + "." + parts[1] + ".AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.Throws<UcanToken.InvalidSignatureException>(() => UcanToken.Verify(tampered, id.PublicBytes()));
    }

    [Fact]
    public void Verify_rejects_the_wrong_signer()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var otherId = KeyPair.GenerateWithDefaultPuzzle();
        var token = UcanToken.Create("did:macula:issuer", "did:macula:audience", Array.Empty<UcanToken.Capability>(), id);

        Assert.Throws<UcanToken.InvalidSignatureException>(() => UcanToken.Verify(token, otherId.PublicBytes()));
    }

    [Fact]
    public void Verify_rejects_an_expired_token()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var token = UcanToken.Create("did:macula:issuer", "did:macula:audience", Array.Empty<UcanToken.Capability>(), id,
            new UcanToken.CreateOpts { ExpiresAt = past });

        Assert.Throws<UcanToken.ExpiredException>(() => UcanToken.Verify(token, id.PublicBytes()));
    }

    [Fact]
    public void Verify_rejects_a_not_yet_valid_token()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var future = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var token = UcanToken.Create("did:macula:issuer", "did:macula:audience", Array.Empty<UcanToken.Capability>(), id,
            new UcanToken.CreateOpts { NotBefore = future });

        Assert.Throws<UcanToken.NotYetValidException>(() => UcanToken.Verify(token, id.PublicBytes()));
    }

    [Fact]
    public void Is_expired_reports_correctly_without_verifying()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var past = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var expired = UcanToken.Create("did:macula:issuer", "did:macula:audience", Array.Empty<UcanToken.Capability>(), id,
            new UcanToken.CreateOpts { ExpiresAt = past });
        var noExpiry = UcanToken.Create("did:macula:issuer", "did:macula:audience", Array.Empty<UcanToken.Capability>(), id);

        Assert.True(UcanToken.IsExpired(expired));
        Assert.False(UcanToken.IsExpired(noExpiry));
    }

    [Fact]
    public void Getters_read_claims_without_verifying()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var caps = new[] { new UcanToken.Capability("mri:x", "y") };
        var token = UcanToken.Create("did:macula:i", "did:macula:a", caps, id, new UcanToken.CreateOpts { Proofs = new[] { "parentcid" } });

        Assert.Equal("did:macula:i", UcanToken.GetIssuer(token));
        Assert.Equal("did:macula:a", UcanToken.GetAudience(token));
        Assert.Equal("mri:x", UcanToken.GetCapabilities(token)[0].With);
        Assert.Equal("parentcid", UcanToken.GetProofs(token)[0]);
    }

    [Fact]
    public void Policy_open_always_passes()
    {
        Policy.Open.Check(Array.Empty<byte>(), Array.Empty<byte>());
    }

    [Fact]
    public void Policy_gated_rejects_no_token()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var caller = KeyPair.GenerateWithDefaultPuzzle();
        var policy = Policy.Required(id.PublicBytes());
        Assert.Throws<UcanToken.NoTokenException>(() => policy.Check(Array.Empty<byte>(), caller.NodeId()));
    }

    [Fact]
    public void Policy_gated_accepts_a_valid_token_from_the_required_issuer()
    {
        var id = KeyPair.GenerateWithDefaultPuzzle();
        var caller = KeyPair.GenerateWithDefaultPuzzle();
        var policy = Policy.Required(id.PublicBytes());
        var token = UcanToken.Create("did:macula:issuer", Convert.ToHexStringLower(caller.NodeId()), Array.Empty<UcanToken.Capability>(), id);

        policy.Check(token, caller.NodeId()); // does not throw
    }

    [Fact]
    public void Policy_gated_rejects_a_token_from_a_different_issuer()
    {
        var requiredId = KeyPair.GenerateWithDefaultPuzzle();
        var otherId = KeyPair.GenerateWithDefaultPuzzle();
        var caller = KeyPair.GenerateWithDefaultPuzzle();
        var policy = Policy.Required(requiredId.PublicBytes());
        var token = UcanToken.Create("did:macula:issuer", Convert.ToHexStringLower(caller.NodeId()), Array.Empty<UcanToken.Capability>(), otherId);

        Assert.Throws<UcanToken.InvalidSignatureException>(() => policy.Check(token, caller.NodeId()));
    }

    [Fact]
    public void Policy_required_refuses_a_token_for_another_audience()
    {
        var issuer = KeyPair.Generate();
        var audience = KeyPair.Generate();
        var presenter = KeyPair.Generate();
        var policy = Policy.Required(issuer.PublicBytes());
        var token = UcanToken.Create("did:macula:issuer", Convert.ToHexStringLower(audience.NodeId()), Array.Empty<UcanToken.Capability>(), issuer);

        Assert.Throws<UcanToken.WrongAudienceException>(() => policy.Check(token, presenter.NodeId()));
        policy.Check(token, audience.NodeId()); // the same token from its audience passes
    }

    [Fact]
    public void Policy_required_refuses_a_missing_or_malformed_audience_or_caller()
    {
        var issuer = KeyPair.Generate();
        var caller = KeyPair.Generate();
        var policy = Policy.Required(issuer.PublicBytes());
        var callerHex = Convert.ToHexStringLower(caller.NodeId());
        byte[] TokenFor(string audience) => UcanToken.Create("did:macula:issuer", audience, Array.Empty<UcanToken.Capability>(), issuer);

        Assert.Throws<UcanToken.WrongAudienceException>(() => policy.Check(TokenFor(""), caller.NodeId()));
        Assert.Throws<UcanToken.WrongAudienceException>(() => policy.Check(TokenFor(callerHex.ToUpperInvariant()), caller.NodeId()));
        Assert.Throws<UcanToken.WrongAudienceException>(() => policy.Check(TokenFor($"did:macula:{callerHex}"), caller.NodeId()));
        Assert.Throws<UcanToken.NoCallerException>(() => policy.Check(TokenFor(callerHex), Array.Empty<byte>()));
    }
}
