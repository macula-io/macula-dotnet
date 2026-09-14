using System.Diagnostics;
using System.Security.Cryptography;
using Macula.Bolt4;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Sending frames from several tasks at once on one <see cref="FrameStream"/>,
/// over a stream that behaves like System.Net.Quic's QuicStream: a write that
/// starts while another is still pending fails with
/// InvalidOperationException instead of being queued. And a call on a
/// dedicated stream over an in-memory pipe, whose reply tests have the Go
/// tests' names.
/// </summary>
public class FrameStreamTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task Concurrent_sends_all_complete_as_whole_frames()
    {
        var wire = new OverlapRejectingStream();
        var frames = new FrameStream(wire);
        var sent = Enumerable.Range(0, 32).Select(Numbered).ToList();

        await Task.WhenAll(sent.Select(frame => Task.Run(() => frames.SendFrameAsync(frame))));

        var received = DecodeAll(wire.Written());
        Assert.Equal(sent.Select(SeqOf).Order(), received.Select(SeqOf).Order());
    }

    /// <summary>
    /// A RESULT or ERROR for the call on a dedicated stream counts only when it
    /// verifies against its responded_by or reported_by: a forged or unsigned
    /// one is dropped and the call keeps waiting for the genuine reply.
    /// </summary>
    [Fact]
    public async Task A_dedicated_stream_reply_that_does_not_verify_leaves_the_call_waiting()
    {
        var responder = KeyPair.Generate();
        var other = KeyPair.Generate();
        var notVerifying = new (string Name, Func<byte[], Value.MapValue> Reply)[]
        {
            ("a forged result", callId => Envelope.Sign(ResultFrom(responder, callId, "forged"), other)),
            ("an unsigned result", callId => ResultFrom(responder, callId, "unsigned")),
            ("a forged error", callId => Envelope.Sign(CallErrorFrame.Build(new CallErrorSpec { CallId = callId, Code = Bolt4Code.UnknownNextPeer, ReportedBy = responder.NodeId() }), other)),
        };

        foreach (var (name, first) in notVerifying)
        {
            var response = await DedicatedCallAsync(null, first, Genuine(responder));

            Assert.True(IsGenuine(response), $"{name} reply first: the call got {response}");
        }
    }

    /// <summary>
    /// A reply dropped on a dedicated stream is warned about on the session the
    /// stream belongs to, as a dropped reply with its call_id prefix.
    /// </summary>
    [Fact]
    public async Task A_dedicated_stream_reply_dropped_is_warned_about_on_its_session()
    {
        var station = RandomNumberGenerator.GetBytes(32);
        var warnings = new DropWarnings(station) { Interval = Short };
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            var responder = KeyPair.Generate();
            var otherCall = new byte[] { 0xca, 0xfe, 0xba, 0xbe }.Concat(new byte[12]).ToArray();
            var forgedPrefix = "";
            Func<byte[], Value.MapValue> forged = callId =>
            {
                forgedPrefix = Convert.ToHexString(callId.AsSpan(0, 4));
                return Envelope.Sign(ResultFrom(responder, callId, "forged"), KeyPair.Generate());
            };
            Func<byte[], Value.MapValue> forAnotherCall = _ => Envelope.Sign(ResultFrom(responder, otherCall, "late"), responder);

            var response = await DedicatedCallAsync(warnings, forged, forAnotherCall, Genuine(responder));
            await Task.Delay(Short * 3);

            Assert.True(IsGenuine(response), $"a forged and a misaddressed reply first: the call got {response}");
            var lines = listener.Events.Where(e => e.Message.Contains(Convert.ToHexStringLower(station))).ToList();
            Assert.Equal(2, lines.Count);
            Assert.Contains($"kind=dropped_reply count=1 reason=invalid_signature call_id={forgedPrefix}", lines[0].Message);
            Assert.Contains("kind=dropped_reply count=1 reason=unknown_call_id call_id=CAFEBABE", lines[1].Message);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    // Makes one call on a dedicated stream carrying warnings, answers it with
    // each of replies in turn, built from the CALL's call_id, and returns what
    // the call got.
    private static async Task<CallResponse> DedicatedCallAsync(DropWarnings? warnings, params Func<byte[], Value.MapValue>[] replies)
    {
        var (caller, provider) = InMemoryPipe.CreatePair();
        var stream = new FrameStream(caller) { DropWarnings = warnings };
        var peer = new FrameStream(provider);
        var call = stream.CallAsync("_content.get_block", new byte[32], Value.Null, 0, KeyPair.Generate(), Wait);
        var sent = Assert.IsType<Value.MapValue>(await peer.RecvFrameAsync().WaitAsync(Wait));
        var callId = Assert.IsType<Value.BytesValue>(sent.Get("call_id")).Value;
        foreach (var reply in replies)
        {
            await peer.SendFrameAsync(reply(callId));
        }
        return await call.WaitAsync(Wait);
    }

    private static Func<byte[], Value.MapValue> Genuine(KeyPair responder) =>
        callId => Envelope.Sign(ResultFrom(responder, callId, "genuine"), responder);

    private static bool IsGenuine(CallResponse response) =>
        response is CallResponse.Result { Payload: Value.TextValue tag } && tag.AsText() == "genuine";

    private static Value.MapValue ResultFrom(KeyPair responder, byte[] callId, string tag) =>
        ResultFrame.Build(new ResultSpec { CallId = callId, Payload = Value.Text(tag), RespondedBy = responder.NodeId() });

    private static Value Numbered(int seq) =>
        Value.Map(new List<KeyValuePair<Value, Value>> { new(Value.Text("seq"), Value.UInt((ulong)seq)) });

    private static long SeqOf(Value frame) =>
        Assert.IsType<Value.MapValue>(frame).Entries.Single(entry => entry.Key.AsText() == "seq").Value.AsInt();

    // Every byte written must belong to a whole frame: a partial frame at the
    // end fails the IsType check.
    private static List<Value> DecodeAll(byte[] written)
    {
        var frames = new List<Value>();
        var offset = 0;
        while (offset < written.Length)
        {
            var frame = Assert.IsType<Decoded.Frame>(WireCodec.Decode(written.AsSpan(offset)));
            frames.Add(frame.Value);
            offset += frame.Consumed;
        }
        return frames;
    }

    private sealed class OverlapRejectingStream : Stream
    {
        private readonly MemoryStream _written = new();
        private int _writing;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _writing, 1) == 1)
            {
                throw new InvalidOperationException("a write is already in progress on this stream");
            }
            try
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                lock (_written)
                {
                    _written.Write(buffer.Span);
                }
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
        }

        internal byte[] Written()
        {
            lock (_written)
            {
                return _written.ToArray();
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
