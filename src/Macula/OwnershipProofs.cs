using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>
/// The ownership proof (v2, mcl-om#7): the <c>asserted_by</c> block by which a node authorises exactly one
/// request's fields, for one procedure in one realm, once. <see cref="NodeKey.OwnershipProof"/> returns the
/// payload with its block, to send as the payload; a verifier (mcl_om) checks it against the fields it
/// receives. The fields are the payload minus <c>asserted_by</c> and minus a <c>"caller"</c>, which a station
/// replaces with the caller it authenticated: a payload carrying one is refused when signing.
/// </summary>
public static class OwnershipProofs
{
    /// <summary>
    /// The exact bytes a proof over a payload's fields signs, for a given identity (node id), timestamp and
    /// 16-byte nonce, as the verifier rebuilds them from a delivered payload.
    /// </summary>
    public static unsafe byte[] Message(MeshId identity, MeshId realm, string procedure, long timestampMs,
        ReadOnlySpan<byte> nonce, JsonObject fields)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(fields);
        if (nonce.Length != 16)
        {
            throw new ArgumentException($"an ownership proof nonce is 16 bytes, not {nonce.Length}", nameof(nonce));
        }
        NativeCall.EnsureLoaded();
        nint err = 0;
        nint bytes;
        nuint length;
        fixed (byte* i = identity.Bytes, r = realm.Bytes, n = nonce)
        {
            bytes = Libmacula.macula_ownership_proof_message(i, r, procedure, timestampMs, n, Payload.ToJson(fields),
                out length, ref err);
        }
        NativeCall.Check(err);
        return NativeCall.TakeBytes(bytes, length);
    }
}
