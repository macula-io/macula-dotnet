using System.Text.Json;
using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>A call a served procedure answers: who called, its signature verified, and what it asked.</summary>
/// <param name="Caller">The calling node.</param>
/// <param name="Realm">The realm of the call.</param>
/// <param name="Procedure">The procedure called.</param>
/// <param name="Payload">The call's arguments.</param>
/// <param name="Deadline">When the caller stops waiting.</param>
public sealed record Request(MeshId Caller, MeshId Realm, string Procedure, JsonNode? Payload, DateTimeOffset Deadline)
{
    internal static Request FromJson(string text)
    {
        using var json = JsonDocument.Parse(text);
        var r = json.RootElement;
        return new Request(MeshId.Parse(r.GetProperty("caller").GetString()!),
            MeshId.Parse(r.GetProperty("realm").GetString()!), r.GetProperty("procedure").GetString()!,
            Macula.Payload.FromElement(r.GetProperty("payload")),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetProperty("deadline_ms").GetInt64()));
    }
}

/// <summary>
/// A procedure this node serves. Each call, or each session of a streaming procedure, runs its handler
/// on its own task. Dispose it to withdraw the procedure everywhere.
/// </summary>
public sealed class Served : IAsyncDisposable
{
    private readonly ServedHandle _handle;
    private readonly NativePump<(nuint Item, string Request)> _pump;
    private readonly Task _dispatch;
    private readonly CancellationTokenSource _stopping = new();

    private Served(ServedHandle handle, Func<nuint, Request, CancellationToken, Task> run)
    {
        _handle = handle;
        _pump = new NativePump<(nuint, string)>("macula served", 64, token =>
        {
            nint err = 0;
            var text = NativeCall.TakeString(
                Libmacula.macula_served_next(_handle, 0, token, out var item, out var closed, ref err));
            NativeCall.Check(err);
            return closed == 1 || text is null ? new(default, true) : new((item, text), false);
        });
        _dispatch = DispatchAsync(run);
    }

    // Each taken call or session runs on a task of its own; the procedure's
    // handler is never waited on before the next is taken.
    private async Task DispatchAsync(Func<nuint, Request, CancellationToken, Task> run)
    {
        var running = new List<Task>();
        await foreach (var (item, text) in _pump.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            running.RemoveAll(t => t.IsCompleted);
            running.Add(Task.Run(() => run(item, Request.FromJson(text), _stopping.Token)));
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    internal static Served Unary(ServedHandle handle,
        Func<Request, CancellationToken, ValueTask<JsonNode?>> handler) =>
        new(handle, async (pending, request, stopping) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            var left = request.Deadline - DateTimeOffset.UtcNow;
            deadline.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            string? failure;
            try
            {
                var result = await handler(request, deadline.Token).ConfigureAwait(false);
                failure = Answer(() =>
                {
                    nint err = 0;
                    Libmacula.macula_pending_reply(pending, Payload.ToJson(result), ref err);
                    return err;
                });
            }
            catch (Exception e)
            {
                failure = e.Message;
            }
            if (failure is not null)
            {
                // The caller receives the message as a handler_error's
                // detail: the handler's own exception, or why its result
                // could not be sent (a boolean in it, say).
                Answer(() =>
                {
                    nint err = 0;
                    Libmacula.macula_pending_error(pending, failure, ref err);
                    return err;
                });
            }
        });

    // Answers a pending call: null when it went, or why it did not. A call
    // already answered (at its deadline, for us) needs nothing more.
    private static string? Answer(Func<nint> send)
    {
        var err = send();
        if (err == 0)
        {
            return null;
        }
        var failure = Errors.FromJson(NativeCall.TakeString(err)!, default);
        return failure is MaculaException { Kind: ErrorKind.Answered } or ObjectDisposedException ? null : failure.Message;
    }

    internal static Served Streaming(ServedHandle handle, Func<MeshStream, CancellationToken, Task> handler) =>
        new(handle, async (session, _, stopping) =>
        {
            await using var stream = new MeshStream(new StreamHandle(session));
            try
            {
                await handler(stream, stopping).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                stream.TryAbort("handler_error", e.Message);
            }
        });

    /// <summary>Withdraws the procedure everywhere; calls and sessions already running finish.</summary>
    public async ValueTask DisposeAsync()
    {
        await _pump.DisposeAsync().ConfigureAwait(false);
        _handle.Dispose();
        await _dispatch.ConfigureAwait(false);
        await _stopping.CancelAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}

public sealed partial class Pool
{
    /// <summary>
    /// Serves <paramref name="procedure"/> in <paramref name="realm"/>: in this node's own namespace
    /// (<see cref="OwnProcedure"/>), or under an org that delegated it to this node. Each call runs
    /// <paramref name="handler"/>, whose cancellation token ends at the call's deadline; its result is
    /// the reply, and an exception it throws answers the caller with a <c>handler_error</c> carrying the
    /// exception's message. With a <paramref name="policy"/>, only calls whose UCAN it accepts reach the
    /// handler; the rest are answered <c>unauthorized</c> (<see cref="AuthPolicy"/>).
    /// </summary>
    public unsafe Served Serve(MeshId realm, string procedure, Func<Request, CancellationToken, ValueTask<JsonNode?>> handler,
        AuthPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(handler);
        var policyJson = policy?.ToJson().ToJsonString();
        nint err = 0;
        ServedHandle handle;
        fixed (byte* r = realm.Bytes)
        {
            handle = policyJson is null
                ? Libmacula.macula_pool_serve(Handle, r, procedure, ref err)
                : Libmacula.macula_pool_serve_gated(Handle, r, procedure, policyJson, ref err);
        }
        NativeCall.Check(err);
        return Served.Unary(handle, handler);
    }

    /// <summary>
    /// Serves <paramref name="procedure"/> in <paramref name="realm"/> as a stream of
    /// <paramref name="mode"/>. Each session runs <paramref name="handler"/> with its stream, which ends
    /// when the handler returns; an exception it throws aborts the stream with <c>handler_error</c>. With a
    /// <paramref name="policy"/>, only opens whose UCAN it accepts start a session; the rest are refused with
    /// a stream error of code <c>unauthorized</c> (<see cref="AuthPolicy"/>).
    /// </summary>
    public unsafe Served ServeStream(MeshId realm, string procedure, StreamMode mode,
        Func<MeshStream, CancellationToken, Task> handler, AuthPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        ArgumentNullException.ThrowIfNull(handler);
        var policyJson = policy?.ToJson().ToJsonString();
        nint err = 0;
        ServedHandle handle;
        fixed (byte* r = realm.Bytes)
        {
            handle = policyJson is null
                ? Libmacula.macula_pool_serve_stream(Handle, r, procedure, (int)mode, ref err)
                : Libmacula.macula_pool_serve_stream_gated(Handle, r, procedure, (int)mode, policyJson, ref err);
        }
        NativeCall.Check(err);
        return Served.Streaming(handle, handler);
    }
}
