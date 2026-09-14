using Macula;
using Macula.Cbor;

namespace Macula.Tests;

/// <summary>
/// Byte-level assertions rather than object-equality assertions throughout:
/// this codec's entire reason for existing is producing exact bytes a real
/// station's Ed25519 signature check depends on, so "the right bytes" is
/// literally the spec, not a proxy for it.
/// </summary>
public class CborCodecTests
{
    [Theory]
    [InlineData(0UL, new byte[] { 0x00 })]
    [InlineData(1UL, new byte[] { 0x01 })]
    [InlineData(23UL, new byte[] { 0x17 })]
    [InlineData(24UL, new byte[] { 0x18, 0x18 })]
    [InlineData(255UL, new byte[] { 0x18, 0xFF })]
    [InlineData(256UL, new byte[] { 0x19, 0x01, 0x00 })]
    [InlineData(65535UL, new byte[] { 0x19, 0xFF, 0xFF })]
    [InlineData(65536UL, new byte[] { 0x1A, 0x00, 0x01, 0x00, 0x00 })]
    [InlineData(4294967295UL, new byte[] { 0x1A, 0xFF, 0xFF, 0xFF, 0xFF })]
    [InlineData(4294967296UL, new byte[] { 0x1B, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 })]
    [InlineData(ulong.MaxValue, new byte[] { 0x1B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })]
    public void UInt_uses_minimal_length_encoding(ulong value, byte[] expected)
    {
        Assert.Equal(expected, CborCodec.Encode(Value.UInt(value)));
    }

    [Theory]
    [InlineData(-1L, new byte[] { 0x20 })]
    [InlineData(-24L, new byte[] { 0x37 })]
    [InlineData(-25L, new byte[] { 0x38, 0x18 })]
    public void Negative_int_uses_minimal_length_encoding(long value, byte[] expected)
    {
        Assert.Equal(expected, CborCodec.Encode(Value.Int(value)));
    }

    [Fact]
    public void NegInt_reaches_down_to_negative_two_to_the_64()
    {
        // Actual value = -1 - NMinusOne, so NMinusOne = ulong.MaxValue means
        // the encoded value is -(2^64) -- one bit further than a plain
        // long/i64::MIN can express, matching the spec's i128-internally note.
        var bytes = CborCodec.Encode(Value.NegInt(ulong.MaxValue));
        Assert.Equal(new byte[] { 0x3B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, bytes);
    }

    [Fact]
    public void Bytes_encodes_as_major_2_raw()
    {
        var bytes = CborCodec.Encode(Value.Bytes(new byte[] { 0xDE, 0xAD, 0xBE }));
        Assert.Equal(new byte[] { 0x43, 0xDE, 0xAD, 0xBE }, bytes);
    }

    [Fact]
    public void Text_stores_bytes_as_is_with_no_utf8_validation()
    {
        // Matches the Erlang encoder's own leniency: invalid UTF-8 must
        // still round-trip through major type 3 unchanged, not throw.
        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0x00 };
        var bytes = CborCodec.Encode(Value.TextBytes(invalidUtf8));
        Assert.Equal(new byte[] { 0x63, 0xFF, 0xFE, 0x00 }, bytes);

        var decoded = CborCodec.Decode(bytes);
        var text = Assert.IsType<Value.TextValue>(decoded);
        Assert.Equal(invalidUtf8, text.Utf8);
    }

    [Fact]
    public void Null_encodes_as_0xF6()
    {
        Assert.Equal(new byte[] { 0xF6 }, CborCodec.Encode(Value.Null));
    }

    [Fact]
    public void Float_always_encodes_as_binary64_regardless_of_source_width()
    {
        // Deliberate divergence from RFC 8949's own shortest-float
        // canonical-form recommendation -- byte derivation must be
        // independent of platform float encoding, so 1.0 (which would fit
        // in far fewer bits under the RFC rule) still costs the full 9 bytes.
        var bytes = CborCodec.Encode(Value.Float(1.0));
        Assert.Equal(9, bytes.Length);
        Assert.Equal(0xFB, bytes[0]);
        Assert.Equal(1.0, BitConverter.Int64BitsToDouble(
            System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(1))));
    }

    [Fact]
    public void List_encodes_head_plus_concatenated_items()
    {
        var value = Value.List(new[] { Value.UInt(1), Value.UInt(2), Value.UInt(3) });
        Assert.Equal(new byte[] { 0x83, 0x01, 0x02, 0x03 }, CborCodec.Encode(value));
    }

    [Fact]
    public void Map_sorts_keys_by_their_own_encoded_bytes_not_by_value()
    {
        // UInt(100) encodes to a single byte starting 0x18 0x64 (major 0,
        // AI 24) -- 0x18. NegInt for -1 encodes to 0x20 (major 1). Major 0's
        // encoded bytes always sort below major 1's, so UInt(100) must sort
        // BEFORE Int(-1) as a map key even though -1 < 100 numerically. This
        // is exactly the divergence the spec calls out as the rule a naive
        // implementation is most likely to get wrong.
        var map = Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Int(-1), Value.UInt(0xAA)),
            new KeyValuePair<Value, Value>(Value.UInt(100), Value.UInt(0xBB)),
        });

        var bytes = CborCodec.Encode(map);

        // head(major5, count=2) = 0xA2, then UInt(100) key+value first
        // (0x18 0x64 0x18 0xBB), then Int(-1) key+value (0x20 0x18 0xAA).
        Assert.Equal(new byte[] { 0xA2, 0x18, 0x64, 0x18, 0xBB, 0x20, 0x18, 0xAA }, bytes);
    }

    [Fact]
    public void Decode_accepts_half_precision_float()
    {
        // 0xF9 3E00 = half-precision 1.5.
        var bytes = new byte[] { 0xF9, 0x3E, 0x00 };
        var decoded = Assert.IsType<Value.FloatValue>(CborCodec.Decode(bytes));
        Assert.Equal(1.5, decoded.Value);
    }

    [Fact]
    public void Decode_accepts_single_precision_float()
    {
        // 0xFA 3F800000 = single-precision 1.0.
        var bytes = new byte[] { 0xFA, 0x3F, 0x80, 0x00, 0x00 };
        var decoded = Assert.IsType<Value.FloatValue>(CborCodec.Decode(bytes));
        Assert.Equal(1.0, decoded.Value);
    }

    [Fact]
    public void Decode_duplicate_map_keys_is_last_write_wins()
    {
        // Map{1} entries, hand-built to contain the SAME key (UInt(1))
        // twice with different values -- head byte is patched to claim 2
        // entries even though a real encoder would never emit this; decode
        // must still accept it and keep only the final value, not error.
        var bytes = new byte[] { 0xA2, 0x01, 0x18, 0xAA, 0x01, 0x18, 0xBB };
        var decoded = Assert.IsType<Value.MapValue>(CborCodec.Decode(bytes));
        var entry = Assert.Single(decoded.Entries);
        Assert.Equal(0xBBUL, ((Value.UIntValue)entry.Value).Value);
    }

    [Fact]
    public void Decode_rejects_major_type_6_tags()
    {
        var bytes = new byte[] { 0xC0, 0x00 };
        Assert.Throws<CborDecodeException>(() => CborCodec.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_trailing_bytes_after_top_level_value()
    {
        var bytes = new byte[] { 0x01, 0x02 };
        Assert.Throws<CborDecodeException>(() => CborCodec.Decode(bytes));
    }

    [Fact]
    public void Decode_rejects_truncated_input_instead_of_throwing_an_unrelated_exception()
    {
        var bytes = new byte[] { 0x18 }; // claims one more byte follows; none does
        Assert.Throws<CborDecodeException>(() => CborCodec.Decode(bytes));
    }

    [Fact]
    public void Encode_decode_encode_is_idempotent_for_a_nested_structure()
    {
        var value = Value.Map(new[]
        {
            new KeyValuePair<Value, Value>(Value.Text("frame_type"), Value.Text("call")),
            new KeyValuePair<Value, Value>(Value.Text("payload"), Value.List(new[]
            {
                Value.Int(-7),
                Value.Bytes(new byte[] { 1, 2, 3 }),
                Value.Float(3.5),
                Value.Null,
            })),
        });

        var first = CborCodec.Encode(value);
        var roundTripped = CborCodec.Decode(first);
        var second = CborCodec.Encode(roundTripped);

        Assert.Equal(first, second);
    }

    /// <summary>
    /// A uint 0 nested <paramref name="depth"/> list or map levels below the
    /// top-level value, one or two bytes per level: a one-element list per
    /// level, or a one-entry map holding the next level as its value, or as
    /// its key.
    /// </summary>
    private static byte[] NestedPayload(string shape, int depth) => shape switch
    {
        "list" => [.. Enumerable.Repeat((byte)0x81, depth), 0x00],
        "map value" => [.. Enumerable.Repeat(new byte[] { 0xA1, 0x00 }, depth).SelectMany(level => level), 0x00],
        "map key" => [.. Enumerable.Repeat((byte)0xA1, depth), .. Enumerable.Repeat((byte)0x00, depth + 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    [Theory]
    [InlineData("list")]
    [InlineData("map value")]
    [InlineData("map key")]
    public void Decode_accepts_nesting_at_the_depth_limit(string shape)
    {
        var payload = NestedPayload(shape, CborCodec.MaxNestingDepth);

        Assert.Equal(payload, CborCodec.Encode(CborCodec.Decode(payload)));
    }

    [Theory]
    [InlineData("list")]
    [InlineData("map value")]
    [InlineData("map key")]
    public void Decode_rejects_nesting_one_past_the_depth_limit(string shape)
    {
        var payload = NestedPayload(shape, CborCodec.MaxNestingDepth + 1);

        var ex = Assert.Throws<CborDecodeException>(() => CborCodec.Decode(payload));
        Assert.Contains("nesting", ex.Message);
    }

    /// <summary>
    /// A million levels is at most a 2 MB frame, well under the frame cap. A
    /// test host that dies here instead of passing or failing means the cap
    /// has regressed.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("map value")]
    [InlineData("map key")]
    public void Decode_rejects_extreme_nesting_without_crashing(string shape)
    {
        var payload = NestedPayload(shape, 1_000_000);

        var ex = Assert.Throws<CborDecodeException>(() => CborCodec.Decode(payload));
        Assert.Contains("nesting", ex.Message);
    }
}
