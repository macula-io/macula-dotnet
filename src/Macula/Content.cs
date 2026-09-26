using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>Bounds on a content fetch. Each left at its default takes macula's.</summary>
public sealed class ContentOptions
{
    /// <summary>The most bytes the content may have (256 MiB by default).</summary>
    public ulong MaxBytes { get; init; }

    /// <summary>The most chunks it may have (16384 by default).</summary>
    public int MaxChunks { get; init; }

    /// <summary>How many chunks are fetched at once (4 by default).</summary>
    public int Parallel { get; init; }

    /// <summary>How long one chunk may take (15 s by default).</summary>
    public TimeSpan ChunkTimeout { get; init; }

    internal string? ToJson()
    {
        var json = new JsonObject();
        if (MaxBytes != 0) json["max_bytes"] = MaxBytes;
        if (MaxChunks != 0) json["max_chunks"] = MaxChunks;
        if (Parallel != 0) json["parallel"] = Parallel;
        if (ChunkTimeout != TimeSpan.Zero) json["chunk_timeout_ms"] = Pool.Milliseconds(ChunkTimeout);
        return json.Count == 0 ? null : json.ToJsonString();
    }
}

public sealed partial class Pool
{
    /// <summary>
    /// Shares <paramref name="data"/> in <paramref name="realm"/> from this node: it keeps the bytes,
    /// serves them on its own <c>~&lt;node id&gt;/content_v1</c> and announces them, for as long as the pool
    /// is open or until <see cref="UnshareContentAsync"/>. Stations keep no content. Anyone who learns the
    /// returned MCID can fetch the bytes. <paramref name="name"/> is carried in the manifest of content
    /// over 256 KiB, and is part of its MCID.
    /// </summary>
    public unsafe Task<Mcid> ShareContentAsync(MeshId realm, ReadOnlyMemory<byte> data, string? name = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            Span<byte> mcid = stackalloc byte[Mcid.Size];
            nint err = 0;
            fixed (byte* r = realm.Bytes, d = data.Span, m = mcid)
            {
                Libmacula.macula_pool_share_content(Handle, r, d, (nuint)data.Length, name, timeoutMs, token, m, ref err);
            }
            NativeCall.Check(err, cancellationToken);
            return new Mcid(mcid);
        }, cancellationToken);
    }

    /// <summary>Stops sharing <paramref name="mcid"/> in <paramref name="realm"/>, and withdraws its announcement.</summary>
    public unsafe Task UnshareContentAsync(MeshId realm, Mcid mcid, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            fixed (byte* r = realm.Bytes, m = mcid.Bytes)
            {
                Libmacula.macula_pool_unshare_content(Handle, r, m, timeoutMs, token, ref err);
            }
            NativeCall.Check(err, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Fetches <paramref name="mcid"/> in <paramref name="realm"/> from the nodes that share it, checking
    /// every block against it, so no sharer is trusted. Throws <see cref="NotSharedException"/> when no
    /// node shares it, and <see cref="ContentUnavailableException"/> when every sharer failed.
    /// </summary>
    public unsafe Task<byte[]> GetContentAsync(MeshId realm, Mcid mcid, ContentOptions? options = null,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var optionsJson = options?.ToJson();
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            nint bytes;
            nuint length;
            fixed (byte* r = realm.Bytes, m = mcid.Bytes)
            {
                bytes = Libmacula.macula_pool_get_content(Handle, r, m, optionsJson, timeoutMs, token, out length, ref err);
            }
            NativeCall.Check(err, cancellationToken);
            return NativeCall.TakeBytes(bytes, length);
        }, cancellationToken);
    }
}
