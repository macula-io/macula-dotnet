using Macula.Native;

namespace Macula;

/// <summary>
/// A node's identity key: ML-DSA-87, or the composite ML-DSA-87 + RSA-PSS-4096 in
/// <see cref="Profile.PqHybrid"/>. Its node id solves the mesh's admission puzzle. A key file is
/// readable by its owner only; one others can read is refused.
/// </summary>
public sealed class NodeKey : IDisposable
{
    internal NodeKey(KeyHandle handle) => Handle = handle;

    internal KeyHandle Handle { get; }

    /// <summary>A new key in <paramref name="profile"/>. Solving the puzzle takes a second or so.</summary>
    public static async Task<NodeKey> GenerateAsync(Profile profile = Profile.PqHybrid,
        CancellationToken cancellationToken = default)
    {
        var name = Profiles.Name(profile);
        return new NodeKey(await NativeCall.RunAsync(token =>
        {
            nint err = 0;
            var handle = Libmacula.macula_key_generate(name, token, ref err);
            NativeCall.Check(err, cancellationToken);
            return handle;
        }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>The key in the file at <paramref name="path"/>.</summary>
    public static NodeKey Load(string path, Profile profile = Profile.PqHybrid)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeCall.EnsureLoaded();
        nint err = 0;
        var handle = Libmacula.macula_key_load(path, Profiles.Name(profile), ref err);
        NativeCall.Check(err);
        return new NodeKey(handle);
    }

    /// <summary>
    /// The key in the file at <paramref name="path"/>, or, when there is no file there, a new one saved
    /// there. A file that exists and cannot be read as a key is an error, never replaced.
    /// </summary>
    public static async Task<NodeKey> LoadOrCreateAsync(string path, Profile profile = Profile.PqHybrid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Profiles.Name(profile);
        return new NodeKey(await NativeCall.RunAsync(token =>
        {
            nint err = 0;
            var handle = Libmacula.macula_key_load_or_create(path, name, token, ref err);
            NativeCall.Check(err, cancellationToken);
            return handle;
        }, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Saves the key to <paramref name="path"/>, readable by its owner only.</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        nint err = 0;
        Libmacula.macula_key_save(Handle, path, ref err);
        NativeCall.Check(err);
    }

    /// <summary>The node id the key names.</summary>
    public unsafe MeshId NodeId
    {
        get
        {
            Span<byte> id = stackalloc byte[32];
            nint err = 0;
            fixed (byte* p = id)
            {
                Libmacula.macula_key_node_id(Handle, p, ref err);
            }
            NativeCall.Check(err);
            return new MeshId(id);
        }
    }

    /// <summary>The public key as the wire carries it.</summary>
    public byte[] PublicKey
    {
        get
        {
            nint err = 0;
            var bytes = Libmacula.macula_key_public_key(Handle, out var length, ref err);
            NativeCall.Check(err);
            return NativeCall.TakeBytes(bytes, length);
        }
    }

    /// <summary>The key's profile.</summary>
    public Profile Profile
    {
        get
        {
            nint err = 0;
            var name = Libmacula.macula_key_profile(Handle, ref err);
            NativeCall.Check(err);
            return Profiles.Parse(NativeCall.TakeString(name)!);
        }
    }

    /// <summary>Signs <paramref name="data"/> as it is given.</summary>
    public unsafe byte[] Sign(ReadOnlySpan<byte> data)
    {
        nint err = 0;
        nint signature;
        nuint length;
        fixed (byte* p = data)
        {
            signature = Libmacula.macula_key_sign(Handle, p, (nuint)data.Length, out length, ref err);
        }
        NativeCall.Check(err);
        return NativeCall.TakeBytes(signature, length);
    }

    /// <summary>Whether <paramref name="signature"/> is valid over <paramref name="data"/> for a public key as carried.</summary>
    public unsafe static bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> publicKey,
        Profile profile = Profile.PqHybrid)
    {
        NativeCall.EnsureLoaded();
        nint err = 0;
        int valid;
        fixed (byte* d = data, s = signature, k = publicKey)
        {
            valid = Libmacula.macula_verify(d, (nuint)data.Length, s, (nuint)signature.Length, k,
                (nuint)publicKey.Length, Profiles.Name(profile), ref err);
        }
        NativeCall.Check(err);
        return valid == 1;
    }

    /// <summary>Frees the key. A pool connected with it keeps what it needs.</summary>
    public void Dispose() => Handle.Dispose();
}
