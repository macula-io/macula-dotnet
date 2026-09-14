// Writes one sample of every frame type macula-dotnet sends, each exactly as
// its encoder writes it to a stream: a 4-byte big-endian length, then the CBOR
// body. Frames are built with the SDK's own builders and signed as the SDK
// signs them, with throwaway identities. DHT and content operations go out as
// CALL frames to _dht.* and _content.* procedures, so they have no frame types
// of their own; call.bin carries a _dht.find_records request.
//
// Usage: dotnet run -- <output dir>

using Macula;
using Macula.Bolt4;
using Macula.Frame;
using Macula.Identity;

var dir = args.Length > 0 ? args[0] : throw new ArgumentException("an output directory");
Directory.CreateDirectory(dir);

var node = KeyPair.GenerateWithDefaultPuzzle();
var realm = Enumerable.Repeat((byte)0x2a, 32).ToArray();
var callId = Enumerable.Repeat((byte)0x11, 16).ToArray();
var streamId = Enumerable.Repeat((byte)0x22, 16).ToArray();
var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

var frames = new List<Value.MapValue>
{
    ConnectFrame.Build(ConnectSpec.New(node.PublicBytes(), node.PuzzleEvidence())),
    CallFrame.Build(new CallSpec
    {
        CallId = callId,
        Procedure = "_dht.find_records",
        Realm = new byte[32],
        Payload = Value.Map([new(Value.Text("key"), Value.Bytes(Enumerable.Repeat((byte)0x33, 32).ToArray()))]),
        DeadlineMs = nowMs + 5_000,
        Caller = node.NodeId(),
    }),
    ResultFrame.Build(new ResultSpec { CallId = callId, Payload = Value.Text("answered"), RespondedBy = node.NodeId() }),
    CallErrorFrame.Build(new CallErrorSpec { CallId = callId, Code = Bolt4Code.UnknownError, ReportedBy = node.NodeId(), Detail = "the handler failed" }),
    PublishFrame.Build(new PublishSpec
    {
        Topic = "app/orders/placed",
        Realm = realm,
        Publisher = node.NodeId(),
        Seq = 1,
        Payload = Value.Text("order 1"),
        PublishedAtMs = (ulong)nowMs,
    }),
    SubscribeFrame.Build(new SubscribeSpec { Topic = "app/orders/*", Realm = realm, Subscriber = node.NodeId() }),
    UnsubscribeFrame.Build(new UnsubscribeSpec { Topic = "app/orders/*", Realm = realm, Subscriber = node.NodeId() }),
    AdvertiseFrame.Build(new AdvertiseSpec { Realm = realm, Procedure = "app/echo", Advertiser = node.NodeId() }),
    UnadvertiseFrame.Build(new UnadvertiseSpec { Realm = realm, Procedure = "app/echo", Advertiser = node.NodeId() }),
    StreamOpenFrame.Build(new StreamOpenSpec
    {
        StreamId = streamId,
        Procedure = "app/stream",
        Realm = realm,
        Mode = StreamMode.ServerStream,
        Args = Value.Map([new(Value.Text("n"), Value.Int(21))]),
        DeadlineMs = nowMs + 5_000,
        Caller = node.NodeId(),
    }),
    StreamDataFrame.Build(new StreamDataSpec { StreamId = streamId, Seq = 0, Encoding = StreamEncoding.Raw, Body = Value.Bytes("a chunk"u8.ToArray()), Signer = node.NodeId() }),
    StreamEndFrame.Build(new StreamEndSpec { StreamId = streamId, Role = StreamRole.Send, Signer = node.NodeId() }),
    StreamErrorFrame.Build(new StreamErrorSpec { StreamId = streamId, Code = "unauthorized", Message = "not authorized for this procedure", Signer = node.NodeId() }),
    StreamReplyFrame.Build(new StreamReplySpec { StreamId = streamId, Payload = Value.Text("done"), RespondedBy = node.NodeId() }),
    GoodbyeFrame.Build("normal", "done"),
};

foreach (var unsigned in frames)
{
    var signed = Envelope.Sign(unsigned, node);
    var frameType = signed.Get("frame_type")!.AsText();
    var bytes = WireCodec.Encode(signed);
    File.WriteAllBytes(Path.Combine(dir, $"{frameType}.bin"), bytes);
    Console.WriteLine($"{frameType}.bin {bytes.Length} bytes");
}
