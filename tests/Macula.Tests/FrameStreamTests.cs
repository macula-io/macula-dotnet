using Macula.Connection;
using Macula.Frame;

namespace Macula.Tests;

/// <summary>
/// Sending frames from several tasks at once on one <see cref="FrameStream"/>,
/// over a stream that behaves like System.Net.Quic's QuicStream: a write that
/// starts while another is still pending fails with
/// InvalidOperationException instead of being queued.
/// </summary>
public class FrameStreamTests
{
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
