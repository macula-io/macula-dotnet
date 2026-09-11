using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Macula.Identity;

namespace Macula.Ucan;

/// <summary>
/// Macula's UCAN (User Controlled Authorization Networks) tokens: creation,
/// verification, and introspection. Ported from macula_ucan_nif.erl -- a
/// JWT-shaped token (header.payload.signature, base64url-no-pad,
/// dot-joined), EdDSA over Ed25519, UCAN spec version "0.10.0" (the OLDER
/// JWT-based draft; NOT the current non-JWT/IPLD UCAN 1.0 spec -- confirmed
/// by reading both the Erlang fallback and its Rust NIF directly, both
/// hand-roll this exact format because no existing library implements
/// 0.10.0; the only actively maintained UCAN library targets 1.0.0-rc.1,
/// an incompatible CBOR/IPLD envelope). This type does the same:
/// hand-rolled on BCL crypto + System.Text.Json, matching the reference
/// exactly rather than adopting an incompatible library.
///
/// A token minted here verifies against macula-go, macula-rust, or
/// the Erlang macula SDK, or vice versa -- same header shape, same payload
/// field names (iss/aud/exp/nbf/nnc/cap/fct/prf), same signing input
/// (header_b64 + "." + payload_b64), same signature algorithm. Field ORDER
/// in the JSON is not part of the compatibility contract, only the field
/// NAMES and the exact bytes signed matter.
/// </summary>
public static class UcanToken
{
    private const string Alg = "EdDSA";
    private const string Typ = "JWT";
    private const string Ucv = "0.10.0";

    public sealed class InvalidTokenException : Exception
    {
        public InvalidTokenException() : base("ucan: invalid token") { }
    }

    public sealed class InvalidSignatureException : Exception
    {
        public InvalidSignatureException() : base("ucan: invalid signature") { }
    }

    public sealed class InvalidPublicKeyException : Exception
    {
        public InvalidPublicKeyException() : base("ucan: invalid public key") { }
    }

    public sealed class ExpiredException : Exception
    {
        public ExpiredException() : base("ucan: token expired") { }
    }

    public sealed class NotYetValidException : Exception
    {
        public NotYetValidException() : base("ucan: token not yet valid") { }
    }

    /// <summary>A UCAN-gated procedure was called with no token at all -- mirrors macula_station_link.erl's check_ucan(&lt;&lt;&gt;&gt;, _) -&gt; unauthorized clause.</summary>
    public sealed class NoTokenException : Exception
    {
        public NoTokenException() : base("ucan: no token presented for a gated procedure") { }
    }

    /// <summary>A UCAN-gated procedure was called with no caller to bind the token to; a missing caller is unauthorized.</summary>
    public sealed class NoCallerException : Exception
    {
        public NoCallerException() : base("ucan: no caller to check the token's audience against") { }
    }

    /// <summary>A token's aud is not the presenting caller's node id as lowercase hex, so it was minted for someone else.</summary>
    public sealed class WrongAudienceException : Exception
    {
        public WrongAudienceException() : base("ucan: token audience is not the caller presenting it") { }
    }

    /// <summary>One entry in a UCAN token's capability list -- mirrors macula_ucan_nif's capability() :: #{with := binary(), can := binary()}.</summary>
    public sealed record Capability(
        [property: JsonPropertyName("with")] string With,
        [property: JsonPropertyName("can")] string Can);

    private sealed class WireHeader
    {
        [JsonPropertyName("alg")] public string Alg { get; init; } = "";
        [JsonPropertyName("typ")] public string Typ { get; init; } = "";
        [JsonPropertyName("ucv")] public string Ucv { get; init; } = "";
    }

    // The JSON shape actually signed/transmitted. Field names match the
    // reference exactly.
    private sealed class WirePayload
    {
        [JsonPropertyName("iss")] public string Issuer { get; init; } = "";
        [JsonPropertyName("aud")] public string Audience { get; init; } = "";
        [JsonPropertyName("exp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? ExpiresAt { get; init; }
        [JsonPropertyName("nbf"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? NotBefore { get; init; }
        [JsonPropertyName("nnc"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Nonce { get; init; }
        [JsonPropertyName("cap")] public List<Capability> Capabilities { get; init; } = new();
        [JsonPropertyName("fct"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, object>? Facts { get; init; }
        [JsonPropertyName("prf")] public List<string> Proofs { get; init; } = new();
    }

    /// <summary>A UCAN token's decoded claims.</summary>
    public sealed record Payload(
        string Issuer,
        string Audience,
        IReadOnlyList<Capability> Capabilities,
        long? ExpiresAt,
        long? NotBefore,
        string Nonce,
        IReadOnlyDictionary<string, object>? Facts,
        IReadOnlyList<string> Proofs);

    /// <summary>Optional UCAN claims -- mirrors macula_ucan_nif's ucan_opts() map.</summary>
    public sealed class CreateOpts
    {
        public long? ExpiresAt { get; init; }
        public long? NotBefore { get; init; }
        public string? Nonce { get; init; }
        public Dictionary<string, object>? Facts { get; init; }
        public IReadOnlyList<string>? Proofs { get; init; }
    }

    /// <summary>
    /// Mints a new UCAN token, self-issued and signed by id. issuer and
    /// audience are opaque strings -- this method does not validate or
    /// resolve DID structure, matching macula_ucan_nif:create/4,5's own
    /// scope exactly.
    ///
    /// For a token a caller presents to a procedure gated with
    /// <see cref="Policy.Required"/>, audience must be that caller's
    /// 32-byte node id as lowercase hex, with no did: prefix:
    /// <c>Convert.ToHexStringLower(caller.NodeId())</c>. The provider
    /// refuses a token whose aud names anyone other than the caller whose
    /// signature is on the CALL.
    /// </summary>
    public static byte[] Create(string issuer, string audience, IReadOnlyList<Capability> capabilities, KeyPair id, CreateOpts? opts = null)
    {
        opts ??= new CreateOpts();
        var payload = new WirePayload
        {
            Issuer = issuer,
            Audience = audience,
            ExpiresAt = opts.ExpiresAt,
            NotBefore = opts.NotBefore,
            Nonce = opts.Nonce is { Length: > 0 } n ? n : null,
            Capabilities = capabilities.ToList(),
            Facts = opts.Facts,
            Proofs = (opts.Proofs ?? Array.Empty<string>()).ToList(),
        };

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(new WireHeader { Alg = Alg, Typ = Typ, Ucv = Ucv });
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload);
        var headerB64 = Base64Url.EncodeToString(headerJson);
        var payloadB64 = Base64Url.EncodeToString(payloadJson);
        var signingInput = headerB64 + "." + payloadB64;
        var sig = id.Sign(Encoding.UTF8.GetBytes(signingInput));
        var sigB64 = Base64Url.EncodeToString(sig);
        return Encoding.UTF8.GetBytes(signingInput + "." + sigB64);
    }

    private static (string HeaderB64, string PayloadB64, string SigB64) SplitToken(byte[] token)
    {
        var parts = Encoding.UTF8.GetString(token).Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidTokenException();
        }
        return (parts[0], parts[1], parts[2]);
    }

    private static Payload DecodePayload(string payloadB64)
    {
        byte[] raw;
        WirePayload? wp;
        try
        {
            raw = Base64Url.DecodeFromChars(payloadB64);
            wp = JsonSerializer.Deserialize<WirePayload>(raw);
        }
        catch (Exception)
        {
            throw new InvalidTokenException();
        }
        if (wp is null)
        {
            throw new InvalidTokenException();
        }
        return new Payload(wp.Issuer, wp.Audience, wp.Capabilities, wp.ExpiresAt, wp.NotBefore, wp.Nonce ?? "", wp.Facts, wp.Proofs);
    }

    /// <summary>
    /// Parses a UCAN token's payload WITHOUT verifying its signature or
    /// checking expiration. Mirrors macula_ucan_nif:decode/1 -- same
    /// warning applies: never use this for an authorization decision, only
    /// Verify does that.
    /// </summary>
    public static Payload Decode(byte[] token)
    {
        var (_, payloadB64, _) = SplitToken(token);
        return DecodePayload(payloadB64);
    }

    /// <summary>
    /// Checks a UCAN token's signature against publicKey (the claimed
    /// issuer's 32-byte Ed25519 public key) and its exp/nbf claims against
    /// the current time, returning the decoded payload only on full
    /// success. Mirrors macula_ucan_nif:verify/2 exactly, including its
    /// check ORDER (public key shape, then token shape, then exp, then nbf,
    /// then signature -- matching both the Erlang fallback and the Rust
    /// NIF, which check claims before the signature; this preserves that
    /// order for parity even though it means an invalid-but-well-formed
    /// token's expiry is observable before its signature is checked).
    /// </summary>
    public static Payload Verify(byte[] token, byte[] publicKey)
    {
        if (publicKey.Length != 32)
        {
            throw new InvalidPublicKeyException();
        }
        var (headerB64, payloadB64, sigB64) = SplitToken(token);
        var payload = DecodePayload(payloadB64);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (payload.ExpiresAt is { } exp && now > exp)
        {
            throw new ExpiredException();
        }
        if (payload.NotBefore is { } nbf && now < nbf)
        {
            throw new NotYetValidException();
        }

        byte[] sig;
        try
        {
            sig = Base64Url.DecodeFromChars(sigB64);
        }
        catch (Exception)
        {
            throw new InvalidTokenException();
        }
        var signingInput = Encoding.UTF8.GetBytes(headerB64 + "." + payloadB64);
        if (!KeyPair.Verify(signingInput, sig, publicKey))
        {
            throw new InvalidSignatureException();
        }
        return payload;
    }

    /// <summary>
    /// A UCAN token's content identifier: SHA-256 of the raw token bytes,
    /// base64url-no-pad encoded. NOT a real multihash/CIDv1 -- matches
    /// macula_ucan_nif:compute_cid/1's own (loosely-named) scheme exactly.
    /// Used only for proof-chain references between UCANs.
    /// </summary>
    public static string ComputeCid(byte[] token) => Base64Url.EncodeToString(SHA256.HashData(token));

    /// <summary>Decodes token (without verifying it) and returns its iss claim. Mirrors macula_ucan_nif:get_issuer/1.</summary>
    public static string GetIssuer(byte[] token) => Decode(token).Issuer;

    /// <summary>Decodes token (without verifying it) and returns its aud claim. Mirrors macula_ucan_nif:get_audience/1.</summary>
    public static string GetAudience(byte[] token) => Decode(token).Audience;

    /// <summary>Decodes token (without verifying it) and returns its cap claim. Mirrors macula_ucan_nif:get_capabilities/1.</summary>
    public static IReadOnlyList<Capability> GetCapabilities(byte[] token) => Decode(token).Capabilities;

    /// <summary>Decodes token (without verifying it) and returns its exp claim, or null if absent. Mirrors macula_ucan_nif:get_expiration/1.</summary>
    public static long? GetExpiration(byte[] token) => Decode(token).ExpiresAt;

    /// <summary>Decodes token (without verifying it) and returns its prf claim. Mirrors macula_ucan_nif:get_proofs/1.</summary>
    public static IReadOnlyList<string> GetProofs(byte[] token) => Decode(token).Proofs;

    /// <summary>Decodes token (without verifying it) and reports whether its exp claim is in the past. A token with no exp claim is never expired. Mirrors macula_ucan_nif:is_expired/1.</summary>
    public static bool IsExpired(byte[] token)
    {
        var p = Decode(token);
        return p.ExpiresAt is { } exp && DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp;
    }
}
