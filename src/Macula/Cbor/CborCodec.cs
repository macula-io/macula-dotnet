using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Macula.Cbor;

/// <summary>
/// The deterministic CBOR codec macula's wire protocol actually uses --
/// transcribed from native/macula_cbor_nif/src/deterministic.rs (the
/// hand-rolled encoder the station calls, NOT the general-purpose
/// ciborium-based path in the same NIF crate). Every frame signature is
/// computed over these exact bytes, so any deviation here breaks signature
/// verification against a real station rather than just "being less
/// canonical."
/// </summary>
public static class CborCodec
{
    /// <summary>
    /// How many list or map levels a decoded value may sit below the
    /// top-level value: the same cap and the same count as macula's decoder,
    /// macula-go and macula-rust. Decoding recurses, and a frame can nest one
    /// level per byte, so this cap is what bounds how deep decoding goes.
    /// </summary>
    internal const int MaxNestingDepth = 128;

    /// <summary>
    /// How many values one decode may produce: the top-level value, every
    /// list or map, and every list item, map key and map value, a key that
    /// merges into an earlier one included. The same budget macula-go keeps,
    /// so what a frame decodes to is bounded by this, not by its length.
    /// </summary>
    internal const int MaxElements = 1 << 20;

    public static byte[] Encode(Value value)
    {
        var buf = new List<byte>();
        WriteValue(buf, value);
        return buf.ToArray();
    }

    public static Value Decode(ReadOnlySpan<byte> data)
    {
        using var state = new DecodeState();
        var decoded = DecodeOne(data, 0, false, state);
        if (decoded.Consumed != data.Length)
        {
            throw new CborDecodeException(
                $"trailing bytes after top-level value: {data.Length - decoded.Consumed} unconsumed");
        }
        return decoded.Value;
    }

    // ---- encode ----

    private static void WriteValue(List<byte> buf, Value value)
    {
        switch (value)
        {
            case Value.UIntValue u:
                WriteHead(buf, 0, u.Value);
                break;
            case Value.NegIntValue n:
                WriteHead(buf, 1, n.NMinusOne);
                break;
            case Value.BytesValue b:
                WriteHead(buf, 2, (ulong)b.Value.Length);
                buf.AddRange(b.Value);
                break;
            case Value.TextValue t:
                WriteHead(buf, 3, (ulong)t.Utf8.Length);
                buf.AddRange(t.Utf8);
                break;
            case Value.ListValue l:
                WriteHead(buf, 4, (ulong)l.Items.Count);
                foreach (var item in l.Items)
                {
                    WriteValue(buf, item);
                }
                break;
            case Value.MapValue m:
                WriteMap(buf, m);
                break;
            case Value.NullValue:
                buf.Add(0xF6);
                break;
            case Value.FloatValue f:
            {
                buf.Add(0xFB);
                Span<byte> tmp = stackalloc byte[8];
                BinaryPrimitives.WriteDoubleBigEndian(tmp, f.Value);
                buf.AddRange(tmp.ToArray());
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(value), $"unencodable value type {value.GetType().Name}");
        }
    }

    /// <summary>
    /// Keys are sorted by the bytewise lexicographic order of their own
    /// ALREADY-ENCODED bytes -- encode each key independently first, then
    /// sort the (key_bytes, value_bytes) pairs by key_bytes. Sorting by the
    /// original key representation instead diverges from station output for
    /// keys of different CBOR major types.
    /// </summary>
    private static void WriteMap(List<byte> buf, Value.MapValue m)
    {
        var encoded = new (byte[] KeyBytes, byte[] ValBytes)[m.Entries.Count];
        for (int i = 0; i < m.Entries.Count; i++)
        {
            var entry = m.Entries[i];
            encoded[i] = (Encode(entry.Key), Encode(entry.Value));
        }

        Array.Sort(encoded, (a, b) => a.KeyBytes.AsSpan().SequenceCompareTo(b.KeyBytes));

        WriteHead(buf, 5, (ulong)encoded.Length);
        foreach (var (keyBytes, valBytes) in encoded)
        {
            buf.AddRange(keyBytes);
            buf.AddRange(valBytes);
        }
    }

    private static void WriteHead(List<byte> buf, byte major, ulong argument)
    {
        byte majorByte = (byte)(major << 5);
        if (argument <= 23)
        {
            buf.Add((byte)(majorByte | (byte)argument));
            return;
        }

        if (argument <= byte.MaxValue)
        {
            buf.Add((byte)(majorByte | 24));
            buf.Add((byte)argument);
            return;
        }

        if (argument <= ushort.MaxValue)
        {
            buf.Add((byte)(majorByte | 25));
            Span<byte> tmp = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(tmp, (ushort)argument);
            buf.AddRange(tmp.ToArray());
            return;
        }

        if (argument <= uint.MaxValue)
        {
            buf.Add((byte)(majorByte | 26));
            Span<byte> tmp = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(tmp, (uint)argument);
            buf.AddRange(tmp.ToArray());
            return;
        }

        buf.Add((byte)(majorByte | 27));
        Span<byte> tmp8 = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(tmp8, argument);
        buf.AddRange(tmp8.ToArray());
    }

    // ---- decode ----
    // Every path here is bounds-checked explicitly -- malformed/truncated
    // network input must produce a CborDecodeException, never an
    // unhandled index-out-of-range from the runtime.

    /// <summary>
    /// A decoded value, how many bytes it took, and its key identity when one
    /// was asked for (the default identity otherwise).
    /// </summary>
    private readonly record struct DecodedValue(Value Value, int Consumed, KeyIdentity Identity);

    /// <summary>
    /// Decodes the value at the start of <paramref name="data"/>, which sits
    /// <paramref name="depth"/> list or map levels below the top-level value,
    /// with its key identity when <paramref name="withIdentity"/> is set: a
    /// map key needs one, and so does everything nested inside a key. Every
    /// value takes one from the decode's budget (<see cref="MaxElements"/>)
    /// as it starts.
    /// </summary>
    private static DecodedValue DecodeOne(ReadOnlySpan<byte> data, int depth, bool withIdentity, DecodeState state)
    {
        if (depth > MaxNestingDepth)
        {
            throw new CborDecodeException($"list or map nesting exceeds {MaxNestingDepth} levels");
        }

        state.TakeElement();

        if (data.Length < 1)
        {
            throw new CborDecodeException("unexpected end of input");
        }

        byte major = (byte)(data[0] >> 5);
        byte ai = (byte)(data[0] & 0x1F);
        return major switch
        {
            0 or 1 => DecodeInteger(data, major, ai, withIdentity),
            2 or 3 => DecodeString(data, major, ai, withIdentity ? state.At(depth) : null),
            4 => DecodeList(data, ai, depth, withIdentity, state),
            5 => DecodeMap(data, ai, depth, withIdentity, state),
            6 => throw new CborDecodeException("major type 6 (tags) is not supported"),
            _ => DecodeSimple(data, ai, withIdentity),
        };
    }

    /// <summary>
    /// The most room made up front for the items of one list or map. A count
    /// is only a claim until its items decode, and every enclosing list keeps
    /// its room while the items inside it decode, so room made from claims
    /// alone would grow with the claims times the depth instead of with the
    /// input. The same bound macula-go and macula's CBOR NIF use.
    /// </summary>
    private const int MaxPreallocatedItems = 1024;

    /// <summary>
    /// Reads the length or count at <paramref name="pos"/> and checks it
    /// against the bytes left after it, where each item it counts takes at
    /// least <paramref name="bytesPerItem"/> bytes. A claim the rest of the
    /// input can't hold is refused before anything is sized by it, which also
    /// keeps every length returned within the range of an int.
    /// </summary>
    private static int ReadLength(ReadOnlySpan<byte> data, ref int pos, byte ai, int bytesPerItem)
    {
        ulong claimed = ReadArgument(data, ref pos, ai);
        ulong left = (ulong)(data.Length - pos);
        if (claimed > left / (ulong)bytesPerItem)
        {
            throw new CborDecodeException($"a length of {claimed} is more than the {left} bytes left can hold");
        }
        return (int)claimed;
    }

    private static DecodedValue DecodeInteger(ReadOnlySpan<byte> data, byte major, byte ai, bool withIdentity)
    {
        int pos = 1;
        ulong argument = ReadArgument(data, ref pos, ai);
        var value = major == 0 ? Value.UInt(argument) : Value.NegInt(argument);
        return new DecodedValue(value, pos, withIdentity ? KeyIdentity.OfFixed((byte)(major << 5), argument) : default);
    }

    /// <summary>
    /// A byte or text string. Its identity, when <paramref name="identity"/>
    /// is given, is hashed from the content where it lies in the input, so a
    /// long key is never copied to be identified.
    /// </summary>
    private static DecodedValue DecodeString(ReadOnlySpan<byte> data, byte major, byte ai, IncrementalHash? identity)
    {
        int pos = 1;
        int len = ReadLength(data, ref pos, ai, 1);
        var content = data.Slice(pos, len);
        var value = major == 2 ? Value.Bytes(content.ToArray()) : Value.TextBytes(content.ToArray());
        return new DecodedValue(value, pos + len, KeyIdentity.OfString(identity, major, content));
    }

    private static DecodedValue DecodeList(ReadOnlySpan<byte> data, byte ai, int depth, bool withIdentity, DecodeState state)
    {
        int pos = 1;
        int count = ReadLength(data, ref pos, ai, 1);
        var identity = withIdentity ? KeyIdentity.Start(state.At(depth), 4, (ulong)count) : null;
        var items = new List<Value>(Math.Min(count, MaxPreallocatedItems));
        for (int i = 0; i < count; i++)
        {
            var item = DecodeOne(data.Slice(pos), depth + 1, withIdentity, state);
            items.Add(item.Value);
            item.Identity.AppendTo(identity);
            pos += item.Consumed;
        }
        return new DecodedValue(Value.List(items), pos, KeyIdentity.Finish(identity));
    }

    /// <summary>
    /// Duplicate keys on decode are last-write-wins, not an error, and two
    /// keys are duplicates exactly when their canonical encodings are equal
    /// (<see cref="KeyIdentity"/>). The map sits <paramref name="depth"/>
    /// levels below the top-level value, so its keys and values sit one level
    /// further down.
    /// </summary>
    private static DecodedValue DecodeMap(ReadOnlySpan<byte> data, byte ai, int depth, bool withIdentity, DecodeState state)
    {
        int pos = 1;
        int count = ReadLength(data, ref pos, ai, 2);
        var entries = new MapEntries(Math.Min(count, MaxPreallocatedItems), withIdentity);
        for (int i = 0; i < count; i++)
        {
            var key = DecodeOne(data.Slice(pos), depth + 1, true, state);
            pos += key.Consumed;
            var value = DecodeOne(data.Slice(pos), depth + 1, withIdentity, state);
            pos += value.Consumed;
            entries.Put(key, value);
        }
        return new DecodedValue(Value.Map(entries.Pairs), pos, withIdentity ? entries.Identity(state.At(depth)) : default);
    }

    private static DecodedValue DecodeSimple(ReadOnlySpan<byte> data, byte ai, bool withIdentity)
    {
        var (value, consumed) = DecodeMajor7(data, ai, 1);
        return new DecodedValue(value, consumed, withIdentity ? SimpleIdentity(value) : default);
    }

    /// <summary>A float is identified by the bits it encodes as, null by its own marker.</summary>
    private static KeyIdentity SimpleIdentity(Value value) => value switch
    {
        Value.FloatValue f => KeyIdentity.OfFixed(0xFB, BitConverter.DoubleToUInt64Bits(f.Value)),
        _ => KeyIdentity.OfFixed(0xF6, 0),
    };

    private static (Value, int) DecodeMajor7(ReadOnlySpan<byte> data, byte ai, int pos)
    {
        switch (ai)
        {
            case 22:
                return (Value.Null, pos);
            case 25:
                RequireRemaining(data, pos, 2);
                double half = (double)BinaryPrimitives.ReadHalfBigEndian(data.Slice(pos, 2));
                return (Value.Float(half), pos + 2);
            case 26:
                RequireRemaining(data, pos, 4);
                double single = BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4));
                return (Value.Float(single), pos + 4);
            case 27:
                RequireRemaining(data, pos, 8);
                double dbl = BinaryPrimitives.ReadDoubleBigEndian(data.Slice(pos, 8));
                return (Value.Float(dbl), pos + 8);
            default:
                throw new CborDecodeException(
                    $"unsupported major-7 additional info {ai}: only null and the three float widths are supported");
        }
    }

    private static ulong ReadArgument(ReadOnlySpan<byte> data, ref int pos, byte ai)
    {
        if (ai <= 23)
        {
            return ai;
        }

        switch (ai)
        {
            case 24:
                RequireRemaining(data, pos, 1);
                var v1 = data[pos];
                pos += 1;
                return v1;
            case 25:
                RequireRemaining(data, pos, 2);
                var v2 = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                pos += 2;
                return v2;
            case 26:
                RequireRemaining(data, pos, 4);
                var v4 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
                pos += 4;
                return v4;
            case 27:
                RequireRemaining(data, pos, 8);
                var v8 = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(pos, 8));
                pos += 8;
                return v8;
            default:
                throw new CborDecodeException(
                    $"unsupported additional info {ai}: indefinite-length encoding is not supported");
        }
    }

    private static void RequireRemaining(ReadOnlySpan<byte> data, int pos, int needed)
    {
        if (data.Length - pos < needed)
        {
            throw new CborDecodeException("unexpected end of input");
        }
    }

    /// <summary>
    /// What a map's duplicate keys are matched by: a SHA-256 digest two
    /// decoded values share exactly when their canonical encodings are equal,
    /// the rule macula-go follows. A scalar's digest covers a marker for its
    /// type and its value; a list's covers its item count and its items'
    /// identities in order; a map's covers its entry count and its entries'
    /// key and value identities ordered by key identity, once its own
    /// duplicates have merged. Each identity is worked out once, while its
    /// value decodes, so a key nested inside other keys is never encoded or
    /// copied again at the levels above it.
    /// </summary>
    private readonly record struct KeyIdentity(ulong A, ulong B, ulong C, ulong D) : IComparable<KeyIdentity>
    {
        private const int Size = 32;

        /// <summary>A scalar whose value fits eight bytes: an integer, a float's bits, or null.</summary>
        public static KeyIdentity OfFixed(byte marker, ulong value)
        {
            Span<byte> preimage = stackalloc byte[9];
            preimage[0] = marker;
            BinaryPrimitives.WriteUInt64BigEndian(preimage[1..], value);
            Span<byte> digest = stackalloc byte[Size];
            SHA256.HashData(preimage, digest);
            return FromDigest(digest);
        }

        /// <summary>A byte or text string, or the default identity when no hash is given.</summary>
        public static KeyIdentity OfString(IncrementalHash? hash, byte major, ReadOnlySpan<byte> content)
        {
            if (hash is null)
            {
                return default;
            }
            Start(hash, major, (ulong)content.Length);
            hash.AppendData(content);
            return FromHash(hash);
        }

        /// <summary>Begins a string, list or map identity with its major type and its length or count.</summary>
        public static IncrementalHash Start(IncrementalHash hash, byte major, ulong count)
        {
            Span<byte> head = stackalloc byte[9];
            head[0] = (byte)(major << 5);
            BinaryPrimitives.WriteUInt64BigEndian(head[1..], count);
            hash.AppendData(head);
            return hash;
        }

        public static KeyIdentity Finish(IncrementalHash? hash) => hash is null ? default : FromHash(hash);

        /// <summary>Adds this identity to a list or map identity being built, if one is.</summary>
        public void AppendTo(IncrementalHash? hash)
        {
            if (hash is null)
            {
                return;
            }
            Span<byte> bytes = stackalloc byte[Size];
            BinaryPrimitives.WriteUInt64BigEndian(bytes, A);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], B);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[16..], C);
            BinaryPrimitives.WriteUInt64BigEndian(bytes[24..], D);
            hash.AppendData(bytes);
        }

        public int CompareTo(KeyIdentity other) => (A, B, C, D).CompareTo((other.A, other.B, other.C, other.D));

        private static KeyIdentity FromHash(IncrementalHash hash)
        {
            Span<byte> digest = stackalloc byte[Size];
            hash.GetHashAndReset(digest);
            return FromDigest(digest);
        }

        private static KeyIdentity FromDigest(ReadOnlySpan<byte> digest) => new(
            BinaryPrimitives.ReadUInt64BigEndian(digest),
            BinaryPrimitives.ReadUInt64BigEndian(digest[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(digest[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(digest[24..]));
    }

    /// <summary>
    /// What one decode keeps across its values: how many more values it may
    /// produce (<see cref="MaxElements"/>), and one SHA-256 hasher per
    /// nesting level, made on first use and reused. A value finishes its
    /// identity before the next value at its level starts, and its items sit
    /// one level down, so one hasher per level serves the whole decode however
    /// many values need an identity.
    /// </summary>
    private sealed class DecodeState : IDisposable
    {
        private IncrementalHash?[]? _byDepth;
        private int _valuesLeft = MaxElements;

        /// <summary>Takes one value from the budget as it starts decoding, refusing the value that goes past it.</summary>
        public void TakeElement()
        {
            if (_valuesLeft == 0)
            {
                throw new CborDecodeException($"more than {MaxElements} values");
            }
            _valuesLeft--;
        }

        public IncrementalHash At(int depth)
        {
            _byDepth ??= new IncrementalHash?[MaxNestingDepth + 1];
            return _byDepth[depth] ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        public void Dispose()
        {
            foreach (var hash in _byDepth ?? [])
            {
                hash?.Dispose();
            }
        }
    }

    /// <summary>
    /// A map's entries as they decode. A key whose identity matches an earlier
    /// key's replaces that entry where it stands, so the later write wins, and
    /// the entries' identities are kept when the map needs one of its own.
    /// </summary>
    private sealed class MapEntries(int capacity, bool withIdentity)
    {
        private readonly Dictionary<KeyIdentity, int> _indexOfKey = new(capacity);
        private readonly List<(KeyIdentity Key, KeyIdentity Value)>? _identities = withIdentity ? new(capacity) : null;

        public List<KeyValuePair<Value, Value>> Pairs { get; } = new(capacity);

        public void Put(DecodedValue key, DecodedValue value)
        {
            if (_indexOfKey.TryGetValue(key.Identity, out var index))
            {
                Replace(index, key, value);
                return;
            }
            _indexOfKey[key.Identity] = Pairs.Count;
            Pairs.Add(new KeyValuePair<Value, Value>(key.Value, value.Value));
            _identities?.Add((key.Identity, value.Identity));
        }

        /// <summary>The map's own identity, from its merged entries ordered by key identity.</summary>
        public KeyIdentity Identity(IncrementalHash hash)
        {
            var identities = _identities!;
            identities.Sort((a, b) => a.Key.CompareTo(b.Key));
            KeyIdentity.Start(hash, 5, (ulong)identities.Count);
            foreach (var (key, value) in identities)
            {
                key.AppendTo(hash);
                value.AppendTo(hash);
            }
            return KeyIdentity.Finish(hash);
        }

        private void Replace(int index, DecodedValue key, DecodedValue value)
        {
            Pairs[index] = new KeyValuePair<Value, Value>(key.Value, value.Value);
            if (_identities is not null)
            {
                _identities[index] = (key.Identity, value.Identity);
            }
        }
    }
}
