using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>
/// A capability a UCAN grants: <see cref="With"/> an MRI (<c>mri:realm:&lt;realm&gt;</c>,
/// <c>mri:org:&lt;realm&gt;/&lt;org&gt;</c> or <c>mri:proc:&lt;realm&gt;/&lt;org&gt;/&lt;name&gt;</c>, a realm named
/// by its name, whose SHA-256 is its id), and <see cref="Can"/> the action.
/// </summary>
public sealed record Capability
{
    /// <summary>A capability to <paramref name="can"/> on <paramref name="with"/>.</summary>
    public Capability(string with, string can)
    {
        ArgumentException.ThrowIfNullOrEmpty(with);
        ArgumentException.ThrowIfNullOrEmpty(can);
        With = with;
        Can = can;
    }

    /// <summary>The MRI the capability is on.</summary>
    public string With { get; }

    /// <summary>The action it grants.</summary>
    public string Can { get; }
}

/// <summary>A UCAN's optional claims.</summary>
public sealed class UcanOptions
{
    /// <summary>When the token becomes valid: at once when null.</summary>
    public DateTimeOffset? NotBefore { get; init; }

    /// <summary>A nonce, when one is wanted.</summary>
    public string? Nonce { get; init; }

    /// <summary>Facts the token carries.</summary>
    public JsonObject? Facts { get; init; }

    /// <summary>
    /// The proof id (<see cref="Ucan.ProofId"/>) of the token this one is delegated from, which must travel
    /// with it as a proof; a root token when null. A chain is linear: one parent.
    /// </summary>
    public string? Parent { get; init; }
}

/// <summary>A UCAN a call or a stream open presents, and the tokens of its chain, parents first or not.</summary>
/// <param name="Token">The token.</param>
/// <param name="Proofs">The chain's other tokens, each named by a <see cref="UcanOptions.Parent"/>.</param>
public sealed record UcanPresentation(string Token, IReadOnlyList<string>? Proofs = null);

/// <summary>
/// What a gated procedure requires of a caller's UCAN. The provider checks each call and stream open before
/// the handler sees it, as macula's link does, and refuses the rest with <c>unauthorized</c> (or
/// <c>malformed_frame</c> for a proof no token in the chain names).
/// </summary>
public abstract record AuthPolicy
{
    private protected AuthPolicy()
    {
    }

    internal abstract JsonObject ToJson();
}

/// <summary>A chain rooted at the identity key of the node <see cref="Issuer"/>.</summary>
/// <param name="Issuer">The root's node id.</param>
public sealed record UcanRequired(MeshId Issuer) : AuthPolicy
{
    internal override JsonObject ToJson() => new() { ["kind"] = "ucan_required", ["issuer"] = Issuer.ToString() };
}

/// <summary>A chain rooted at the realm key <see cref="KeyId"/> names, granting a capability of <see cref="Can"/>.</summary>
public sealed record RealmMemberRequired : AuthPolicy
{
    /// <summary>A realm member policy: the realm key's key id (<see cref="Ucan.KeyId"/>) and the action.</summary>
    public RealmMemberRequired(MeshId keyId, string can)
    {
        ArgumentException.ThrowIfNullOrEmpty(can);
        KeyId = keyId;
        Can = can;
    }

    /// <summary>The realm key's key id.</summary>
    public MeshId KeyId { get; }

    /// <summary>The action the capability must grant.</summary>
    public string Can { get; }

    internal override JsonObject ToJson() =>
        new() { ["kind"] = "realm_member_required", ["key_id"] = KeyId.ToString(), ["can"] = Can };
}

/// <summary>
/// macula 12's UCANs (D7): capability tokens signed with a node's identity key in its profile. Mint one with
/// <see cref="NodeKey.CreateUcan"/> for the node that will present it, present it with
/// <see cref="CallOptions.Ucan"/> or <see cref="Pool.OpenStreamAsync"/>, and gate a procedure on one with the
/// policy of <see cref="Pool.Serve"/> or <see cref="Pool.ServeStream"/>. macula's
/// <c>test/vectors/UCAN_V1.md</c> is the contract.
/// </summary>
public static class Ucan
{
    private static readonly byte[] KeyIdLabel = "MACULA-KEY-ID-V1"u8.ToArray();

    /// <summary>The id a child's parent names <paramref name="token"/> by: lowercase hex SHA-384 of its text.</summary>
    public static string ProofId(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        NativeCall.EnsureLoaded();
        nint err = 0;
        var id = Libmacula.macula_ucan_proof_id(token, ref err);
        NativeCall.Check(err);
        return NativeCall.TakeString(id)!;
    }

    /// <summary>
    /// The key id of a key as carried, in its profile: what a <see cref="RealmMemberRequired"/> policy names a
    /// realm key by. SHA-256 over a label, a zero byte, the profile name's length and text, and the key
    /// (macula_node_keys:key_id/2).
    /// </summary>
    public static MeshId KeyId(ReadOnlySpan<byte> publicKey, Profile profile)
    {
        var name = Encoding.ASCII.GetBytes(Profiles.Name(profile));
        return new MeshId(SHA256.HashData([.. KeyIdLabel, 0, (byte)name.Length, .. name, .. publicKey]));
    }
}
