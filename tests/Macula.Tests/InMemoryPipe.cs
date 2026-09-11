using System.Threading.Channels;

namespace Macula.Tests;

/// <summary>
/// One end of an in-memory duplex byte stream: what one end writes, the other
/// end reads, in order. <see cref="Hangup"/> ends what the other end reads, the
/// way a closed QUIC stream does.
/// </summary>
internal sealed class InMemoryPipe : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;
    private byte[]? _pending;
    private int _offset;

    private InMemoryPipe(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    internal static (InMemoryPipe Client, InMemoryPipe Station) CreatePair()
    {
        var toStation = Channel.CreateUnbounded<byte[]>();
        var toClient = Channel.CreateUnbounded<byte[]>();
        return (new InMemoryPipe(toClient, toStation), new InMemoryPipe(toStation, toClient));
    }

    internal void Hangup() => _outgoing.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending is null || _offset == _pending.Length)
        {
            if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }
            if (_incoming.Reader.TryRead(out var chunk))
            {
                (_pending, _offset) = (chunk, 0);
            }
        }
        var count = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _outgoing.Writer.TryWrite(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public override bool CanRead => true;
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
