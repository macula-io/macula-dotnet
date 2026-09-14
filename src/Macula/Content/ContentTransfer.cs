using System.Runtime.Versioning;
using Macula.Bolt4;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Content;

/// <summary>
/// Content sharing: put/get by content-address, over a dedicated QUIC
/// stream -- ordinary CALL/RESULT against four well-known `_content.*`
/// procedures, ported from `macula_content_transfer.erl`. Not a separate
/// wire protocol: nothing here is new frame types.
///
/// Deliberate v1 simplification (matching the sibling Go/Rust SDKs):
/// chunked transfers run strictly sequentially, one `_content.put_block` /
/// `_content.get_block` in flight at a time on the single dedicated stream
/// this opens -- not the reference's parallel multi-lane algorithm.
/// Multi-lane parallelism is a throughput optimization, not a correctness
/// requirement: every `_content.*` call, the MCID scheme, and the manifest
/// wire format are identical either way.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public static class ContentTransfer
{
    /// <summary>Reserved realm sentinel for all `_content.*` calls -- 32 zero bytes, distinct from any real realm.</summary>
    public static readonly byte[] ContentRealm = new byte[32];

    private const string PutBlockProc = "_content.put_block";
    private const string GetBlockProc = "_content.get_block";
    private const string PutManifestProc = "_content.put_manifest";
    private const string GetManifestProc = "_content.get_manifest";

    /// <summary>Matches `CONTENT_BLOCK_TIMEOUT_MS` in macula_content_transfer.erl.</summary>
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Matches `CONTENT_MANIFEST_TIMEOUT_MS`.</summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Matches the spec's retry policy: up to 3 attempts total, 200ms backoff, only for a BOLT#4 code flagged retryable.</summary>
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromMilliseconds(200);

    public enum RemoteReason { RemoteError, UnexpectedReply, NotFound, HashMismatch, ManifestDecodeFailed, VerifyFailed }

    public sealed class ContentTransferException : Exception
    {
        public RemoteReason Reason { get; }
        public byte? Code { get; }
        public string? Name { get; }
        public string? Detail { get; }

        public ContentTransferException(RemoteReason reason, string message, byte? code = null, string? name = null, string? detail = null)
            : base(message)
        {
            Reason = reason;
            Code = code;
            Name = name;
            Detail = detail;
        }
    }

    /// <summary>
    /// Store <paramref name="data"/>, returning the MCID it's now
    /// addressable by. <paramref name="name"/> is attached to the manifest
    /// when data is large enough to be chunked; a single block is
    /// addressed purely by content hash and carries no name at all.
    /// </summary>
    public static async Task<byte[]> PutAsync(Session session, byte[] data, string name, KeyPair identity, CancellationToken ct = default)
    {
        var stream = await session.OpenDedicatedStreamAsync(ct).ConfigureAwait(false);

        if (data.Length <= ManifestBuilder.DefaultChunkSize)
        {
            var mcid = ManifestBuilder.BlockMcid(data);
            await PutBlockAsync(stream, mcid, data, identity, ct).ConfigureAwait(false);
            return mcid;
        }

        var opts = new CreateOptions { Name = name };
        var (manifest, chunks) = ManifestBuilder.Create(data, opts);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunkMcid = ManifestBuilder.ChunkMcid(manifest, index)!;
            await PutBlockAsync(stream, chunkMcid, chunks[index], identity, ct).ConfigureAwait(false);
        }
        await PutManifestAsync(stream, manifest, identity, ct).ConfigureAwait(false);
        return manifest.Mcid;
    }

    /// <summary>
    /// Fetch and verify the content addressed by <paramref name="mcid"/>. For
    /// chunked content, the fetched manifest must describe
    /// <paramref name="mcid"/> and its content whole before any of it is used.
    /// </summary>
    public static async Task<byte[]> GetAsync(Session session, byte[] mcid, KeyPair identity, CancellationToken ct = default)
    {
        var stream = await session.OpenDedicatedStreamAsync(ct).ConfigureAwait(false);

        if (!Mcid.IsChunked(mcid))
        {
            var data = await GetBlockAsync(stream, mcid, identity, ct).ConfigureAwait(false);
            if (!ManifestBuilder.BlockMcid(data).AsSpan().SequenceEqual(mcid))
            {
                throw new ContentTransferException(RemoteReason.HashMismatch, "fetched content does not hash to its MCID");
            }
            return data;
        }

        var manifest = await GetManifestAsync(stream, mcid, identity, ct).ConfigureAwait(false);
        return await AssembleAsync(mcid, manifest, (chunkMcid, token) => GetBlockAsync(stream, chunkMcid, identity, token), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The part of a chunked get that runs once <paramref name="manifest"/>,
    /// fetched for <paramref name="mcid"/>, has arrived. None of the
    /// manifest's fields is used until it describes <paramref name="mcid"/>,
    /// as macula_manifest checks it, and its chunks describe its content whole.
    /// Each chunk fetched through <paramref name="fetchBlock"/> must then be
    /// the size its entry says and hash to its MCID, and the content is put
    /// together from the chunks that arrived, never sized from the manifest's
    /// claim alone.
    /// </summary>
    internal static async Task<byte[]> AssembleAsync(byte[] mcid, Manifest manifest, Func<byte[], CancellationToken, Task<byte[]>> fetchBlock, CancellationToken ct)
    {
        if (!ManifestBuilder.VerifyMcid(manifest, mcid))
        {
            throw new ContentTransferException(RemoteReason.HashMismatch, "the fetched manifest does not describe the requested MCID");
        }
        if (!ManifestBuilder.CheckWhole(manifest))
        {
            throw new ContentTransferException(RemoteReason.ManifestDecodeFailed, "the fetched manifest's chunks do not describe its content whole");
        }
        if (manifest.Size > (ulong)Array.MaxLength)
        {
            throw new ContentTransferException(RemoteReason.ManifestDecodeFailed, $"the fetched manifest describes {manifest.Size} bytes, more than one array can hold");
        }

        var chunks = new List<byte[]>(manifest.Chunks.Count);
        foreach (var entry in manifest.Chunks)
        {
            chunks.Add(await FetchChunkAsync(entry, fetchBlock, ct).ConfigureAwait(false));
        }
        var data = Concatenate(chunks, (int)manifest.Size);
        VerifyAssembled(manifest, data);
        return data;
    }

    /// <summary>Fetches the chunk <paramref name="entry"/> describes and checks it is the size the entry says and hashes to its MCID.</summary>
    private static async Task<byte[]> FetchChunkAsync(ChunkInfo entry, Func<byte[], CancellationToken, Task<byte[]>> fetchBlock, CancellationToken ct)
    {
        var chunkMcid = Mcid.Make(Mcid.CodecRaw, entry.Hash);
        var chunk = await fetchBlock(chunkMcid, ct).ConfigureAwait(false);
        if (chunk.Length != entry.Size || !ManifestBuilder.BlockMcid(chunk).AsSpan().SequenceEqual(chunkMcid))
        {
            throw new ContentTransferException(RemoteReason.HashMismatch, "a fetched chunk is not the size its entry says or does not hash to its MCID");
        }
        return chunk;
    }

    private static byte[] Concatenate(List<byte[]> chunks, int size)
    {
        var data = new byte[size];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(data, offset);
            offset += chunk.Length;
        }
        return data;
    }

    private static void VerifyAssembled(Manifest manifest, byte[] data)
    {
        try
        {
            ManifestBuilder.Verify(manifest, data);
        }
        catch (InvalidOperationException e)
        {
            throw new ContentTransferException(RemoteReason.VerifyFailed, $"reassembled content failed verification: {e.Message}");
        }
    }

    private static async Task PutBlockAsync(FrameStream stream, byte[] mcid, byte[] bytes, KeyPair identity, CancellationToken ct)
    {
        var payload = Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Text("mcid"), Value.Bytes(mcid)),
            new KeyValuePair<Value, Value>(Value.Text("payload"), Value.Bytes(bytes)),
        });
        var response = await CallWithRetryAsync(stream, PutBlockProc, payload, BlockTimeout, identity, ct).ConfigureAwait(false);
        switch (response)
        {
            case CallResponse.Result r when r.Payload is Value.TextValue { } t && t.AsText() == "ok":
                return;
            case CallResponse.Result r when r.Payload is Value.TextValue { } t && t.AsText() == "hash_mismatch":
                throw new ContentTransferException(RemoteReason.HashMismatch, "station reported hash_mismatch");
            case CallResponse.Result r:
                throw new ContentTransferException(RemoteReason.UnexpectedReply, "unexpected reply shape for put_block");
            case CallResponse.Error e:
                throw new ContentTransferException(RemoteReason.RemoteError, $"station returned error {e.Code} ({e.Name}): {e.Detail}", e.Code, e.Name, e.Detail);
        }
    }

    private static async Task PutManifestAsync(FrameStream stream, Manifest manifest, KeyPair identity, CancellationToken ct)
    {
        var payload = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("manifest"), ManifestBuilder.ToWire(manifest)) });
        var response = await CallWithRetryAsync(stream, PutManifestProc, payload, ManifestTimeout, identity, ct).ConfigureAwait(false);
        switch (response)
        {
            case CallResponse.Result r when r.Payload is Value.TextValue { } t && t.AsText() == "ok":
                return;
            case CallResponse.Result:
                throw new ContentTransferException(RemoteReason.UnexpectedReply, "unexpected reply shape for put_manifest");
            case CallResponse.Error e:
                throw new ContentTransferException(RemoteReason.RemoteError, $"station returned error {e.Code} ({e.Name}): {e.Detail}", e.Code, e.Name, e.Detail);
        }
    }

    private static async Task<byte[]> GetBlockAsync(FrameStream stream, byte[] mcid, KeyPair identity, CancellationToken ct)
    {
        var payload = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("mcid"), Value.Bytes(mcid)) });
        var response = await CallWithRetryAsync(stream, GetBlockProc, payload, BlockTimeout, identity, ct).ConfigureAwait(false);
        return response switch
        {
            CallResponse.Result { Payload: Value.BytesValue b } => b.Value,
            CallResponse.Result { Payload: Value.TextValue t } when t.AsText() == "not_found" =>
                throw new ContentTransferException(RemoteReason.NotFound, "station reported not_found"),
            CallResponse.Result =>
                throw new ContentTransferException(RemoteReason.UnexpectedReply, "unexpected reply shape for get_block"),
            CallResponse.Error e =>
                throw new ContentTransferException(RemoteReason.RemoteError, $"station returned error {e.Code} ({e.Name}): {e.Detail}", e.Code, e.Name, e.Detail),
            _ => throw new InvalidOperationException("unreachable"),
        };
    }

    private static async Task<Manifest> GetManifestAsync(FrameStream stream, byte[] mcid, KeyPair identity, CancellationToken ct)
    {
        var payload = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("mcid"), Value.Bytes(mcid)) });
        var response = await CallWithRetryAsync(stream, GetManifestProc, payload, ManifestTimeout, identity, ct).ConfigureAwait(false);
        switch (response)
        {
            case CallResponse.Result { Payload: Value.MapValue } r:
                try
                {
                    return ManifestBuilder.FromWire(r.Payload);
                }
                catch (ManifestParseException e)
                {
                    throw new ContentTransferException(RemoteReason.ManifestDecodeFailed, $"decoding the fetched manifest: {e.Message}");
                }
            case CallResponse.Result { Payload: Value.TextValue t } when t.AsText() == "not_found":
                throw new ContentTransferException(RemoteReason.NotFound, "station reported not_found");
            case CallResponse.Result:
                throw new ContentTransferException(RemoteReason.UnexpectedReply, "unexpected reply shape for get_manifest");
            case CallResponse.Error e:
                throw new ContentTransferException(RemoteReason.RemoteError, $"station returned error {e.Code} ({e.Name}): {e.Detail}", e.Code, e.Name, e.Detail);
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>
    /// Send one `_content.*` CALL, retrying: up to <see cref="MaxAttempts"/>
    /// total, <see cref="RetryBackoff"/> between them, only when the prior
    /// attempt's ERROR carries a BOLT#4 code flagged retryable. A
    /// non-retryable ERROR, or a RESULT (whatever its payload turns out to
    /// mean to the caller), both return on the first attempt.
    /// </summary>
    private static async Task<CallResponse> CallWithRetryAsync(FrameStream stream, string procedure, Value payload, TimeSpan timeout, KeyPair identity, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds;
            var outcome = await stream.CallAsync(procedure, ContentRealm, payload, deadlineMs, identity, timeout, ct).ConfigureAwait(false);

            var shouldRetry = attempt < MaxAttempts && outcome is CallResponse.Error e && Bolt4Code.FromValue(e.Code).IsRetryable;
            if (!shouldRetry)
            {
                return outcome;
            }
            await Task.Delay(RetryBackoff, ct).ConfigureAwait(false);
        }
    }
}
