using Macula.Content;

namespace Macula.Tests;

/// <summary>
/// The part of a chunked get that runs once the manifest has arrived
/// (<c>ContentTransfer.AssembleAsync</c>), with blocks served from memory, so
/// nothing touches the network. Test names match macula-go's, and Pluto's for
/// the sha256 rule.
/// </summary>
public class ContentAssembleTests
{
    /// <summary>Content of three chunks filled from <paramref name="seed"/>, with its manifest and its blocks by MCID.</summary>
    private static (Manifest Manifest, Dictionary<string, byte[]> Blocks, byte[] Data) ChunkedContent(int seed)
    {
        var data = Enumerable.Range(0, 3 * ManifestBuilder.DefaultChunkSize - 1).Select(i => (byte)(i % seed)).ToArray();
        var (manifest, chunks) = ManifestBuilder.Create(data, new CreateOptions());
        return (manifest, chunks.ToDictionary(chunk => Convert.ToHexString(ManifestBuilder.BlockMcid(chunk))), data);
    }

    /// <summary>Serves blocks by MCID from memory and counts every fetch.</summary>
    private sealed class BlockServer(Dictionary<string, byte[]> blocks)
    {
        public int Fetched { get; private set; }

        public Task<byte[]> FetchAsync(byte[] mcid, CancellationToken ct)
        {
            Fetched++;
            return blocks.TryGetValue(Convert.ToHexString(mcid), out var block)
                ? Task.FromResult(block)
                : Task.FromException<byte[]>(new KeyNotFoundException("no block for this mcid"));
        }
    }

    private static Manifest WithOwnMcid(Manifest manifest) => manifest with { Mcid = ManifestBuilder.McidFor(manifest) };

    private static Task<ContentTransfer.ContentTransferException> RefusedAsync(byte[] requested, Manifest manifest, BlockServer server) =>
        Assert.ThrowsAsync<ContentTransfer.ContentTransferException>(
            () => ContentTransfer.AssembleAsync(requested, manifest, server.FetchAsync, CancellationToken.None));

    [Fact]
    public async Task Get_assembles_the_content_a_whole_manifest_for_its_mcid_describes()
    {
        var (manifest, blocks, data) = ChunkedContent(251);

        var assembled = await ContentTransfer.AssembleAsync(manifest.Mcid, manifest, new BlockServer(blocks).FetchAsync, CancellationToken.None);

        Assert.Equal(data, assembled);
    }

    /// <summary>
    /// A manifest that doesn't describe the MCID asked for is refused before
    /// any chunk is fetched, even one that is consistent with its own chunks.
    /// </summary>
    [Fact]
    public async Task Get_refuses_a_manifest_that_does_not_describe_the_requested_mcid()
    {
        var (requested, _, _) = ChunkedContent(251);
        var (substitute, blocks, _) = ChunkedContent(241);
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(requested.Mcid, substitute, server);

        Assert.Equal(ContentTransfer.RemoteReason.HashMismatch, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }

    /// <summary>A manifest for its own MCID that counts more chunks than it lists is refused as not whole.</summary>
    [Fact]
    public async Task Get_refuses_a_manifest_counting_more_chunks_than_it_lists()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var manifest = WithOwnMcid(created with { ChunkCount = created.ChunkCount + 5 });
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(manifest.Mcid, manifest, server);

        Assert.Equal(ContentTransfer.RemoteReason.ManifestDecodeFailed, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }

    /// <summary>
    /// A manifest for its own MCID that claims more bytes than its chunks hold
    /// is refused as not whole, before anything is sized from its claim.
    /// </summary>
    [Fact]
    public async Task Get_refuses_a_manifest_claiming_more_bytes_than_its_chunks_hold()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var manifest = WithOwnMcid(created with { Size = 1UL << 62 });
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(manifest.Mcid, manifest, server);

        Assert.Equal(ContentTransfer.RemoteReason.ManifestDecodeFailed, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }

    /// <summary>
    /// A manifest for its own MCID with a chunk size of zero is refused as not
    /// whole, before its content is cut by that size.
    /// </summary>
    [Fact]
    public async Task Get_refuses_a_manifest_with_a_chunk_size_of_zero()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var manifest = WithOwnMcid(created with { ChunkSize = 0 });
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(manifest.Mcid, manifest, server);

        Assert.Equal(ContentTransfer.RemoteReason.ManifestDecodeFailed, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }

    /// <summary>A block that hashes to its chunk's MCID but isn't the size its entry says is refused.</summary>
    [Fact]
    public async Task Get_refuses_a_chunk_that_is_not_the_size_its_entry_says()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var oversized = new byte[created.ChunkSize + 1];
        blocks[Convert.ToHexString(ManifestBuilder.BlockMcid(oversized))] = oversized;
        var lastIndex = created.Chunks.Count - 1;
        var last = created.Chunks[lastIndex] with { Hash = Algorithm.Blake3.Hash(oversized) };
        IReadOnlyList<ChunkInfo> chunks = [.. created.Chunks.Take(lastIndex), last];
        var manifest = WithOwnMcid(created with { Chunks = chunks, RootHash = ManifestBuilder.RootHashFor(chunks, Algorithm.Blake3) });

        var ex = await RefusedAsync(manifest.Mcid, manifest, new BlockServer(blocks));

        Assert.Equal(ContentTransfer.RemoteReason.HashMismatch, ex.Reason);
    }

    /// <summary>
    /// A manifest whose chunk hashes don't combine to its root hash is refused
    /// before any chunk is fetched, even though it describes the MCID asked
    /// for: the root hash is part of the MCID, and the chunk hashes are not.
    /// </summary>
    [Fact]
    public async Task Get_refuses_a_manifest_whose_chunk_hashes_do_not_make_its_root_hash()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var flipped = created.Chunks[1] with { Hash = created.Chunks[1].Hash.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray() };
        var manifest = created with { Chunks = [created.Chunks[0], flipped, created.Chunks[2]] };
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(manifest.Mcid, manifest, server);

        Assert.Equal(ContentTransfer.RemoteReason.HashMismatch, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }

    /// <summary>
    /// A whole manifest for its own MCID whose content is longer than one
    /// array can hold is refused before any chunk is fetched.
    /// </summary>
    [Fact]
    public async Task Get_refuses_content_larger_than_one_array_can_hold()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var manifest = WithOwnMcid(created with
        {
            Size = (ulong)int.MaxValue + 101,
            ChunkSize = int.MaxValue,
            ChunkCount = 2,
            Chunks = [new ChunkInfo(0, 0, int.MaxValue, created.Chunks[0].Hash), new ChunkInfo(1, int.MaxValue, 101, created.Chunks[1].Hash)],
        });
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(manifest.Mcid, manifest, server);

        Assert.Equal(ContentTransfer.RemoteReason.ManifestDecodeFailed, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }

    /// <summary>
    /// A manifest describes an MCID when its name, size, chunk_size,
    /// chunk_count, hash_algorithm and root_hash recompute to it, its name has
    /// a UTF-8 form and its algorithm is blake3. Its created time, version and
    /// own mcid field play no part. One that doesn't describe the MCID asked
    /// for is refused before any chunk is fetched.
    /// </summary>
    [Theory]
    [InlineData("another created time, version and own mcid", true)]
    [InlineData("another name", false)]
    [InlineData("another size", false)]
    [InlineData("another chunk count", false)]
    [InlineData("another root hash", false)]
    [InlineData("a name with no UTF-8 form, for its own MCID", false)]
    [InlineData("an unknown hash algorithm", false)]
    public async Task Verify_mcid_checks_the_fields_macula_checks(string edit, bool describes)
    {
        var (created, blocks, _) = ChunkedContent(251);
        var (manifest, requested) = McidEdit(created, edit);
        var server = new BlockServer(blocks);

        var thrown = await Record.ExceptionAsync(() => ContentTransfer.AssembleAsync(requested, manifest, server.FetchAsync, CancellationToken.None));

        Assert.True(describes == (thrown is null), $"{edit}: {thrown?.Message ?? "assembled"}");
        Assert.True(
            describes || (thrown is ContentTransfer.ContentTransferException { Reason: ContentTransfer.RemoteReason.HashMismatch } && server.Fetched == 0),
            $"{edit}: {thrown?.GetType().Name} {thrown?.Message} after {server.Fetched} fetches");
    }

    private static (Manifest Manifest, byte[] Requested) McidEdit(Manifest created, string edit) => edit switch
    {
        "another created time, version and own mcid" => (created with { Created = created.Created + 1, Version = 9, Mcid = new byte[34] }, created.Mcid),
        "another name" => (created with { Name = "other" }, created.Mcid),
        "another size" => (created with { Size = created.Size + 1 }, created.Mcid),
        "another chunk count" => (created with { ChunkCount = created.ChunkCount + 1 }, created.Mcid),
        "another root hash" => (created with { RootHash = created.RootHash.Select((b, i) => i == 0 ? (byte)(b ^ 1) : b).ToArray() }, created.Mcid),
        "a name with no UTF-8 form, for its own MCID" => ForItsOwnMcid(created with { Name = "\uD800" }),
        "an unknown hash algorithm" => (created with { HashAlgorithm = (Algorithm)7 }, created.Mcid),
        _ => throw new ArgumentOutOfRangeException(nameof(edit)),
    };

    private static (Manifest Manifest, byte[] Requested) ForItsOwnMcid(Manifest manifest) => (manifest, ManifestBuilder.McidFor(manifest));

    /// <summary>A sha256 manifest never describes an MCID, even its own, so nothing of it is fetched.</summary>
    [Fact]
    public async Task Verify_mcid_refuses_sha256()
    {
        var (created, blocks, _) = ChunkedContent(251);
        var manifest = WithOwnMcid(created with { HashAlgorithm = Algorithm.Sha256 });
        var server = new BlockServer(blocks);

        var ex = await RefusedAsync(manifest.Mcid, manifest, server);

        Assert.Equal(ContentTransfer.RemoteReason.HashMismatch, ex.Reason);
        Assert.Equal(0, server.Fetched);
    }
}
