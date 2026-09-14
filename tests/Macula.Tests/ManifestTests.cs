using Macula.Cbor;
using Macula.Content;

namespace Macula.Tests;

/// <summary>
/// Reuses macula-rust's own reference vectors for manifest.rs,
/// captured from a real `macula_manifest:create/2` via `rebar3 shell`
/// against macula-io/macula -- root hash, MCID, and the full byte-for-byte
/// `to_wire` encoding.
/// </summary>
public class ManifestTests
{
    [Fact]
    public void Even_chunk_count_matches_the_reference()
    {
        var data = "AAAABBBBCCCCD"u8.ToArray(); // 13 bytes
        var opts = new CreateOptions { Name = "test-file", ChunkSize = 4, HashAlgorithm = Algorithm.Blake3 };
        var (manifest, chunks) = ManifestBuilder.CreateWithCreated(data, opts, 0);

        Assert.Equal(4, chunks.Count);
        Assert.Equal("AAAA"u8.ToArray(), chunks[0]);
        Assert.Equal("D"u8.ToArray(), chunks[3]);

        Assert.Equal("784F87CDC9C180A21C878FC26703F9E4782F2FD2E6235048299811675E36EAC4", Convert.ToHexStringLower(manifest.RootHash).ToUpperInvariant());
        Assert.Equal("01564CC855EF538530393E36DBD4CCD216558B60F87498889890247EEB9B52B8FED7", Convert.ToHexStringLower(manifest.Mcid).ToUpperInvariant());

        Assert.Equal(0, manifest.Chunks[0].Offset);
        Assert.Equal("26C7BB3DAAAA0439EB3E5C5270E7C4DB05218D8892A0258FBD4911CEF5006D23", Convert.ToHexStringLower(manifest.Chunks[0].Hash).ToUpperInvariant());
        Assert.Equal(12, manifest.Chunks[3].Offset);
        Assert.Equal(1, manifest.Chunks[3].Size);

        var chunkMcid = ManifestBuilder.ChunkMcid(manifest, 0);
        Assert.Equal("015526C7BB3DAAAA0439EB3E5C5270E7C4DB05218D8892A0258FBD4911CEF5006D23", Convert.ToHexStringLower(chunkMcid!).ToUpperInvariant());

        ManifestBuilder.Verify(manifest, data); // does not throw
    }

    /// <summary>Odd chunk count (3) -- exercises the Merkle fold's "pair the last hash with itself" branch, which the even-count test above never touches.</summary>
    [Fact]
    public void Odd_chunk_count_matches_the_reference()
    {
        var data = "AAAABBBBCCCC"u8.ToArray(); // 12 bytes, chunk_size 4 -> exactly 3 chunks
        var opts = new CreateOptions { Name = "odd-test", ChunkSize = 4, HashAlgorithm = Algorithm.Blake3 };
        var (manifest, chunks) = ManifestBuilder.CreateWithCreated(data, opts, 0);

        Assert.Equal(3, chunks.Count);
        Assert.Equal("50FE839CCDE80B13D7531A9C34FD856DBCBBB87D8FBD241DE6AFF2C86909CD54", Convert.ToHexStringLower(manifest.RootHash).ToUpperInvariant());
        Assert.Equal("0156589728C90DB0138CA87E4E500A61812C64D30C3BE325184A761F20CA04BC86FB", Convert.ToHexStringLower(manifest.Mcid).ToUpperInvariant());

        ManifestBuilder.Verify(manifest, data); // does not throw

        var ex = Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Verify(manifest, "AAAABBBBWRONG"u8.ToArray()));
        Assert.Equal(VerifyError.SizeMismatch.ToString(), ex.Message);
    }

    [Fact]
    public void Verify_rejects_tampered_content_of_the_same_size()
    {
        var data = "AAAABBBBCCCC"u8.ToArray();
        var opts = new CreateOptions { ChunkSize = 4 };
        var (manifest, _) = ManifestBuilder.CreateWithCreated(data, opts, 0);

        var ex = Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Verify(manifest, "AAAABBBBCCCX"u8.ToArray()));
        Assert.Equal(VerifyError.RootHashMismatch.ToString(), ex.Message);
    }

    /// <summary>
    /// The full manifest map as it's actually sent in a
    /// `_content.put_manifest` CALL payload -- proves `name` really is
    /// bytes on the wire, not text.
    /// </summary>
    [Fact]
    public void ToWire_matches_the_reference_byte_for_byte()
    {
        var data = "AAAABBBBCCCC"u8.ToArray();
        var opts = new CreateOptions { Name = "odd-test", ChunkSize = 4, HashAlgorithm = Algorithm.Blake3 };
        var (manifest, _) = ManifestBuilder.CreateWithCreated(data, opts, 1_787_892_082); // 0x6A911172

        var wire = ManifestBuilder.ToWire(manifest);
        var encoded = CborCodec.Encode(wire);

        Assert.Equal(
            "AA646D63696458220156589728C90DB0138CA87E4E500A61812C64D30C3BE325184A761F20CA04BC86FB646E616D65486F64642D746573746473697A650C666368756E6B7383A46468617368582026C7BB3DAAAA0439EB3E5C5270E7C4DB05218D8892A0258FBD4911CEF5006D236473697A650465696E64657800666F666673657400A464686173685820255EC90F561EDA98B1E5E3EFA56B7B477086E273CD07CC4F780A646D052726446473697A650465696E64657801666F666673657404A464686173685820A83CE6EC6760EB7F66D3D7BBC84D1AAC3BEF0948074F8ED21423D825AE8821726473697A650465696E64657802666F66667365740867637265617465641A6A9111726776657273696F6E0169726F6F745F68617368582050FE839CCDE80B13D7531A9C34FD856DBCBBB87D8FBD241DE6AFF2C86909CD546A6368756E6B5F73697A65046B6368756E6B5F636F756E74036E686173685F616C676F726974686D66626C616B6533",
            Convert.ToHexStringLower(encoded).ToUpperInvariant());

        // Round-trip through FromWire.
        var decoded = CborCodec.Decode(encoded);
        var parsed = ManifestBuilder.FromWire(decoded);
        Assert.Equal(manifest, parsed);
    }

    [Fact]
    public void FromWire_rejects_a_missing_field()
    {
        var value = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("mcid"), Value.Bytes(new byte[34])) });
        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(value));
        Assert.Equal(FromWireError.MissingField, ex.Kind);
        Assert.Equal("version", ex.Field);
    }

    [Fact]
    public void Algorithm_from_name_defaults_to_blake3()
    {
        Assert.Equal(Algorithm.Blake3, AlgorithmExtensions.FromName("blake3"));
        Assert.Equal(Algorithm.Sha256, AlgorithmExtensions.FromName("sha256"));
        Assert.Equal(Algorithm.Blake3, AlgorithmExtensions.FromName("something-unknown"));
    }

    [Fact]
    public void Empty_data_produces_zero_chunks()
    {
        var (manifest, chunks) = ManifestBuilder.CreateWithCreated(Array.Empty<byte>(), new CreateOptions(), 0);
        Assert.Empty(chunks);
        Assert.Equal(0, manifest.ChunkCount);
    }

    /// <summary>A manifest of <paramref name="length"/> bytes cut into chunks of 4.</summary>
    private static Manifest Created(int length) =>
        ManifestBuilder.CreateWithCreated(Enumerable.Repeat((byte)7, length).ToArray(), new CreateOptions { Name = "file", ChunkSize = 4 }, 0).Item1;

    /// <summary><paramref name="manifest"/>'s wire map with one field replaced, or left out when <paramref name="value"/> is null.</summary>
    private static Value WireWith(Manifest manifest, string field, Value? value)
    {
        var without = ((Value.MapValue)ManifestBuilder.ToWire(manifest)).Without(field);
        return value is null ? without : without.WithField(field, value);
    }

    /// <summary><paramref name="manifest"/>'s wire chunk list with one field of its first chunk set to <paramref name="value"/>.</summary>
    private static Value FirstChunkWith(Manifest manifest, string field, ulong value)
    {
        var chunks = ((Value.ListValue)((Value.MapValue)ManifestBuilder.ToWire(manifest)).Get("chunks")!).Items;
        return Value.List([((Value.MapValue)chunks[0]).WithField(field, Value.UInt(value)), .. chunks.Skip(1)]);
    }

    private static Value? AlgorithmOnTheWire(string form, string name) => form switch
    {
        "missing" => null,
        "text" => Value.Text(name),
        "bytes" => Value.Bytes(System.Text.Encoding.UTF8.GetBytes(name)),
        _ => Value.UInt(1),
    };

    /// <summary>
    /// hash_algorithm is read as macula reads it: a missing one is blake3,
    /// blake3 is accepted as text or as bytes, and anything else is refused,
    /// sha256 included.
    /// </summary>
    [Theory]
    [InlineData("missing", "", true)]
    [InlineData("text", "blake3", true)]
    [InlineData("bytes", "blake3", true)]
    [InlineData("bytes", "sha256", false)]
    [InlineData("text", "md5", false)]
    [InlineData("a number", "", false)]
    public void From_wire_reads_the_hash_algorithm_as_macula_does(string form, string name, bool accepted)
    {
        var wire = WireWith(Created(13), "hash_algorithm", AlgorithmOnTheWire(form, name));

        var thrown = Record.Exception(() => Assert.Equal(Algorithm.Blake3, ManifestBuilder.FromWire(wire).HashAlgorithm));

        Assert.True(accepted == (thrown is null), $"{form} {name}: {thrown?.Message ?? "accepted"}");
        Assert.True(accepted || thrown is ManifestParseException { Field: "hash_algorithm" }, $"{form} {name}: {thrown?.GetType().Name} {thrown?.Message}");
    }

    [Fact]
    public void A_sha256_manifest_is_refused()
    {
        var wire = WireWith(Created(13), "hash_algorithm", Value.Text("sha256"));

        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(wire));

        Assert.Equal(FromWireError.InvalidValue, ex.Kind);
        Assert.Equal("hash_algorithm", ex.Field);
    }

    /// <summary>A number is checked against its field's range before it is converted, never wrapped into range.</summary>
    [Theory]
    [InlineData("version", 1UL << 32)]
    [InlineData("chunk_size", 1UL << 31)]
    [InlineData("chunk_count", 1UL << 31)]
    public void From_wire_refuses_a_number_too_large_for_its_field(string field, ulong value)
    {
        var wire = WireWith(Created(13), field, Value.UInt(value));

        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(wire));

        Assert.Equal(FromWireError.InvalidValue, ex.Kind);
        Assert.Equal(field, ex.Field);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("offset")]
    [InlineData("size")]
    public void From_wire_refuses_a_chunk_number_too_large_for_its_field(string field)
    {
        var wire = WireWith(Created(13), "chunks", FirstChunkWith(Created(13), field, 1UL << 31));

        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(wire));

        Assert.Equal(FromWireError.InvalidValue, ex.Kind);
        Assert.Equal(field, ex.Field);
    }

    [Fact]
    public void From_wire_refuses_a_name_that_is_not_utf8()
    {
        var wire = WireWith(Created(13), "name", Value.Bytes([0xFF, 0xFE]));

        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(wire));

        Assert.Equal(FromWireError.InvalidValue, ex.Kind);
        Assert.Equal("name", ex.Field);
    }

    /// <summary>
    /// A whole manifest's chunks are cut the way Create cuts content:
    /// ceil(size / chunk_size) of them, chunk i at offset i x chunk_size and
    /// chunk_size bytes long, the last holding what is left, from 1 to
    /// chunk_size bytes. FromWire refuses any other chunk list, before a caller
    /// sizes or counts anything by it.
    /// </summary>
    [Theory]
    [InlineData("a chunk cut short before the last, with the count still right")]
    [InlineData("a chunk counted that isn't listed")]
    [InlineData("chunks out of index order")]
    [InlineData("a gap between chunks")]
    [InlineData("a chunk larger than the chunk size")]
    [InlineData("a size the chunks don't add up to")]
    [InlineData("a chunk size of zero")]
    public void Check_whole_refuses_chunks_that_do_not_describe_the_content_whole(string edit)
    {
        var wire = ManifestBuilder.ToWire(NotWhole(Created(13), edit));

        var thrown = Record.Exception(() => ManifestBuilder.FromWire(wire));

        Assert.True(thrown is ManifestParseException { Kind: FromWireError.InvalidValue }, $"{edit}: {thrown?.GetType().Name ?? "accepted"} {thrown?.Message}");
    }

    private static Manifest NotWhole(Manifest m, string edit) => edit switch
    {
        "a chunk cut short before the last, with the count still right" =>
            m with { Chunks = [m.Chunks[0] with { Size = 3 }, m.Chunks[1] with { Offset = 3 }, m.Chunks[2] with { Offset = 7 }, m.Chunks[3] with { Offset = 11, Size = 2 }] },
        "a chunk counted that isn't listed" => m with { ChunkCount = m.ChunkCount + 1 },
        "chunks out of index order" => m with { Chunks = [m.Chunks[0] with { Index = 1 }, m.Chunks[1] with { Index = 0 }, m.Chunks[2], m.Chunks[3]] },
        "a gap between chunks" => m with { Chunks = [m.Chunks[0], m.Chunks[1] with { Offset = 5 }, m.Chunks[2], m.Chunks[3]] },
        "a chunk larger than the chunk size" => m with { Chunks = [m.Chunks[0], m.Chunks[1], m.Chunks[2], m.Chunks[3] with { Size = m.ChunkSize + 1 }] },
        "a size the chunks don't add up to" => m with { Size = m.Size + 1 },
        "a chunk size of zero" => m with { ChunkSize = 0 },
        _ => throw new ArgumentOutOfRangeException(nameof(edit)),
    };

    /// <summary>
    /// Empty content has one whole form, the one Create makes: size 0, no
    /// chunks and a chunk count of 0. The same content as one empty chunk is
    /// refused.
    /// </summary>
    [Fact]
    public void Empty_content_has_one_whole_form()
    {
        var empty = ManifestBuilder.CreateWithCreated([], new CreateOptions { Name = "empty" }, 0).Item1;
        var oneEmptyChunk = empty with { ChunkCount = 1, Chunks = [new ChunkInfo(0, 0, 0, Algorithm.Blake3.Hash([]))] };

        Assert.Equal(empty, ManifestBuilder.FromWire(ManifestBuilder.ToWire(empty)));
        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(ManifestBuilder.ToWire(oneEmptyChunk)));
        Assert.Equal(FromWireError.InvalidValue, ex.Kind);
    }

    [Fact]
    public void An_empty_chunk_is_not_whole()
    {
        var created = Created(13);
        var withEmptyChunk = created with
        {
            ChunkCount = created.ChunkCount + 1,
            Chunks = [.. created.Chunks, new ChunkInfo(created.ChunkCount, 13, 0, Algorithm.Blake3.Hash([]))],
        };

        var ex = Assert.Throws<ManifestParseException>(() => ManifestBuilder.FromWire(ManifestBuilder.ToWire(withEmptyChunk)));

        Assert.Equal(FromWireError.InvalidValue, ex.Kind);
    }

    /// <summary>Verify refuses a manifest whose chunk size isn't positive instead of cutting the data by it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Verify_refuses_a_chunk_size_that_is_not_positive(int chunkSize)
    {
        var manifest = Created(1) with { ChunkSize = chunkSize };

        var ex = Assert.Throws<InvalidOperationException>(() => ManifestBuilder.Verify(manifest, [7]));

        Assert.Equal(VerifyError.InvalidManifest.ToString(), ex.Message);
    }

    /// <summary>Content is hashed with blake3 only, the one algorithm every macula stack fetches.</summary>
    [Fact]
    public void Create_offers_no_hash_algorithm_but_blake3()
    {
        var ex = Assert.Throws<ArgumentException>(() => ManifestBuilder.Create([1, 2, 3], new CreateOptions { HashAlgorithm = Algorithm.Sha256 }));

        Assert.Equal("opts", ex.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_refuses_a_chunk_size_that_is_not_positive(int chunkSize)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ManifestBuilder.Create([1, 2, 3], new CreateOptions { ChunkSize = chunkSize }));

        Assert.Equal("opts", ex.ParamName);
    }
}
