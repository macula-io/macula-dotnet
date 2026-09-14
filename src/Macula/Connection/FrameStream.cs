using System.Net.Quic;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Connection;

/// <summary>
/// Reads and writes length-prefixed CBOR frames (<see cref="WireCodec"/>)
/// over any duplex byte stream. Backs both the control stream and any
/// dedicated stream (content transfer, streaming RPC) -- the framing is
/// identical either way, only the stream's purpose differs.
/// </summary>
public sealed class FrameStream
{
    private readonly Stream _stream;
    private byte[] _buf = new byte[4096];
    private int _len;
    private readonly byte[] _readScratch = new byte[8192];

    // QuicStream fails a write that starts while another is still pending,
    // so senders sharing this stream take turns here.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    private readonly StreamEnds _ends;

    public FrameStream(Stream stream)
        : this(stream, QuicEnds(stream))
    {
    }

    internal FrameStream(Stream stream, StreamEnds ends)
    {
        _stream = stream;
        _ends = ends;
    }

    /// <summary>
    /// The warnings of the session this dedicated stream belongs to, where a
    /// reply it drops is warned about. None for a stream of no session.
    /// </summary>
    internal DropWarnings? DropWarnings { get; init; }

    /// <summary>
    /// How a dedicated stream ends short of a normal close, each with a code
    /// and then freed: both directions aborted, or the send side finished and
    /// the receive side stopped.
    /// </summary>
    internal sealed record StreamEnds(Func<long, ValueTask> AbortBoth, Func<long, ValueTask> FinishAndStopReading);

    /// <summary>
    /// Aborts both directions with <paramref name="code"/>, writing nothing:
    /// RESET_STREAM on the send side and STOP_SENDING on the receive side.
    /// Then frees the stream.
    /// </summary>
    internal ValueTask AbortBothAsync(long code) => _ends.AbortBoth(code);

    /// <summary>
    /// Finishes the send side, so what was written still reaches the peer,
    /// stops the receive side with <paramref name="code"/>, then frees the
    /// stream.
    /// </summary>
    internal ValueTask FinishAndStopReadingAsync(long code) => _ends.FinishAndStopReading(code);

    private static StreamEnds QuicEnds(Stream stream) => new(
        async code =>
        {
            if (stream is QuicStream quic && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()))
            {
                quic.Abort(QuicAbortDirection.Both, code);
            }
            await stream.DisposeAsync().ConfigureAwait(false);
        },
        async code =>
        {
            if (stream is QuicStream quic && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()))
            {
                quic.CompleteWrites();
                quic.Abort(QuicAbortDirection.Read, code);
            }
            await stream.DisposeAsync().ConfigureAwait(false);
        });

    /// <summary>Writes one frame whole. Safe to call from several tasks at once: their sends take turns.</summary>
    public async Task SendFrameAsync(Value frame, CancellationToken ct = default)
    {
        var bytes = WireCodec.Encode(frame);
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Blocks for the next complete frame. Throws <see cref="EndOfStreamException"/> if the stream ends first.</summary>
    public async Task<Value> RecvFrameAsync(CancellationToken ct = default)
    {
        while (true)
        {
            if (TryTakeFrame(out var frame))
            {
                return frame;
            }

            var n = await _stream.ReadAsync(_readScratch, ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("stream closed before a complete frame arrived");
            }
            Append(_readScratch.AsSpan(0, n));
        }
    }

    /// <summary>
    /// Send a signed CALL and wait for the matching RESULT or ERROR,
    /// correlated by call_id. On this stream specifically (a dedicated
    /// stream, never the control stream) nothing else ever arrives to
    /// discard -- the same "one thing at a time" limitation
    /// <see cref="Session.CallAsync"/> documents for the control stream
    /// doesn't apply here, since a dedicated stream carries only this one
    /// exchange's frames.
    /// </summary>
    public Task<CallResponse> CallAsync(string procedure, byte[] realm, Value payload, long deadlineMs, KeyPair identity, TimeSpan timeout, CancellationToken ct = default) =>
        CallAsync(procedure, realm, payload, deadlineMs, identity, timeout, Array.Empty<byte>(), ct);

    /// <summary>As <see cref="CallAsync(string, byte[], Value, long, KeyPair, TimeSpan, CancellationToken)"/>, attaching ucanToken to the outgoing CALL's ucan_token field -- for a procedure gated by <c>Macula.Ucan.Policy.Required</c> on the provider side.</summary>
    public async Task<CallResponse> CallAsync(string procedure, byte[] realm, Value payload, long deadlineMs, KeyPair identity, TimeSpan timeout, byte[] ucanToken, CancellationToken ct = default)
    {
        var callId = new byte[16];
        Random.Shared.NextBytes(callId);
        var spec = new CallSpec
        {
            CallId = callId,
            Procedure = procedure,
            Realm = realm,
            Payload = payload,
            DeadlineMs = deadlineMs,
            Caller = identity.NodeId(),
            UcanToken = ucanToken,
        };
        var signed = Envelope.Sign(CallFrame.Build(spec), identity);
        await SendFrameAsync(signed, ct).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var value = await RecvFrameAsync(timeoutCts.Token).ConfigureAwait(false);
                var gotCallId = CallFrameParsing.FrameCallId(value);
                if (gotCallId is null || !gotCallId.AsSpan().SequenceEqual(callId))
                {
                    continue;
                }
                try
                {
                    return CallFrameParsing.ParseCallResponse(value);
                }
                catch (ParseFrameException)
                {
                    // matching call_id, unexpected shape: keep waiting.
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no response for call_id {Convert.ToHexStringLower(callId)} within {timeout}");
        }
    }

    private bool TryTakeFrame(out Value frame)
    {
        var decoded = WireCodec.Decode(_buf.AsSpan(0, _len));
        if (decoded is Decoded.Frame f)
        {
            frame = f.Value;
            var remaining = _len - f.Consumed;
            if (remaining > 0)
            {
                Buffer.BlockCopy(_buf, f.Consumed, _buf, 0, remaining);
            }
            _len = remaining;
            return true;
        }

        frame = null!;
        return false;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(_len + data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
        {
            return;
        }
        var newSize = _buf.Length;
        while (newSize < needed)
        {
            newSize *= 2;
        }
        Array.Resize(ref _buf, newSize);
    }
}
