using System.Security.Cryptography;
using System.Text;
using System.Text.Unicode;
using Macula.Cbor;

namespace Macula.Content;

public enum Algorithm
{
    Blake3,
    /// <summary>
    /// No longer accepted in a manifest: a content id carries no algorithm, so
    /// every macula stack fetches blake3 content only. Kept in 0.4.x so code
    /// that names it still compiles; removed in 0.5.0.
    /// </summary>
    Sha256,
}

public static class AlgorithmExtensions
{
    public static byte[] Hash(this Algorithm algorithm, byte[] data) => algorithm switch
    {
        Algorithm.Blake3 => global::Blake3.Hasher.Hash(data).AsSpan().ToArray(),
        Algorithm.Sha256 => SHA256.HashData(data),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    public static string Name(this Algorithm algorithm) => algorithm switch
    {
        Algorithm.Blake3 => "blake3",
        Algorithm.Sha256 => "sha256",
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
    };

    /// <summary>Matches `to_algorithm/1`'s own fallback: anything unrecognized defaults to blake3, it doesn't error.</summary>
    public static Algorithm FromName(string name) => name switch
    {
        "sha256" => Algorithm.Sha256,
        _ => Algorithm.Blake3,
    };
}

/// <summary>
/// A content-address: `&lt;&lt;Version:8, Codec:8, Hash:32/binary&gt;&gt;`, 34 bytes.
/// </summary>
public static class Mcid
{
    public const byte Version = 1;
    public const byte CodecRaw = 0x55;
    public const byte CodecManifest = 0x56;

    public static byte[] Make(byte codec, byte[] hash)
    {
        var outBytes = new byte[34];
        outBytes[0] = Version;
        outBytes[1] = codec;
        hash.CopyTo(outBytes, 2);
        return outBytes;
    }

    /// <summary>Whether an MCID addresses a manifest (chunked content) rather than a single raw block -- from its own codec byte, no network round trip needed.</summary>
    public static bool IsChunked(byte[] mcid) => mcid[1] == CodecManifest;
}

// Both records below carry byte[] (and, for Manifest, a List<ChunkInfo>)
// members. C#'s auto-generated record equality compares those by
// reference, not content, so a decoded manifest would never compare equal
// to the original it round-tripped from even with identical bytes --
// hence the hand-written Equals/GetHashCode pair on each.

public sealed record ChunkInfo(int Index, int Offset, int Size, byte[] Hash)
{
    public bool Equals(ChunkInfo? other) =>
        other is not null && Index == other.Index && Offset == other.Offset && Size == other.Size
        && Hash.AsSpan().SequenceEqual(other.Hash);

    public override int GetHashCode() => HashCode.Combine(Index, Offset, Size);
}

public sealed record Manifest(
    byte[] Mcid,
    uint Version,
    string Name,
    ulong Size,
    ulong Created,
    int ChunkSize,
    int ChunkCount,
    Algorithm HashAlgorithm,
    byte[] RootHash,
    IReadOnlyList<ChunkInfo> Chunks)
{
    public bool Equals(Manifest? other) =>
        other is not null
        && Mcid.AsSpan().SequenceEqual(other.Mcid)
        && Version == other.Version
        && Name == other.Name
        && Size == other.Size
        && Created == other.Created
        && ChunkSize == other.ChunkSize
        && ChunkCount == other.ChunkCount
        && HashAlgorithm == other.HashAlgorithm
        && RootHash.AsSpan().SequenceEqual(other.RootHash)
        && Chunks.SequenceEqual(other.Chunks);

    public override int GetHashCode() => HashCode.Combine(Version, Name, Size, Created, ChunkSize, ChunkCount, HashAlgorithm);
}

public sealed class CreateOptions
{
    public string Name { get; init; } = "unnamed";
    public int ChunkSize { get; init; } = ManifestBuilder.DefaultChunkSize;

    /// <summary>Must be <see cref="Algorithm.Blake3"/>, the one algorithm every macula stack fetches. Removed in 0.5.0.</summary>
    public Algorithm HashAlgorithm { get; init; } = Algorithm.Blake3;
}

public enum VerifyError
{
    SizeMismatch,
    RootHashMismatch,
    /// <summary>The manifest's chunk size is not positive, so no data can be cut by it.</summary>
    InvalidManifest,
}

public enum FromWireError
{
    MissingField,
    WrongFieldType,
    /// <summary>
    /// The field has the right type but a value no manifest can have: a
    /// number out of range, a name that isn't UTF-8, a hash algorithm other
    /// than blake3, or chunks that don't describe whole content.
    /// </summary>
    InvalidValue,
}

public sealed class ManifestParseException : Exception
{
    public FromWireError Kind { get; }
    public string Field { get; }

    public ManifestParseException(FromWireError kind, string field) : base($"{kind}: {field}")
    {
        Kind = kind;
        Field = field;
    }
}

/// <summary>
/// Fixed-size chunking, Merkle-root computation, and manifest construction
/// for content larger than one storage block. Ported from macula's own
/// `macula_manifest` (SDK).
/// </summary>
public static class ManifestBuilder
{
    /// <summary>256 KiB -- matches `macula_manifest:default_chunk_size/0`.</summary>
    public const int DefaultChunkSize = 262_144;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Split <paramref name="data"/> into fixed-size chunks and build its
    /// manifest. Returns the manifest and the chunk bytes in order (index 0
    /// first) -- a caller uploads each chunk (`_content.put_block`) then the
    /// manifest itself (`_content.put_manifest`). The chunk size must be
    /// positive and the hash algorithm blake3.
    /// </summary>
    public static (Manifest Manifest, IReadOnlyList<byte[]> Chunks) Create(byte[] data, CreateOptions opts) =>
        CreateWithCreated(data, opts, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    internal static (Manifest, IReadOnlyList<byte[]>) CreateWithCreated(byte[] data, CreateOptions opts, ulong created)
    {
        if (opts.HashAlgorithm != Algorithm.Blake3)
        {
            throw new ArgumentException("content is hashed with blake3 only, the one algorithm every macula stack fetches", nameof(opts));
        }
        if (opts.ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(opts), opts.ChunkSize, "the chunk size must be positive");
        }

        var chunks = DoChunk(data, opts.ChunkSize);
        var chunkInfos = ChunkInfos(chunks, opts.HashAlgorithm);
        var rootHash = RootHashFor(chunkInfos, opts.HashAlgorithm);
        var chunkCount = chunkInfos.Count;
        var mcid = ComputeMcid(opts.Name, (ulong)data.Length, opts.ChunkSize, chunkCount, opts.HashAlgorithm, rootHash);

        var manifest = new Manifest(
            Mcid: mcid,
            Version: 1,
            Name: opts.Name,
            Size: (ulong)data.Length,
            Created: created,
            ChunkSize: opts.ChunkSize,
            ChunkCount: chunkCount,
            HashAlgorithm: opts.HashAlgorithm,
            RootHash: rootHash,
            Chunks: chunkInfos);
        return (manifest, chunks);
    }

    /// <summary>The MCID a chunk at <paramref name="index"/> is stored/fetched under -- the station derives this same value independently.</summary>
    public static byte[]? ChunkMcid(Manifest manifest, int index) =>
        index >= 0 && index < manifest.Chunks.Count ? Content.Mcid.Make(Content.Mcid.CodecRaw, manifest.Chunks[index].Hash) : null;

    /// <summary>
    /// The MCID a whole blob is stored/fetched under when it's small enough
    /// to be a single block (no manifest at all). Matches
    /// `macula_content_transfer:put_single_block/3` exactly: ALWAYS BLAKE3,
    /// regardless of any algorithm preference.
    /// </summary>
    public static byte[] BlockMcid(byte[] data) => Content.Mcid.Make(Content.Mcid.CodecRaw, Algorithm.Blake3.Hash(data));

    /// <summary>
    /// Verify reassembled <paramref name="data"/> against <paramref name="manifest"/>:
    /// size, then a fresh Merkle root over data re-chunked the same way. A
    /// manifest whose chunk size isn't positive is refused with
    /// <see cref="VerifyError.InvalidManifest"/> instead of cutting data by it.
    /// </summary>
    public static void Verify(Manifest manifest, byte[] data)
    {
        if (manifest.ChunkSize <= 0)
        {
            throw new InvalidOperationException(VerifyError.InvalidManifest.ToString());
        }
        if ((ulong)data.Length != manifest.Size)
        {
            throw new InvalidOperationException(VerifyError.SizeMismatch.ToString());
        }
        var chunks = DoChunk(data, manifest.ChunkSize);
        var infos = ChunkInfos(chunks, manifest.HashAlgorithm);
        var actualRoot = RootHashFor(infos, manifest.HashAlgorithm);
        if (!actualRoot.AsSpan().SequenceEqual(manifest.RootHash))
        {
            throw new InvalidOperationException(VerifyError.RootHashMismatch.ToString());
        }
    }

    /// <summary>
    /// Whether <paramref name="manifest"/> describes <paramref name="mcid"/>,
    /// as macula_manifest's verify_mcid/2 decides it: its hash algorithm is
    /// blake3, its name has a UTF-8 form, and the MCID recomputed from its
    /// canonical fields (<see cref="McidFor"/>) equals <paramref name="mcid"/>.
    /// Its own mcid field, created time, version and chunk list play no part.
    /// </summary>
    internal static bool VerifyMcid(Manifest manifest, byte[] mcid) =>
        manifest.HashAlgorithm == Algorithm.Blake3
        && HasUtf8Form(manifest.Name)
        && McidFor(manifest).AsSpan().SequenceEqual(mcid);

    /// <summary>
    /// Whether <paramref name="manifest"/>'s chunks describe its content whole,
    /// cut the way <see cref="Create"/> cuts content: a positive chunk size;
    /// ceil(size / chunk size) chunks, which is the chunk count; chunk i with
    /// index i at offset i x chunk size and chunk size bytes long, except the
    /// last, which holds what is left, from 1 up to the chunk size; and every
    /// hash 32 bytes. Empty content is whole only with no chunks at all.
    /// </summary>
    internal static bool CheckWhole(Manifest manifest)
    {
        if (manifest.ChunkSize <= 0 || manifest.ChunkCount != manifest.Chunks.Count || manifest.RootHash.Length != 32)
        {
            return false;
        }
        return (ulong)manifest.ChunkCount == ChunksFor(manifest.Size, manifest.ChunkSize)
            && manifest.Chunks.Select((chunk, index) => CutAt(chunk, index, manifest)).All(cut => cut);
    }

    /// <summary>How many chunks of <paramref name="chunkSize"/> bytes <paramref name="size"/> bytes make: ceil(size / chunk size), 0 for no bytes.</summary>
    private static ulong ChunksFor(ulong size, int chunkSize) =>
        size / (ulong)chunkSize + Math.Min(size % (ulong)chunkSize, 1UL);

    /// <summary>
    /// Whether <paramref name="chunk"/> is chunk <paramref name="index"/> of
    /// <paramref name="manifest"/> as Create cuts it. Relies on the manifest
    /// already having ceil(size / chunk size) chunks, so the chunk starts
    /// inside the content.
    /// </summary>
    private static bool CutAt(ChunkInfo chunk, int index, Manifest manifest)
    {
        var offset = (ulong)index * (ulong)manifest.ChunkSize;
        var size = Math.Min((ulong)manifest.ChunkSize, manifest.Size - offset);
        return chunk.Index == index && chunk.Offset >= 0 && (ulong)chunk.Offset == offset
            && chunk.Size >= 0 && (ulong)chunk.Size == size && chunk.Hash.Length == 32;
    }

    private static bool HasUtf8Form(string text)
    {
        try
        {
            StrictUtf8.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static List<byte[]> DoChunk(byte[] data, int chunkSize)
    {
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var size = Math.Min(chunkSize, data.Length - offset);
            chunks.Add(data.AsSpan(offset, size).ToArray());
        }
        return chunks;
    }

    private static List<ChunkInfo> ChunkInfos(IReadOnlyList<byte[]> chunks, Algorithm algorithm)
    {
        var infos = new List<ChunkInfo>(chunks.Count);
        var offset = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            infos.Add(new ChunkInfo(index, offset, chunk.Length, algorithm.Hash(chunk)));
            offset += chunk.Length;
        }
        return infos;
    }

    private static byte[] RootHashFor(IReadOnlyList<ChunkInfo> infos, Algorithm algorithm)
    {
        if (infos.Count == 0)
        {
            return algorithm.Hash(Array.Empty<byte>());
        }
        var hashes = infos.Select(i => i.Hash).ToList();
        while (hashes.Count > 1)
        {
            hashes = Combine(hashes, algorithm);
        }
        return hashes[0];
    }

    /// <summary>One Merkle-fold pass: pairs from the front, hash(L || R). An odd leftover at the end is paired with itself, hash(Last || Last).</summary>
    private static List<byte[]> Combine(List<byte[]> hashes, Algorithm algorithm)
    {
        var result = new List<byte[]>((hashes.Count + 1) / 2);
        for (var i = 0; i < hashes.Count; i += 2)
        {
            var left = hashes[i];
            var right = i + 1 < hashes.Count ? hashes[i + 1] : hashes[i];
            var buf = new byte[left.Length + right.Length];
            left.CopyTo(buf, 0);
            right.CopyTo(buf, left.Length);
            result.Add(algorithm.Hash(buf));
        }
        return result;
    }

    /// <summary>
    /// The MCID <paramref name="manifest"/>'s canonical fields describe: its
    /// name, size, chunk_size, chunk_count, hash_algorithm and root_hash. Its
    /// own mcid field plays no part.
    /// </summary>
    internal static byte[] McidFor(Manifest manifest) =>
        ComputeMcid(manifest.Name, manifest.Size, manifest.ChunkSize, manifest.ChunkCount, manifest.HashAlgorithm, manifest.RootHash);

    /// <summary>
    /// The canonical hash input for a manifest's own MCID -- deliberately
    /// excludes `created` and `chunks` (already rolled up into `root_hash`).
    /// `name` is wrapped as CBOR text here specifically -- NOT the same
    /// encoding <see cref="ToWire"/> uses for `name` (bytes there).
    /// </summary>
    private static byte[] ComputeMcid(string name, ulong size, int chunkSize, int chunkCount, Algorithm algorithm, byte[] rootHash)
    {
        var canonical = Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Text("name"), Value.Text(name)),
            new KeyValuePair<Value, Value>(Value.Text("size"), Value.UInt(size)),
            new KeyValuePair<Value, Value>(Value.Text("chunk_size"), Value.UInt((ulong)chunkSize)),
            new KeyValuePair<Value, Value>(Value.Text("chunk_count"), Value.UInt((ulong)chunkCount)),
            new KeyValuePair<Value, Value>(Value.Text("hash_algorithm"), Value.Text(algorithm.Name())),
            new KeyValuePair<Value, Value>(Value.Text("root_hash"), Value.Bytes(rootHash)),
        });
        var bytes = CborCodec.Encode(canonical);
        var hash = algorithm.Hash(bytes);
        return Content.Mcid.Make(Content.Mcid.CodecManifest, hash);
    }

    /// <summary>
    /// Encode <paramref name="manifest"/> as it's actually sent in a
    /// `_content.put_manifest` CALL payload -- `name` as bytes (its real
    /// `binary()` type), NOT the text-wrapped form <see cref="ComputeMcid"/>
    /// uses internally.
    /// </summary>
    public static Value ToWire(Manifest manifest) =>
        Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Text("mcid"), Value.Bytes(manifest.Mcid)),
            new KeyValuePair<Value, Value>(Value.Text("version"), Value.UInt(manifest.Version)),
            new KeyValuePair<Value, Value>(Value.Text("name"), Value.Bytes(Encoding.UTF8.GetBytes(manifest.Name))),
            new KeyValuePair<Value, Value>(Value.Text("size"), Value.UInt(manifest.Size)),
            new KeyValuePair<Value, Value>(Value.Text("created"), Value.UInt(manifest.Created)),
            new KeyValuePair<Value, Value>(Value.Text("chunk_size"), Value.UInt((ulong)manifest.ChunkSize)),
            new KeyValuePair<Value, Value>(Value.Text("chunk_count"), Value.UInt((ulong)manifest.ChunkCount)),
            new KeyValuePair<Value, Value>(Value.Text("hash_algorithm"), Value.Text(manifest.HashAlgorithm.Name())),
            new KeyValuePair<Value, Value>(Value.Text("root_hash"), Value.Bytes(manifest.RootHash)),
            new KeyValuePair<Value, Value>(Value.Text("chunks"), Value.List(manifest.Chunks.Select(ChunkInfoToWire).ToList())),
        });

    private static Value ChunkInfoToWire(ChunkInfo info) =>
        Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Text("index"), Value.UInt((ulong)info.Index)),
            new KeyValuePair<Value, Value>(Value.Text("offset"), Value.UInt((ulong)info.Offset)),
            new KeyValuePair<Value, Value>(Value.Text("size"), Value.UInt((ulong)info.Size)),
            new KeyValuePair<Value, Value>(Value.Text("hash"), Value.Bytes(info.Hash)),
        });

    /// <summary>
    /// Parse a manifest as received from a `_content.get_manifest` RESULT.
    /// A missing hash_algorithm is blake3 and any other algorithm is refused;
    /// every number is checked against its field's range before it is
    /// converted; the name must be UTF-8; and the chunks must describe the
    /// content whole (<see cref="CheckWhole"/>), all before a caller sizes or
    /// counts anything by them. A value no manifest can have is
    /// <see cref="FromWireError.InvalidValue"/>.
    /// </summary>
    public static Manifest FromWire(Value value)
    {
        if (value is not Value.MapValue map)
        {
            throw new ManifestParseException(FromWireError.WrongFieldType, "<root>");
        }

        var mcid = GetBytesExact(map, "mcid", 34);
        var version = GetUInt32(map, "version");
        var name = GetName(map, "name");
        var size = GetUInt(map, "size");
        var created = GetUInt(map, "created");
        var chunkSize = GetInt(map, "chunk_size");
        var chunkCount = GetInt(map, "chunk_count");
        var hashAlgorithm = GetHashAlgorithm(map, "hash_algorithm");
        var rootHash = GetBytesExact(map, "root_hash", 32);

        var chunks = map.Get("chunks") switch
        {
            Value.ListValue list => list.Items.Select(ChunkInfoFromWire).ToList(),
            null => throw new ManifestParseException(FromWireError.MissingField, "chunks"),
            _ => throw new ManifestParseException(FromWireError.WrongFieldType, "chunks"),
        };

        var manifest = new Manifest(mcid, version, name, size, created, chunkSize, chunkCount, hashAlgorithm, rootHash, chunks);
        return CheckWhole(manifest) ? manifest : throw new ManifestParseException(FromWireError.InvalidValue, "chunks");
    }

    private static ChunkInfo ChunkInfoFromWire(Value value)
    {
        if (value is not Value.MapValue map)
        {
            throw new ManifestParseException(FromWireError.WrongFieldType, "chunks[]");
        }
        return new ChunkInfo(
            GetInt(map, "index"),
            GetInt(map, "offset"),
            GetInt(map, "size"),
            GetBytesExact(map, "hash", 32));
    }

    private static ulong GetUInt(Value.MapValue map, string field) => map.Get(field) switch
    {
        Value.UIntValue u => u.Value,
        null => throw new ManifestParseException(FromWireError.MissingField, field),
        _ => throw new ManifestParseException(FromWireError.WrongFieldType, field),
    };

    /// <summary>A uint field that must fit an int, checked before it is converted.</summary>
    private static int GetInt(Value.MapValue map, string field)
    {
        var number = GetUInt(map, field);
        return number <= int.MaxValue ? (int)number : throw new ManifestParseException(FromWireError.InvalidValue, field);
    }

    /// <summary>A uint field that must fit 32 bits, checked before it is converted.</summary>
    private static uint GetUInt32(Value.MapValue map, string field)
    {
        var number = GetUInt(map, field);
        return number <= uint.MaxValue ? (uint)number : throw new ManifestParseException(FromWireError.InvalidValue, field);
    }

    /// <summary>
    /// The name, sent as bytes, which must be UTF-8: decoding it leniently
    /// would put another name in the manifest than the one that was sent.
    /// </summary>
    private static string GetName(Value.MapValue map, string field) => map.Get(field) switch
    {
        Value.BytesValue b when Utf8.IsValid(b.Value) => Encoding.UTF8.GetString(b.Value),
        Value.BytesValue => throw new ManifestParseException(FromWireError.InvalidValue, field),
        null => throw new ManifestParseException(FromWireError.MissingField, field),
        _ => throw new ManifestParseException(FromWireError.WrongFieldType, field),
    };

    /// <summary>
    /// The hash algorithm as macula reads it: a missing one is blake3, and
    /// blake3 is accepted as text or as bytes. Any other name, sha256
    /// included, is refused, since a content id carries no algorithm and only
    /// blake3 content is ever fetched.
    /// </summary>
    private static Algorithm GetHashAlgorithm(Value.MapValue map, string field) => map.Get(field) switch
    {
        null => Algorithm.Blake3,
        Value.TextValue t when IsBlake3(t.Utf8) => Algorithm.Blake3,
        Value.BytesValue b when IsBlake3(b.Value) => Algorithm.Blake3,
        Value.TextValue or Value.BytesValue => throw new ManifestParseException(FromWireError.InvalidValue, field),
        _ => throw new ManifestParseException(FromWireError.WrongFieldType, field),
    };

    private static bool IsBlake3(byte[] name) => name.AsSpan().SequenceEqual("blake3"u8);

    private static byte[] GetBytesExact(Value.MapValue map, string field, int length) => map.Get(field) switch
    {
        Value.BytesValue b when b.Value.Length == length => b.Value,
        null => throw new ManifestParseException(FromWireError.MissingField, field),
        _ => throw new ManifestParseException(FromWireError.WrongFieldType, field),
    };
}
