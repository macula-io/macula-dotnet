using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>Which ways a stream's data flows.</summary>
public enum StreamMode
{
    /// <summary>The provider sends, the caller receives.</summary>
    Server = 0,
    /// <summary>The caller sends, the provider receives.</summary>
    Client = 1,
    /// <summary>Both send.</summary>
    Bidi = 2,
}

/// <summary>One frame of a stream the peer sent.</summary>
public abstract record StreamFrame;

/// <summary>A chunk of data: bytes (<c>raw</c>) or a value (<c>msgpack</c>).</summary>
public sealed record StreamData(string Encoding, JsonNode? Body) : StreamFrame;

/// <summary>The peer closed its send side (<c>send</c>) or the whole stream (<c>both</c>).</summary>
public sealed record StreamEnd(string Role) : StreamFrame;

/// <summary>The provider's terminal value.</summary>
public sealed record StreamReply(JsonNode? Payload) : StreamFrame;

/// <summary>The stream ended normally.</summary>
public sealed record StreamEof : StreamFrame;

/// <summary>The stream ended with an error: the peer's, or a station's (<see cref="Relay"/>).</summary>
public sealed record StreamFailed(string Code, string Message, bool Relay) : StreamFrame;

/// <summary>
/// A streaming session, opened by a caller (<see cref="Pool.OpenStreamAsync"/>) or handed to a
/// provider's handler (<see cref="Pool.ServeStream"/>). Dispose it to free it, which aborts it when it
/// has not ended.
/// </summary>
public sealed class MeshStream : IAsyncDisposable
{
    private readonly StreamHandle _handle;
    private NativePump<StreamFrame>? _pump;
    private readonly object _pumpLock = new();

    internal MeshStream(StreamHandle handle) => _handle = handle;

    /// <summary>The call that opened the stream.</summary>
    public Request Request
    {
        get
        {
            nint err = 0;
            var text = NativeCall.TakeString(Libmacula.macula_stream_request(_handle, ref err));
            NativeCall.Check(err);
            return Request.FromJson(text!);
        }
    }

    /// <summary>
    /// This caller's seal report for the stream (<see cref="SealReport"/>). It settles on the provider's first data or
    /// reply opened under the stream's key, after which no reseal can happen, or on a clear stream on its first data,
    /// reply or end; a stream that settled keeps it after it ends. Before it settles, and on a stream that ended
    /// first, it throws a <see cref="MaculaException"/> of kind <see cref="ErrorKind.NotSettled"/>; on a served stream,
    /// of kind <see cref="ErrorKind.NotACaller"/>.
    /// </summary>
    public SealReport Report()
    {
        nint err = 0;
        var text = NativeCall.TakeString(Libmacula.macula_stream_report(_handle, ref err));
        NativeCall.Check(err);
        using var json = JsonDocument.Parse(text!);
        return SealReport.FromElement(json.RootElement);
    }

    /// <summary>Sends a chunk of bytes.</summary>
    public unsafe void Send(ReadOnlySpan<byte> data)
    {
        nint err = 0;
        fixed (byte* d = data)
        {
            Libmacula.macula_stream_send_bytes(_handle, d, (nuint)data.Length, ref err);
        }
        NativeCall.Check(err);
    }

    /// <summary>Sends a value.</summary>
    public void Send(JsonNode? value)
    {
        nint err = 0;
        Libmacula.macula_stream_send_json(_handle, Payload.ToJson(value), ref err);
        NativeCall.Check(err);
    }

    /// <summary>Ends this side's sending; the peer may still send.</summary>
    public void CloseSend()
    {
        nint err = 0;
        Libmacula.macula_stream_close_send(_handle, ref err);
        NativeCall.Check(err);
    }

    /// <summary>Sends the provider's terminal value, which ends the stream.</summary>
    public void Reply(JsonNode? payload)
    {
        nint err = 0;
        Libmacula.macula_stream_reply(_handle, Payload.ToJson(payload), ref err);
        NativeCall.Check(err);
    }

    /// <summary>Ends the stream on both sides.</summary>
    public void Close()
    {
        nint err = 0;
        Libmacula.macula_stream_close(_handle, ref err);
        NativeCall.Check(err);
    }

    /// <summary>Ends the stream with an error the peer receives.</summary>
    public void Abort(string code, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(message);
        nint err = 0;
        Libmacula.macula_stream_abort(_handle, code, message, ref err);
        NativeCall.Check(err);
    }

    // Close, where a stream the handler already ended needs nothing more.
    internal void TryClose()
    {
        nint err = 0;
        Libmacula.macula_stream_close(_handle, ref err);
        if (err != 0)
        {
            Libmacula.macula_free_string(err);
        }
    }

    internal void TryAbort(string code, string message)
    {
        nint err = 0;
        Libmacula.macula_stream_abort(_handle, code, message, ref err);
        if (err != 0)
        {
            Libmacula.macula_free_string(err);
        }
    }

    /// <summary>
    /// The peer's frames as they arrive, ending after the stream's own end (<see cref="StreamEof"/> or
    /// <see cref="StreamFailed"/>), which is yielded last.
    /// </summary>
    public async IAsyncEnumerable<StreamFrame> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in Pump().Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private NativePump<StreamFrame> Pump()
    {
        lock (_pumpLock)
        {
            return _pump ??= new NativePump<StreamFrame>("macula stream", 64, token =>
            {
                nint err = 0;
                var text = NativeCall.TakeString(Libmacula.macula_stream_recv(_handle, 0, token, ref err));
                NativeCall.Check(err);
                var frame = FrameOf(text!);
                // The end is handed over, then the pump stops.
                return new(frame, false);
            }, stopAfter: static frame => frame is StreamEof or StreamFailed);
        }
    }

    private static StreamFrame FrameOf(string text)
    {
        using var json = JsonDocument.Parse(text);
        var f = json.RootElement;
        return f.GetProperty("kind").GetString() switch
        {
            "data" => new StreamData(f.GetProperty("encoding").GetString()!, Payload.FromElement(f.GetProperty("body"))),
            "end" => new StreamEnd(f.GetProperty("role").GetString()!),
            "reply" => new StreamReply(Payload.FromElement(f.GetProperty("payload"))),
            "eof" => new StreamEof(),
            _ => new StreamFailed(f.GetProperty("code").GetString()!, f.GetProperty("message").GetString()!,
                f.GetProperty("relay").GetInt32() == 1),
        };
    }

    /// <summary>Frees the stream, aborting it when it has not ended.</summary>
    public async ValueTask DisposeAsync()
    {
        NativePump<StreamFrame>? pump;
        lock (_pumpLock)
        {
            pump = _pump;
        }
        if (pump is not null)
        {
            await pump.DisposeAsync().ConfigureAwait(false);
        }
        _handle.Dispose();
    }
}

public sealed partial class Pool
{
    /// <summary>
    /// Opens a stream of <paramref name="mode"/> on <paramref name="procedure"/> in <paramref name="realm"/>
    /// by direct dial, with <paramref name="payload"/> as its opening arguments.
    /// </summary>
    /// <param name="realm">The realm.</param>
    /// <param name="procedure">The streaming procedure.</param>
    /// <param name="mode">The stream's mode, which must be the procedure's.</param>
    /// <param name="payload">The opening arguments.</param>
    /// <param name="provider">The provider to open it at; any the realm trusts when null.</param>
    /// <param name="deadline">
    /// How far ahead the open's signed deadline lies (30 s when null). It bounds the provider's admission of
    /// the open, not the stream's life (cabi/CONTRACT.md "Serving and streams").
    /// </param>
    /// <param name="timeout">How long opening it may take.</param>
    /// <param name="ucan">
    /// A UCAN and its chain's proofs, for a gated procedure (<see cref="Ucan"/>); a provider that refuses it
    /// ends the stream with a <see cref="StreamFailed"/> of code <c>unauthorized</c>.
    /// </param>
    /// <param name="confidential">
    /// Whether the stream is sealed, as <see cref="CallOptions.Confidential"/>; one that could not be kept confidential
    /// throws a <see cref="ConfidentialityException"/> here. Its seal report is <see cref="MeshStream.Report"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the opening.</param>
    public async Task<MeshStream> OpenStreamAsync(MeshId realm, string procedure, StreamMode mode,
        JsonNode? payload = null, MeshId? provider = null, TimeSpan? deadline = null, TimeSpan? timeout = null,
        UcanPresentation? ucan = null, Confidential? confidential = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        var payloadJson = Payload.ToJson(payload);
        var deadlineMs = Milliseconds(deadline);
        var optionsJson = Sealing.CallOptionsJson(provider, ucan, confidential, report: false);
        var timeoutMs = Milliseconds(timeout);
        var handle = await NativeCall.RunAsync(token =>
        {
            nint err = 0;
            StreamHandle h;
            unsafe
            {
                fixed (byte* r = realm.Bytes)
                {
                    h = Libmacula.macula_pool_open_stream_opts(Handle, r, procedure, (int)mode, payloadJson, optionsJson,
                        deadlineMs, timeoutMs, token, ref err);
                }
            }
            NativeCall.Check(err, cancellationToken);
            return h;
        }, cancellationToken).ConfigureAwait(false);
        return new MeshStream(handle);
    }
}
