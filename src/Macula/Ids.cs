namespace Macula;

/// <summary>
/// A 32-byte mesh id: a node id, a realm id, a station id or a DHT record key. Written as 64 lowercase
/// hex characters.
/// </summary>
public readonly struct MeshId : IEquatable<MeshId>
{
    private readonly byte[]? _bytes;

    /// <summary>An id of exactly 32 bytes.</summary>
    public MeshId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 32)
        {
            throw new ArgumentException($"a mesh id is 32 bytes, not {bytes.Length}", nameof(bytes));
        }
        _bytes = bytes.ToArray();
    }

    /// <summary>The id's bytes (32 zero bytes for <c>default</c>).</summary>
    public ReadOnlySpan<byte> Bytes => _bytes ?? new byte[32];

    /// <summary>The id of 64 hex characters.</summary>
    public static MeshId Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 64)
        {
            throw new FormatException($"a mesh id is 64 hex characters, not {hex.Length}");
        }
        return new MeshId(Convert.FromHexString(hex));
    }

    /// <inheritdoc/>
    public override string ToString() => Convert.ToHexStringLower(Bytes);

    /// <inheritdoc/>
    public bool Equals(MeshId other) => Bytes.SequenceEqual(other.Bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MeshId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>Whether two ids are the same.</summary>
    public static bool operator ==(MeshId left, MeshId right) => left.Equals(right);

    /// <summary>Whether two ids differ.</summary>
    public static bool operator !=(MeshId left, MeshId right) => !left.Equals(right);
}

/// <summary>
/// A content id (MCID): 50 bytes, <c>&lt;&lt;2, codec, SHA-384&gt;&gt;</c>, written as 100 lowercase hex
/// characters. It names content by what it is, so every byte fetched is checked against it.
/// </summary>
public readonly struct Mcid : IEquatable<Mcid>
{
    /// <summary>An MCID's length in bytes.</summary>
    public const int Size = 50;

    private readonly byte[]? _bytes;

    /// <summary>An MCID of exactly 50 bytes.</summary>
    public Mcid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"an MCID is {Size} bytes, not {bytes.Length}", nameof(bytes));
        }
        _bytes = bytes.ToArray();
    }

    /// <summary>The MCID's bytes.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes ?? new byte[Size];

    /// <summary>The MCID of 100 hex characters.</summary>
    public static Mcid Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        if (hex.Length != 2 * Size)
        {
            throw new FormatException($"an MCID is {2 * Size} hex characters, not {hex.Length}");
        }
        return new Mcid(Convert.FromHexString(hex));
    }

    /// <inheritdoc/>
    public override string ToString() => Convert.ToHexStringLower(Bytes);

    /// <inheritdoc/>
    public bool Equals(Mcid other) => Bytes.SequenceEqual(other.Bytes);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Mcid other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>Whether two MCIDs are the same.</summary>
    public static bool operator ==(Mcid left, Mcid right) => left.Equals(right);

    /// <summary>Whether two MCIDs differ.</summary>
    public static bool operator !=(Mcid left, Mcid right) => !left.Equals(right);
}

/// <summary>A node's crypto profile. A realm runs one, and every node in it uses that one.</summary>
public enum Profile
{
    /// <summary>
    /// The composite ML-DSA-87 + RSA-PSS-4096 (LAMPS <c>id-MLDSA87-RSA4096-PSS-SHA512</c>): the fleet's
    /// profile, and the default.
    /// </summary>
    PqHybrid,

    /// <summary>ML-DSA-87 alone (CNSA 2.0).</summary>
    PqPure,
}

internal static class Profiles
{
    internal static string Name(Profile profile) => profile switch
    {
        Profile.PqHybrid => "pq_hybrid",
        Profile.PqPure => "pq_pure",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static Profile Parse(string name) => name switch
    {
        "pq_hybrid" => Profile.PqHybrid,
        "pq_pure" => Profile.PqPure,
        _ => throw new MaculaException(ErrorKind.Failed, $"libmacula named an unknown profile {name}"),
    };
}
