using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>How a device request is signed: which form of it the realm rebuilds.</summary>
public enum DeviceRequestRule
{
    /// <summary>A join session's HTTP body, under the realm's JSON rule.</summary>
    Http = 0,
    /// <summary>A mesh payload, as it goes on the wire.</summary>
    Mesh = 1,
}

/// <summary>
/// A device's request to a realm, signed (realm proof v2, macula-realm#29): a join session over HTTP, or a
/// membership UCAN over the mesh. The proof binds the device's key to exactly this request, in this realm,
/// once. <see cref="NodeKey.DeviceRequestProof(MeshId, string, JsonObject, DeviceRequestRule)"/> makes one;
/// a request carrying a <c>"caller"</c> is refused, since the realm drops it: the caller is the signer.
/// </summary>
public static class DeviceRequestProofs
{
    /// <summary>The procedure of a join session over HTTP.</summary>
    public const string JoinSession = "macula_realm.join_session";

    /// <summary>The procedure of a membership UCAN asked for over the mesh.</summary>
    public const string MembershipUcan = "macula_realm.membership_ucan";

    /// <summary>
    /// The exact bytes a proof signs, for a given timestamp and 16-byte nonce, over <paramref name="request"/>
    /// under <paramref name="rule"/>: what a verifier rebuilds.
    /// </summary>
    public static byte[] Message(ReadOnlySpan<byte> publicKey, MeshId realm, string procedure, long timestampMs,
        ReadOnlySpan<byte> nonce, JsonObject request, DeviceRequestRule rule)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MessageOf(publicKey, realm, procedure, timestampMs, nonce, RequestJson(request, rule), rule);
    }

    /// <summary>The exact bytes a proof over a join session's <paramref name="body"/>, as sent, signs.</summary>
    public static byte[] Message(ReadOnlySpan<byte> publicKey, MeshId realm, string procedure, long timestampMs,
        ReadOnlySpan<byte> nonce, string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return MessageOf(publicKey, realm, procedure, timestampMs, nonce, body, DeviceRequestRule.Http);
    }

    internal static string RequestJson(JsonObject request, DeviceRequestRule rule)
    {
        if (!Enum.IsDefined(rule))
        {
            throw new ArgumentOutOfRangeException(nameof(rule), rule, "a request rule is Http or Mesh");
        }
        return request.ToJsonString();
    }

    private static unsafe byte[] MessageOf(ReadOnlySpan<byte> publicKey, MeshId realm, string procedure,
        long timestampMs, ReadOnlySpan<byte> nonce, string requestJson, DeviceRequestRule rule)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        if (nonce.Length != 16)
        {
            throw new ArgumentException($"a device request nonce is 16 bytes, not {nonce.Length}", nameof(nonce));
        }
        NativeCall.EnsureLoaded();
        nint err = 0;
        nint bytes;
        nuint length;
        fixed (byte* k = publicKey, r = realm.Bytes, n = nonce)
        {
            bytes = Libmacula.macula_device_request_message(k, (nuint)publicKey.Length, r, procedure, timestampMs, n,
                requestJson, (int)rule, out length, ref err);
        }
        NativeCall.Check(err);
        return NativeCall.TakeBytes(bytes, length);
    }
}
