using Macula.Bolt4;
using Macula.Cbor;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Every test here builds the EXACT SAME frame macula-rust's own
/// differential-vector tests build (same identity, same fixed
/// frame_id/sent_at_ms/call_id/stream_id, same field values) and asserts
/// the resulting Ed25519 signature matches byte-for-byte -- and those
/// Rust vectors were themselves captured from a real, live
/// `macula_frame:*` + `sign/2` call in `macula-io/macula`'s own
/// `rebar3 shell`, not independently derived. A match here proves the
/// canonical-CBOR encoding, the field set, and the signing domain for
/// every frame type are all bit-for-bit compatible with the real
/// station's own Erlang implementation, without touching the network.
///
/// Every hex constant below was extracted programmatically from
/// macula-rust's src/frame.rs (regex over the source), not
/// hand-transcribed.
/// </summary>
public class FrameGoldenVectorTests
{
    private const string VectorPub = "B966A9812649C3D5542FF54954FE090C43FDA6574FE48A0DD326626CFAD29A83";
    private const string VectorPriv = "457F45FF5A09E172ED15CB20D6CB26B51AD15ED7308C12D478E8631F9CA03D4F";
    private const string VectorPuzzleEvidence = "09D48C91CB46513ED2580BDCEA87C40DA508D4E50EC3DF2F701AFC55D1C5C0B2";
    private const string VectorFrameId = "0192E8B0F1A47000A1B2C3D4E5F60718";
    private const ulong VectorSentAtMs = 1_700_000_000_000;
    private const string VectorCallId = "AABBCCDDEEFF00112233445566778899";
    private const string VectorStreamId = "0102030405060708090A0B0C0D0E0F10";
    private static readonly byte[] VectorZeroRealm = new byte[32];

    private static byte[] Hex(string s) => Convert.FromHexString(s);
    private static byte[] PubBytes => Hex(VectorPub);
    private static KeyPair Identity => KeyPair.FromSeedBytes(Hex(VectorPriv));
    private static byte[] FrameId => Hex(VectorFrameId);
    private static byte[] CallId => Hex(VectorCallId);
    private static byte[] StreamId => Hex(VectorStreamId);

    private static string SigHex(Value.MapValue signed) =>
        signed.Get("signature") is Value.BytesValue b
            ? Convert.ToHexStringLower(b.Value).ToUpperInvariant()
            : throw new Exception("expected a signature field");

    [Fact]
    public void Connect_frame_matches_the_reference_byte_for_byte()
    {
        var pubBytes = PubBytes;
        var identity = Identity;
        var puzzleEvidence = Hex(VectorPuzzleEvidence);

        var spec = ConnectSpec.New(pubBytes, puzzleEvidence);
        var unsigned = ConnectFrame.Build(spec, FrameId, VectorSentAtMs);
        var signed = Envelope.Sign(unsigned, identity);

        Assert.Equal(
            "CF6959A61A2F4D2046F0124C1DD56A6541265F36A24CB18CA8C45C95031854D6AECE5FB93E2AE7BA6C444A09C7C5DED195B6EB0D1CC8E487CCF6E4F0D903B409",
            SigHex(signed));

        var encoded = WireCodec.Encode(signed);
        Assert.Equal(375, encoded.Length);

        // Round-trip: decode what we just built and verify it against the
        // known pubkey, exactly like a receiving station would.
        var decoded = Assert.IsType<Decoded.Frame>(WireCodec.Decode(encoded));
        Assert.Equal(encoded.Length, decoded.Consumed);
        var decodedMap = Assert.IsType<Value.MapValue>(decoded.Value);
        Assert.Null(Envelope.Verify(decodedMap, pubBytes));
    }

    [Fact]
    public void Verify_rejects_a_tampered_field()
    {
        var identity = Identity;
        var pubBytes = identity.PublicBytes();
        var spec = ConnectSpec.New(pubBytes, Hex(VectorPuzzleEvidence));
        var signed = Envelope.Sign(ConnectFrame.Build(spec), identity);

        var tampered = signed.WithField("capabilities", Value.UInt(999));
        Assert.Equal(Envelope.VerifyError.SignatureInvalid, Envelope.Verify(tampered, pubBytes));
    }

    [Fact]
    public void Verify_rejects_a_missing_signature()
    {
        var frame = (Value.MapValue)Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("frame_type"), Value.Text("connect")) });
        Assert.Equal(Envelope.VerifyError.MissingSignature, Envelope.Verify(frame, new byte[32]));
    }

    [Fact]
    public void Decode_reports_more_for_a_short_buffer()
    {
        Assert.Equal(2, Assert.IsType<Decoded.More>(WireCodec.Decode(new byte[] { 0, 0 })).NeededBytes);

        var buf = new byte[6];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, 10);
        Assert.Equal(8, Assert.IsType<Decoded.More>(WireCodec.Decode(buf)).NeededBytes);
    }

    [Fact]
    public void Decode_rejects_a_length_over_the_cap()
    {
        var buf = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)Envelope.MaxFrameBytes + 1);
        Assert.Throws<FrameTooLargeException>(() => WireCodec.Decode(buf));
    }

    /// <summary>
    /// A length prefix with its top bit set is over the cap too: the prefix is
    /// an unsigned 32-bit length, never a negative one.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0xFFFFFFFFu)]
    public void Decode_rejects_a_length_with_its_top_bit_set_as_over_the_cap(uint length)
    {
        var buf = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buf, length);
        Assert.Throws<FrameTooLargeException>(() => WireCodec.Decode(buf));
    }

    [Fact]
    public void Call_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new CallSpec
        {
            CallId = CallId,
            Procedure = "_content.get_manifest",
            Realm = VectorZeroRealm,
            Payload = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("hello"), Value.Text("world")) }),
            DeadlineMs = 1_700_000_030_000,
            Caller = PubBytes,
        };
        var signed = Envelope.Sign(CallFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "A6BC174F0241E644F634702C08781C8FC8BD3CDE3CA9650DE8A731A01203D9B9403A2CAD75800F7B8C9AAE16FA146B1195FF03F0E6DC4595A652D7F29BFE350A",
            SigHex(signed));
        Assert.Equal(386, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Result_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new ResultSpec { CallId = CallId, Payload = Value.Text("ok-result"), RespondedBy = PubBytes };
        var signed = Envelope.Sign(ResultFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "03E8F72D51D958C318B7F1C25D78408408317DEAB23434D6EA32F211CADEA1C62900DA15AFF603E795B19A388D382BDB10E65AEFC6F0CE551270AB172A88E50B",
            SigHex(signed));
        Assert.Equal(301, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Error_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new CallErrorSpec { CallId = CallId, Code = Bolt4Code.UnknownNextPeer, ReportedBy = PubBytes };
        var signed = Envelope.Sign(CallErrorFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "182ECD5217CE378F576635B23CC8C9F265555142845D6CBA033A282BAED97966C23FBE91D08507FB8E840375AA17665763804F40F89102F8D3EDAD4DA98FC20D",
            SigHex(signed));
        Assert.Equal(333, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Publish_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new PublishSpec
        {
            Topic = "test.topic",
            Realm = VectorZeroRealm,
            Publisher = PubBytes,
            Seq = 42,
            Payload = Value.Text("published-data"),
            PublishedAtMs = VectorSentAtMs,
        };
        var signed = Envelope.Sign(PublishFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "DD49D10EFA9F2EED0A393DC02DC5BBAC25D6731562EA39F5AB2E5337824527AFFBC7D917AF4DE5EFDBE5BC41E58659E05EC6FDE4E91FB1A32CC9C211456DF10C",
            SigHex(signed));
        Assert.Equal(355, WireCodec.Encode(signed).Length);
    }

    // Reference vector generated directly from the Erlang implementation
    // (macula-io/macula, src/peering/macula_frame.erl:sign_publisher/2),
    // live in a rebar3 shell against the same fixed identity every other
    // vector test here uses. First publisher_sig implementation in any
    // repo as of 2026-08-29 (macula-go, macula-rust,
    // macula-dotnet all lacked it) -- no prior port existed to
    // cross-check against instead, so this is checked straight against
    // the Erlang source of truth (same vector already proven byte-for-byte
    // against Go and Rust ports).
    [Fact]
    public void PublisherSig_matches_the_erlang_reference()
    {
        var spec = new PublishSpec
        {
            Topic = "acme/svc.do",
            Realm = VectorZeroRealm,
            Publisher = PubBytes,
            Seq = 42,
            Payload = Value.Bytes("hello"u8.ToArray()),
            PublishedAtMs = VectorSentAtMs,
        };
        var unsigned = PublishFrame.Build(spec, FrameId, VectorSentAtMs);
        var withPublisherSig = Envelope.SignPublisher(unsigned, Identity);

        var sig = withPublisherSig.Get("publisher_sig") is Value.BytesValue b
            ? b.Value
            : throw new Exception("expected a publisher_sig field");
        Assert.Equal(
            "C11BEB676A590FD1BA86F0B77E377B4582AA461DB1283F64E57224E920A7BD0A2C7D36271B795FFC3CB4F2C7BB8925B034431AA6425E25B2AEEFAC026883BB0C",
            Convert.ToHexStringLower(sig).ToUpperInvariant());

        Assert.Null(Envelope.VerifyPublisher(withPublisherSig));

        // Tamper check: changing payload after signing must invalidate it.
        var tampered = withPublisherSig.WithField("payload", Value.Bytes("world"u8.ToArray()));
        Assert.NotNull(Envelope.VerifyPublisher(tampered));

        // Absence must be a verification failure, not "trusted".
        Assert.Equal(Envelope.VerifyPublisherError.MissingPublisherSig, Envelope.VerifyPublisher(unsigned));
    }

    // Full encode/decode round trip with BOTH publisher_sig and the
    // per-hop signature present, mirroring exactly what a real caller
    // (Session.PublishAsync, once wired) builds.
    [Fact]
    public void Publish_frame_with_both_signatures_round_trips()
    {
        var identity = KeyPair.Generate();
        var pubBytes = identity.PublicBytes();
        var spec = new PublishSpec
        {
            Topic = "acme/svc.do",
            Realm = VectorZeroRealm,
            Publisher = pubBytes,
            Seq = 1,
            Payload = Value.Bytes("hello"u8.ToArray()),
            PublishedAtMs = VectorSentAtMs,
        };
        var unsigned = PublishFrame.Build(spec);
        var withPublisherSig = Envelope.SignPublisher(unsigned, identity);
        var fullySigned = Envelope.Sign(withPublisherSig, identity);

        var encoded = WireCodec.Encode(fullySigned);
        var decodeResult = Assert.IsType<Decoded.Frame>(WireCodec.Decode(encoded));
        Assert.Equal(encoded.Length, decodeResult.Consumed);
        var decoded = Assert.IsType<Value.MapValue>(decodeResult.Value);

        Assert.Null(Envelope.Verify(decoded, pubBytes));
        Assert.Null(Envelope.VerifyPublisher(decoded));
        Assert.NotNull(decoded.Get("publisher_sig"));
        Assert.NotNull(decoded.Get("signature"));
    }

    [Fact]
    public void Subscribe_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new SubscribeSpec { Topic = "test.topic", Realm = VectorZeroRealm, Subscriber = PubBytes };
        var signed = Envelope.Sign(SubscribeFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "ABDD7304B887A53B149CE4D4C62F1AFD20AE07D8612B76F22006FA6676B8DDB37C1D5106358D32080246BA4355A9E04BF49F73600E752F5F9037D7A93A47020A",
            SigHex(signed));
        Assert.Equal(313, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Unsubscribe_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new UnsubscribeSpec { Topic = "test.topic", Realm = VectorZeroRealm, Subscriber = PubBytes };
        var signed = Envelope.Sign(UnsubscribeFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "C917068BE4E1C5A3C753F249037DD8F44293D888BB252BF1E828671969547969982160C91A0E3CA1C31DE29ED39E3677E7F20F4BDE61539D4618B3703018E403",
            SigHex(signed));
        Assert.Equal(298, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Event_frame_matches_the_reference_byte_for_byte()
    {
        var pubBytes = PubBytes;
        var unsigned = Envelope.Base("event", 0, FrameId, VectorSentAtMs)
            .WithField("realm", Value.Bytes(VectorZeroRealm))
            .WithField("topic", Value.Bytes("test.topic"u8.ToArray()))
            .WithField("publisher", Value.Bytes(pubBytes))
            .WithField("seq", Value.UInt(42))
            .WithField("payload", Value.Text("published-data"))
            .WithField("delivered_via", Value.Text("direct"));
        var signed = Envelope.Sign(unsigned, Identity);

        Assert.Equal(
            "9B9EE4EAC375FBD0C9B5A5BC6D82E35739F8ECBF594979891BF35E5BDB53A148B3936AF99217C3D8C12E2EEA0686F68D5FE63284BE6B142F87BFF319DDDB780F",
            SigHex(signed));
        Assert.Equal(341, WireCodec.Encode(signed).Length);

        // Round-trip through EventFrameParsing too, since EVENT has a real
        // parser a receiving client uses.
        var decoded = Assert.IsType<Decoded.Frame>(WireCodec.Decode(WireCodec.Encode(signed)));
        var info = EventFrameParsing.Parse(decoded.Value);
        Assert.Equal("test.topic", info.Topic);
        Assert.Equal(42UL, info.Seq);
        Assert.Equal("direct", info.DeliveredVia);
    }

    [Fact]
    public void Stream_open_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamOpenSpec
        {
            StreamId = StreamId,
            Procedure = "macula_rust_sdk.test_stream",
            Realm = VectorZeroRealm,
            Mode = StreamMode.ClientStream,
            Args = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("hello"), Value.Text("world")) }),
            DeadlineMs = 1_700_000_030_000,
            Caller = PubBytes,
        };
        var signed = Envelope.Sign(StreamOpenFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "6070D8AB71F837591AC2C803C04F9E1D3FA01C9310D33C96A90434820C5E50550F9DEA8A764247EB49AF63447C037E192B7892A365C1A4ACB9BC46B98AA5670F",
            SigHex(signed));
        Assert.Equal(415, WireCodec.Encode(signed).Length);
    }

    /// <summary>
    /// Round-tripped against the SAME already-byte-verified construction
    /// above: since the previous test already proves the constructor's
    /// encoding is bit-for-bit correct, getting the same field values back
    /// out here proves the parser inverts it correctly too.
    /// </summary>
    [Fact]
    public void Parse_stream_open_round_trips_a_well_formed_frame()
    {
        var pubBytes = PubBytes;
        var args = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("hello"), Value.Text("world")) });
        var spec = new StreamOpenSpec
        {
            StreamId = StreamId,
            Procedure = "macula_rust_sdk.test_stream",
            Realm = VectorZeroRealm,
            Mode = StreamMode.ClientStream,
            Args = args,
            DeadlineMs = 1_700_000_030_000,
            Caller = pubBytes,
        };
        var frame = StreamOpenFrame.Build(spec, FrameId, VectorSentAtMs);
        var info = StreamOpenFrameParsing.Parse(frame);

        Assert.Equal(StreamId, info.StreamId);
        Assert.Equal("macula_rust_sdk.test_stream", info.Procedure);
        Assert.Equal(VectorZeroRealm, info.Realm);
        Assert.Equal(StreamMode.ClientStream, info.Mode);
        Assert.Equal(CborCodec.Encode(args), CborCodec.Encode(info.Args));
        Assert.Equal(1_700_000_030_000, info.DeadlineMs);
        Assert.Equal(pubBytes, info.Caller);
    }

    [Fact]
    public void Stream_data_raw_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamDataSpec
        {
            StreamId = StreamId,
            Seq = 0,
            Encoding = StreamEncoding.Raw,
            Body = Value.Bytes("raw chunk bytes"u8.ToArray()),
        };
        var signed = Envelope.Sign(StreamDataFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "35770744FE5BD01B86DDA01AB4EF855E4E4FE0EDFEDC89FF690728C585C60A5CB035717E3EA9133C4AD833E226F4DB95E9A5AF9AC59E7BACBB8BDF72611F8003",
            SigHex(signed));
        Assert.Equal(269, WireCodec.Encode(signed).Length);
    }

    /// <summary>
    /// The vector this SDK was missing until 2026-08-29: `Signer` present,
    /// matching what every real <see cref="Macula.Streaming.StreamHandle"/>
    /// call site now sends. Generated live against
    /// `macula_frame:stream_data/1` with `signer => Pub` in the spec map
    /// (`rebar3 shell`, same identity/frame_id/stream_id/sent_at_ms
    /// fixture as every vector in this file) -- not guessed from the
    /// field's shape. Ported verbatim from macula-rust's own vector
    /// of the same name, same fixture, same signature.
    /// </summary>
    [Fact]
    public void Stream_data_with_signer_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamDataSpec
        {
            StreamId = StreamId,
            Seq = 0,
            Encoding = StreamEncoding.Raw,
            Body = Value.Bytes("raw chunk bytes"u8.ToArray()),
            Signer = PubBytes,
        };
        var signed = Envelope.Sign(StreamDataFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "3EA0B6B6DB1549D2EA42AF015A477FCD6D00B11F48F9CC07AF0914CAC18F22B5C12E5EE446811388F207D688960B67D9BEE7B4D998BE02F2B1426B6C4A06D307",
            SigHex(signed));
        Assert.Equal(310, WireCodec.Encode(signed).Length);
    }

    /// <summary>
    /// The real point of this vector: `encoding = msgpack` with a
    /// structured body still matches the reference's signature
    /// byte-for-byte -- proving `body` is encoded as an ordinary nested
    /// CBOR value in the frame's own envelope, not pre-serialized through
    /// a separate msgpack codec.
    /// </summary>
    [Fact]
    public void Stream_data_msgpack_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamDataSpec
        {
            StreamId = StreamId,
            Seq = 1,
            Encoding = StreamEncoding.Msgpack,
            Body = Value.Map(new[]
            {
                new KeyValuePair<Value, Value>(Value.Text("a"), Value.UInt(1)),
                // `greeting`'s VALUE is a binary in the reference, not an
                // atom -- bytes, not text, unlike its (atom) key.
                new KeyValuePair<Value, Value>(Value.Text("greeting"), Value.Bytes("hi"u8.ToArray())),
            }),
        };
        var signed = Envelope.Sign(StreamDataFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "99CA90B0C01FD349DBAF317D03872E5F460426789874D79B6FBE37F4AC92C2AD690A00CDB3734F262D5C58C8F3BFD06F8AE892A8B5655274718A283ABA1D4D08",
            SigHex(signed));
        Assert.Equal(273, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Stream_end_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamEndSpec { StreamId = StreamId, Role = StreamRole.Send };
        var signed = Envelope.Sign(StreamEndFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "78F2B94BD5AC70901EABB31D8B17C89B58A88942300C6232545899AFB933B2C4B7399BB183A5660671981B6346DA27033C8F93A99E7EBA96F0F689B03D4F940A",
            SigHex(signed));
        Assert.Equal(239, WireCodec.Encode(signed).Length);
    }

    /// <summary>See Stream_data_with_signer's doc -- same fixture, same 2026-08-29 gap, this SDK's STREAM_END.</summary>
    [Fact]
    public void Stream_end_with_signer_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamEndSpec { StreamId = StreamId, Role = StreamRole.Send, Signer = PubBytes };
        var signed = Envelope.Sign(StreamEndFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "CC316B0A1C1AD4701AD16D8A140ED62D5DEEFD721C1CEB574CC8755C645CA27413EF9C6A6A9C4768564524C412515C14637A9D6BD4CCB8CD1ADD44F2A240C70C",
            SigHex(signed));
        Assert.Equal(280, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Stream_error_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamErrorSpec { StreamId = StreamId, Code = "cancelled", Message = "boom" };
        var signed = Envelope.Sign(StreamErrorFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "119F379518EC17C603ED5466A57D7AE53198A8AC4D5CA9849934A78994428CB3DAD40BC0EFECE1A0C8EEB0ACC28973C0F7E55DE6444827091814AF0715D9FF0B",
            SigHex(signed));
        Assert.Equal(259, WireCodec.Encode(signed).Length);
    }

    /// <summary>See Stream_data_with_signer's doc -- same fixture, same 2026-08-29 gap, this SDK's STREAM_ERROR.</summary>
    [Fact]
    public void Stream_error_with_signer_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamErrorSpec { StreamId = StreamId, Code = "cancelled", Message = "boom", Signer = PubBytes };
        var signed = Envelope.Sign(StreamErrorFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "223062E2816C5E6DABCF08A0A4FD01F477F2D1D933F2F1FDC971CAB570003DDE8192CC2F8811CE4A2D180B6781AFA64EB4057947E25CF121F745A9654DC23D0A",
            SigHex(signed));
        Assert.Equal(300, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Stream_reply_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new StreamReplySpec
        {
            StreamId = StreamId,
            Payload = Value.Map(new[] { new KeyValuePair<Value, Value>(Value.Text("ok"), Value.Text("true")) }),
            RespondedBy = PubBytes,
        };
        var signed = Envelope.Sign(StreamReplyFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "ADF57AD58B253F175ADF72E4717E078C62F3E22CBDDBF8DDC0DD8A47CAAA061E8A37C73BAAB91E450D1D8472021B6A0161169D77E9D186C436D3E6580D48C703",
            SigHex(signed));
        Assert.Equal(295, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Advertise_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new AdvertiseSpec { Realm = VectorZeroRealm, Procedure = "macula_rust_sdk.test_procedure", Advertiser = PubBytes };
        var signed = Envelope.Sign(AdvertiseFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "22AE051A542289279A56FB9C8587341232EF48208F9A8641C77F37E1B5D3D26A4B7C30CDCA4AE6E851FEB4E2FBF9C5B2469AFCC7317D59F5D775A05C99E99C0A",
            SigHex(signed));
        Assert.Equal(330, WireCodec.Encode(signed).Length);
    }

    [Fact]
    public void Unadvertise_frame_matches_the_reference_byte_for_byte()
    {
        var spec = new UnadvertiseSpec { Realm = VectorZeroRealm, Procedure = "macula_rust_sdk.test_procedure", Advertiser = PubBytes };
        var signed = Envelope.Sign(UnadvertiseFrame.Build(spec, FrameId, VectorSentAtMs), Identity);

        Assert.Equal(
            "C4111E5C2685DCDDB035B9DA29AD2A30D90BC7CAC09620A675D9A3DB480508FDAD7DCDD145B77607395DBF6195643BBA60C2C6D29E2DCFE5F70F20CF15DA2600",
            SigHex(signed));
        Assert.Equal(323, WireCodec.Encode(signed).Length);
    }
}
