using System.Buffers.Binary;

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

    public static byte[] Encode(Value value)
    {
        var buf = new List<byte>();
        WriteValue(buf, value);
        return buf.ToArray();
    }

    public static Value Decode(ReadOnlySpan<byte> data)
    {
        var (value, consumed) = DecodeOne(data, 0);
        if (consumed != data.Length)
        {
            throw new CborDecodeException(
                $"trailing bytes after top-level value: {data.Length - consumed} unconsumed");
        }
        return value;
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
    /// Decodes the value at the start of <paramref name="data"/>, which sits
    /// <paramref name="depth"/> list or map levels below the top-level value.
    /// </summary>
    private static (Value Value, int Consumed) DecodeOne(ReadOnlySpan<byte> data, int depth)
    {
        if (depth > MaxNestingDepth)
        {
            throw new CborDecodeException($"list or map nesting exceeds {MaxNestingDepth} levels");
        }

        if (data.Length < 1)
        {
            throw new CborDecodeException("unexpected end of input");
        }

        byte initial = data[0];
        byte major = (byte)(initial >> 5);
        byte ai = (byte)(initial & 0x1F);
        int pos = 1;

        switch (major)
        {
            case 0:
            {
                ulong v = ReadArgument(data, ref pos, ai);
                return (Value.UInt(v), pos);
            }
            case 1:
            {
                ulong n = ReadArgument(data, ref pos, ai);
                return (Value.NegInt(n), pos);
            }
            case 2:
            {
                int len = checked((int)ReadArgument(data, ref pos, ai));
                RequireRemaining(data, pos, len);
                var bytes = data.Slice(pos, len).ToArray();
                return (Value.Bytes(bytes), pos + len);
            }
            case 3:
            {
                int len = checked((int)ReadArgument(data, ref pos, ai));
                RequireRemaining(data, pos, len);
                var bytes = data.Slice(pos, len).ToArray();
                return (Value.TextBytes(bytes), pos + len);
            }
            case 4:
            {
                int count = checked((int)ReadArgument(data, ref pos, ai));
                var items = new Value[count];
                for (int i = 0; i < count; i++)
                {
                    var (item, consumed) = DecodeOne(data.Slice(pos), depth + 1);
                    items[i] = item;
                    pos += consumed;
                }
                return (Value.List(items), pos);
            }
            case 5:
                return DecodeMap(data, ai, pos, depth);
            case 6:
                throw new CborDecodeException("major type 6 (tags) is not supported");
            case 7:
                return DecodeMajor7(data, ai, pos);
            default:
                throw new CborDecodeException($"unreachable major type {major}");
        }
    }

    /// <summary>
    /// Duplicate keys on decode are last-write-wins, not an error. Dedup is
    /// keyed by each key's own raw encoded bytes as they appeared on the
    /// wire -- not by Value equality, which would require a deep-equality
    /// contract this value model deliberately doesn't carry. The map sits
    /// <paramref name="depth"/> levels below the top-level value, so its keys
    /// and values sit one level further down.
    /// </summary>
    private static (Value, int) DecodeMap(ReadOnlySpan<byte> data, byte ai, int pos, int depth)
    {
        int count = checked((int)ReadArgument(data, ref pos, ai));
        var entries = new List<KeyValuePair<Value, Value>>();
        var indexOfKeyBytes = new Dictionary<string, int>();

        for (int i = 0; i < count; i++)
        {
            int keyStart = pos;
            var (key, keyConsumed) = DecodeOne(data.Slice(pos), depth + 1);
            pos += keyConsumed;
            var rawKeyBytes = data.Slice(keyStart, keyConsumed);

            var (val, valConsumed) = DecodeOne(data.Slice(pos), depth + 1);
            pos += valConsumed;

            var keyId = Convert.ToHexStringLower(rawKeyBytes);
            if (indexOfKeyBytes.TryGetValue(keyId, out var existingIndex))
            {
                entries[existingIndex] = new KeyValuePair<Value, Value>(key, val);
            }
            else
            {
                indexOfKeyBytes[keyId] = entries.Count;
                entries.Add(new KeyValuePair<Value, Value>(key, val));
            }
        }

        return (Value.Map(entries), pos);
    }

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
}
