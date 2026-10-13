using System.Runtime.InteropServices;

namespace Macula.Native;

// libmacula's C ABI as macula-go's cabi/macula.h declares it (ABI 1), one
// declaration per function, in the header's order. cabi/CONTRACT.md says what
// each means; this file only binds them. A returned char* or uint8_t* is
// freed with macula_free_string / macula_free_bytes (NativeText, NativeBytes),
// and every char** err_out starts at 0.
internal static unsafe partial class Libmacula
{
    // libmacula, not macula: Windows names are case-insensitive, and a native
    // macula.dll would be the managed Macula.dll beside it.
    internal const string Library = "libmacula";

    // The ABI this binding is written against: MACULA_ABI_VERSION.
    internal const int AbiVersion = 1;

    // The wire this binding is written against: MACULA_WIRE_VERSION, which
    // macula.h defines beside the ABI. libmacula says which wire it speaks
    // (macula_wire_version), and the binding refuses any other major
    // (AcceptsWire, NativeCall).
    internal const string WireVersion = "1.0";

    // Whether a library reporting this wire is one this binding can speak.
    // Another major is another wire, and every 1.x peer shares the frozen core,
    // so a 1.1 library is spoken with, not refused (cabi/CONTRACT.md
    // "Version"). A library that answers nothing recognizable is refused.
    internal static bool AcceptsWire(string? libraryWire)
    {
        var major = WireMajor(libraryWire);
        return major.Length != 0 && major == WireMajor(WireVersion);
    }

    private static string WireMajor(string? wire)
    {
        if (string.IsNullOrEmpty(wire))
        {
            return "";
        }
        var dot = wire.IndexOf('.');
        return dot < 0 ? wire : wire[..dot];
    }

    // The oldest macula-go release this package ships a library from, and so
    // the oldest one that can be beside this assembly: v0.27.0-rc.1's libmacula
    // speaks wire 1.0 and says so. Every earlier release speaks an older wire,
    // and is refused by name when the library is loaded (NativeCall), rather
    // than by guessing at what it exports. Those earlier libraries are also the
    // ones missing the bounds this package relies on: handshake v5's channel
    // binding (v0.20.0), SecP384r1MLKEM1024 alone so no station can land it on
    // SecP256r1MLKEM768 (v0.23.0, macula-go#20), and the refusal of a UCAN
    // whose issuer did:key is over 4,400 characters before decoding it
    // (v0.26.0, macula#87) that an older library would spend CPU on.
    internal const string LibraryFloor = "v0.27.0-rc.1";

    // ---- The library ----

    [LibraryImport(Library)]
    internal static partial int macula_abi_version();

    // Since macula-go v0.27.0 (cabi/CONTRACT.md "Version"): the release the
    // library was built from ("v0.27.0-rc.1", or "devel") and the wire it
    // speaks ("1.0"). Read at load, to refuse a library that is not this
    // package's wire (NativeCall). The library owns both strings: never freed.
    [LibraryImport(Library)]
    internal static partial nint macula_library_version();

    [LibraryImport(Library)]
    internal static partial nint macula_wire_version();

    [LibraryImport(Library)]
    internal static partial void macula_free_string(nint s);

    [LibraryImport(Library)]
    internal static partial void macula_free_bytes(nint b);

    // ---- Cancellation ----

    [LibraryImport(Library)]
    internal static partial CancelHandle macula_cancel_new();

    [LibraryImport(Library)]
    internal static partial void macula_cancel(CancelHandle cancel);

    [LibraryImport(Library)]
    internal static partial void macula_cancel_free(nuint cancel);

    // ---- Node keys ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial KeyHandle macula_key_generate(string? profile, nuint cancel, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial KeyHandle macula_key_load(string path, string? profile, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial KeyHandle macula_key_load_or_create(string path, string? profile, nuint cancel,
        ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_key_save(KeyHandle key, string path, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_key_node_id(KeyHandle key, byte* outNodeId, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_key_public_key(KeyHandle key, out nuint outLen, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_key_profile(KeyHandle key, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_key_sign(KeyHandle key, byte* data, nuint dataLen, out nuint outLen, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int macula_verify(byte* data, nuint dataLen, byte* signature, nuint signatureLen,
        byte* publicKey, nuint publicKeyLen, string? profile, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_key_free(nuint key);

    // ---- Device request proofs (realm proof v2), since macula-go v0.14.0 ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_key_device_request_proof(KeyHandle key, byte* realm, string procedure,
        string requestJson, int rule, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_device_request_message(byte* publicKey, nuint publicKeyLen, byte* realm,
        string procedure, long timestampMs, byte* nonce, string requestJson, int rule, out nuint outLen, ref nint err);

    // ---- Ownership proofs (v2), since macula-go v0.16.0 ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_key_ownership_proof(KeyHandle key, byte* realm, string procedure,
        string payloadJson, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_ownership_proof_message(byte* identity, byte* realm, string procedure,
        long timestampMs, byte* nonce, string fieldsJson, out nuint outLen, ref nint err);

    // ---- UCANs, since macula-go v0.17.0 ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_ucan_create(KeyHandle key, byte* audienceNodeId, string capsJson, long expS,
        string? optionsJson, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_ucan_proof_id(string token, ref nint err);

    // ---- Pool ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial PoolHandle macula_pool_connect(KeyHandle key, string seedsJson, string? optionsJson,
        nuint cancel, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_pool_close(nuint pool);

    [LibraryImport(Library)]
    internal static partial void macula_pool_node_id(PoolHandle pool, byte* outNodeId, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_pool_status(PoolHandle pool, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_pool_events_next(PoolHandle pool, long timeoutMs, nuint cancel,
        out int closed, ref nint err);

    // ---- Calls ----

    // A call's options (provider, UCAN, proofs, confidentiality, report) as JSON, since macula-go v0.18.0: the one way
    // this binding calls.
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_pool_call_opts(PoolHandle pool, byte* realm, string procedure,
        string payloadJson, string? optionsJson, long timeoutMs, nuint cancel, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_pool_providers(PoolHandle pool, byte* realm, string procedure, long timeoutMs,
        nuint cancel, ref nint err);

    // ---- Publish / subscribe ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_pool_publish(PoolHandle pool, byte* realm, string topic, string payloadJson,
        long ttlMs, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial SubscriptionHandle macula_pool_subscribe(PoolHandle pool, byte* realm, string topic,
        ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_subscription_next(SubscriptionHandle subscription, long timeoutMs,
        nuint cancel, out int closed, ref nint err);

    [LibraryImport(Library)]
    internal static partial ulong macula_subscription_dropped(SubscriptionHandle subscription, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_subscription_stop(nuint subscription);

    // ---- Serving ----

    // A served procedure's options (policy, confidentiality) as JSON, since macula-go v0.18.0: the one way this
    // binding serves.
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ServedHandle macula_pool_serve_opts(PoolHandle pool, byte* realm, string procedure,
        string? optionsJson, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ServedHandle macula_pool_serve_stream_opts(PoolHandle pool, byte* realm, string procedure,
        int mode, string? optionsJson, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_served_next(ServedHandle served, long timeoutMs, nuint cancel,
        out nuint outItem, out int closed, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_pending_reply(nuint pending, string resultJson, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_pending_error(nuint pending, string message, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_served_stop(nuint served, ref nint err);

    // ---- Streams ----

    // An open's options (a call's, without report) as JSON, since macula-go v0.18.0: the one way this binding opens.
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial StreamHandle macula_pool_open_stream_opts(PoolHandle pool, byte* realm, string procedure,
        int mode, string payloadJson, string? optionsJson, long deadlineMs, long timeoutMs, nuint cancel,
        ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_stream_request(StreamHandle stream, ref nint err);

    // A caller stream's seal report, since macula-go v0.19.0.
    [LibraryImport(Library)]
    internal static partial nint macula_stream_report(StreamHandle stream, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_stream_send_bytes(StreamHandle stream, byte* data, nuint dataLen, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_stream_send_json(StreamHandle stream, string valueJson, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_stream_close_send(StreamHandle stream, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_stream_reply(StreamHandle stream, string payloadJson, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_stream_abort(StreamHandle stream, string code, string message, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_stream_close(StreamHandle stream, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_stream_recv(StreamHandle stream, long timeoutMs, nuint cancel,
        ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_stream_free(nuint stream);

    // ---- Content ----

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void macula_pool_share_content(PoolHandle pool, byte* realm, byte* data, nuint dataLen,
        string? name, long timeoutMs, nuint cancel, byte* outMcid, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_pool_unshare_content(PoolHandle pool, byte* realm, byte* mcid, long timeoutMs,
        nuint cancel, ref nint err);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint macula_pool_get_content(PoolHandle pool, byte* realm, byte* mcid, string? optionsJson,
        long timeoutMs, nuint cancel, out nuint outLen, ref nint err);

    // ---- DHT records ----

    [LibraryImport(Library)]
    internal static partial nint macula_pool_find_record(PoolHandle pool, byte* key, long timeoutMs,
        nuint cancel, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_pool_find_records(PoolHandle pool, byte* key, long timeoutMs,
        nuint cancel, ref nint err);

    [LibraryImport(Library)]
    internal static partial nint macula_pool_find_records_by_type(PoolHandle pool, int recordType, long timeoutMs,
        nuint cancel, ref nint err);

    [LibraryImport(Library)]
    internal static partial void macula_pool_put_record(PoolHandle pool, byte* wire, nuint wireLen, long timeoutMs,
        nuint cancel, ref nint err);
}
