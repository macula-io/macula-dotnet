using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Macula.Bolt4;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Connection;

/// <summary>
/// A <see cref="Subscription"/> fell more than 256 events behind. It ends that
/// subscription only; the session stays up.
/// </summary>
public sealed class ConsumerOverflowException : Exception
{
    public ConsumerOverflowException(string message) : base(message) { }
}

/// <summary>
/// The station sent a frame that only belongs to the handshake (HELLO or
/// CONNECT) after it. The control stream can't be trusted to be in step any
/// more, so the session ends.
/// </summary>
public sealed class ProtocolViolationException : IOException
{
    public ProtocolViolationException(string message) : base(message) { }
}

/// <summary>
/// A write on a session's control stream stalled for longer than the send
/// timeout of 30 seconds. The frame may be half written, so the station can't
/// read anything after it in step: the session ends with this reason, and a
/// <see cref="StationPool"/> link dials again.
/// </summary>
public sealed class SendTimeoutException : IOException
{
    public SendTimeoutException(string message) : base(message) { }
}

/// <summary>
/// A call ran out of its own timeout. <see cref="WriteStarted"/> says how far
/// its CALL got: false when it was still waiting for its turn to write, so it
/// was never sent and may be tried elsewhere; true when its write had started,
/// so the station may have it and it must not be sent again. A reply that
/// arrives after the timeout is counted as unrouted.
/// </summary>
public sealed class CallTimeoutException : TimeoutException
{
    public CallTimeoutException(string message, bool writeStarted) : base(message)
    {
        WriteStarted = writeStarted;
    }

    public bool WriteStarted { get; }
}

/// <summary>
/// Reads a session's control stream and routes every frame to whatever waits
/// for it: a RESULT or ERROR to its call, an EVENT to each subscription whose
/// realm and topic match, and a CALL signed by its caller to the inbound call
/// queue. GOODBYE, or HELLO or CONNECT after the handshake, ends the channel.
/// Any other frame is dropped and counted by type, with at most one trace line
/// per type per minute.
///
/// Writers take turns. Waiting for a turn is bounded by the caller's deadline:
/// a call's own timeout, and the send timeout for every other frame. A write
/// in progress is bounded by the send timeout, and one that stalls past it
/// ends the channel. The reader never waits on a write. The frames a session
/// sends on its own account, the replies the reader makes and the facts the
/// session announces, are handed to a writer of their own, and one is dropped
/// when 64 already wait there.
///
/// When the channel ends, every waiting call fails with the reason, every
/// subscription and the call queue end with it, and onEnded runs once.
/// </summary>
internal sealed class ControlChannel
{
    internal const int EventQueueCapacity = 256;
    internal const int CallQueueCapacity = 64;
    internal const int HandOffCapacity = 64;
    internal static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

    private readonly FrameStream _frames;
    private readonly KeyPair _identity;
    private readonly byte[] _stationId;
    private readonly Action<Exception> _onEnded;
    private readonly TimeSpan _sendTimeout;
    // FrameStream makes its writers take turns as well. Only this channel
    // writes the control stream, so that turn is always free to a writer
    // holding this one.
    private readonly SemaphoreSlim _writeTurn = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CallResponse>> _calls = new();
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    // Subscribing and removing take turns across the count change and the
    // SUBSCRIBE or UNSUBSCRIBE it sends, so the frames reach the station in
    // the order the counts changed.
    private readonly SemaphoreSlim _subscriptionTurn = new(1, 1);
    private readonly Channel<CallInfo> _inboundCalls = Channel.CreateBounded<CallInfo>(
        new BoundedChannelOptions(CallQueueCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<Value.MapValue> _handOff = Channel.CreateBounded<Value.MapValue>(
        new BoundedChannelOptions(HandOffCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<string, long> _unrouted = new();
    private readonly Dictionary<string, (long Dropped, long? LoggedAt)> _logged = new();
    private readonly TaskCompletionSource<Exception> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();

    internal ControlChannel(FrameStream frames, KeyPair identity, byte[] stationId, Action<Exception> onEnded, TimeSpan? sendTimeout = null)
    {
        _frames = frames;
        _identity = identity;
        _stationId = stationId;
        _onEnded = onEnded;
        _sendTimeout = sendTimeout ?? DefaultSendTimeout;
    }

    /// <summary>Completes with the reason once the channel has ended.</summary>
    internal Task<Exception> Ended => _ended.Task;

    /// <summary>How many frames of each type arrived with nothing to route them to.</summary>
    internal IReadOnlyDictionary<string, long> UnroutedFrames => new Dictionary<string, long>(_unrouted);

    /// <summary>
    /// Starts reading the control stream, and writing the frames handed off.
    /// The read loop ends the channel with whatever stops it, so nothing it
    /// throws goes unobserved.
    /// </summary>
    internal void Start()
    {
        _ = Task.Run(() => ReadAsync(_stop.Token));
        _ = Task.Run(WriteHandedOffAsync);
    }

    /// <summary>Ends the channel with reason, stopping the reader and every writer still waiting for a turn.</summary>
    internal void Stop(Exception reason) => End(reason);

    /// <summary>
    /// Signs frame and sends it whole. Waiting for the turn to write is bounded
    /// by the send timeout: when it runs out this throws TimeoutException, the
    /// frame was not sent, and the session carries on. A write that stalls past
    /// the send timeout ends the session with <see cref="SendTimeoutException"/>.
    /// ct can stop the wait for a turn; once the write started, it only stops
    /// this caller waiting for it.
    /// </summary>
    internal async Task SendAsync(Value.MapValue frame, CancellationToken ct)
    {
        var signed = Envelope.Sign(frame, _identity);
        using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            waiting.CancelAfter(_sendTimeout);
            try
            {
                await TakeWriteTurnAsync(waiting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no turn to write a {TypeOf(frame)} frame within {_sendTimeout}, so it was not sent");
            }
        }
        await Observed(WriteTakenTurnAsync(signed)).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands frame to the writer of the frames a session sends on its own
    /// account, without waiting. When 64 frames already wait there, or the
    /// channel ended, the frame is dropped.
    /// </summary>
    internal void HandOff(Value.MapValue frame) => _handOff.Writer.TryWrite(frame);

    /// <summary>
    /// Sends the CALL and waits for the RESULT or ERROR with its call_id, all
    /// within timeout, its turn to write included. Other calls, events and
    /// inbound calls on the same session carry on meanwhile. When timeout runs
    /// out this throws <see cref="CallTimeoutException"/>, saying whether the
    /// CALL's write had started; a write that started finishes on its own,
    /// within the send timeout. Once the CALL is written, afterWritten, when
    /// given, is handed off.
    /// </summary>
    internal async Task<CallResponse> CallAsync(CallSpec spec, TimeSpan timeout, CancellationToken ct, Value.MapValue? afterWritten = null)
    {
        var key = Convert.ToHexStringLower(spec.CallId);
        var reply = new TaskCompletionSource<CallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[key] = reply;
        var writeStarted = false;
        try
        {
            // A channel that ended before this call registered never saw it.
            ThrowIfEnded();
            var signed = Envelope.Sign(CallFrame.Build(spec), _identity);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(timeout);
            try
            {
                await TakeWriteTurnAsync(within.Token).ConfigureAwait(false);
                writeStarted = true;
                var write = Observed(WriteTakenTurnAsync(signed));
                if (afterWritten is not null)
                {
                    _ = write.ContinueWith(_ => HandOff(afterWritten), CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                await write.WaitAsync(within.Token).ConfigureAwait(false);
                return await reply.Task.WaitAsync(within.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CallTimeoutException(
                    writeStarted
                        ? $"no response for call_id {key} within {timeout}"
                        : $"no turn to write call_id {key} within {timeout}, so it was not sent",
                    writeStarted);
            }
        }
        finally
        {
            _calls.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Starts a subscription, sending SUBSCRIBE unless another subscription on
    /// this session already holds that realm and topic at the station.
    /// </summary>
    internal async Task<Subscription> SubscribeAsync(SubscribeSpec spec, CancellationToken ct)
    {
        var subscription = new Subscription(this, spec);
        await _subscriptionTurn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool first;
            lock (_gate)
            {
                ThrowIfEnded();
                first = !_subscriptions.Any(s => s.SameStationSubscription(spec.Realm, spec.Topic));
                _subscriptions.Add(subscription);
            }
            if (!first)
            {
                return subscription;
            }
            try
            {
                await SendAsync(SubscribeFrame.Build(spec), ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                lock (_gate)
                {
                    _subscriptions.Remove(subscription);
                }
                subscription.End(null);
                throw;
            }
            return subscription;
        }
        finally
        {
            _subscriptionTurn.Release();
        }
    }

    /// <summary>
    /// Ends a subscription, and sends UNSUBSCRIBE when no other subscription on
    /// this session still holds its realm and topic.
    /// </summary>
    internal async ValueTask RemoveAsync(Subscription subscription)
    {
        await _subscriptionTurn.WaitAsync().ConfigureAwait(false);
        try
        {
            bool last;
            lock (_gate)
            {
                if (!_subscriptions.Remove(subscription))
                {
                    return;
                }
                last = !_subscriptions.Any(s => s.SameStationSubscription(subscription.Realm, subscription.Topic));
            }
            subscription.End(null);
            if (!last || _ended.Task.IsCompleted)
            {
                return;
            }
            var spec = new UnsubscribeSpec { Topic = subscription.Topic, Realm = subscription.Realm, Subscriber = subscription.Subscriber };
            try
            {
                await SendAsync(UnsubscribeFrame.Build(spec), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Not sent in time, or the session ended; a station drops a
                // connection's subscriptions together with the connection.
            }
        }
        finally
        {
            _subscriptionTurn.Release();
        }
    }

    /// <summary>The next inbound CALL signed by its caller. Once the channel ended, throws its reason.</summary>
    internal async Task<CallInfo> NextInboundCallAsync(CancellationToken ct)
    {
        try
        {
            return await _inboundCalls.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException e) when (e.InnerException is { } reason)
        {
            ExceptionDispatchInfo.Throw(reason);
            throw;
        }
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var frame = await _frames.RecvFrameAsync(ct).ConfigureAwait(false);
                if (Route(frame) is { } reason)
                {
                    End(reason);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            End(new IOException("the session was closed"));
        }
        catch (Exception e)
        {
            End(e as IOException ?? new IOException($"the control stream failed: {e.Message}", e));
        }
    }

    // Routes one frame, returning the reason when the frame ends the channel.
    private Exception? Route(Value frame)
    {
        var type = TypeOf(frame);
        switch (type)
        {
            case "result":
            case "error":
                CompleteCall(type, frame);
                return null;
            case "event":
                DeliverEvent(frame);
                return null;
            case "call":
                QueueInboundCall(frame);
                return null;
            case "goodbye":
                return new IOException($"the station said goodbye: {GoodbyeReason(frame)}");
            case "hello":
            case "connect":
                return new ProtocolViolationException($"the station sent {type} after the handshake");
            default:
                Drop(type);
                return null;
        }
    }

    private void CompleteCall(string type, Value frame)
    {
        if (CallFrameParsing.FrameCallId(frame) is not { } callId || !_calls.TryRemove(Convert.ToHexStringLower(callId), out var reply))
        {
            Drop(type);
            return;
        }
        try
        {
            reply.TrySetResult(CallFrameParsing.ParseCallResponse(frame));
        }
        catch (ParseFrameException e)
        {
            reply.TrySetException(e);
        }
    }

    private void DeliverEvent(Value frame)
    {
        EventInfo evt;
        try
        {
            evt = EventFrameParsing.Parse(frame);
        }
        catch (ParseFrameException)
        {
            Drop("event");
            return;
        }

        var matched = false;
        lock (_gate)
        {
            foreach (var subscription in _subscriptions)
            {
                if (subscription.Overflowed || !subscription.Matches(evt))
                {
                    continue;
                }
                matched = true;
                if (!subscription.TryDeliver(evt))
                {
                    subscription.Overflow(new ConsumerOverflowException(
                        $"the subscription to {subscription.Topic} fell more than {EventQueueCapacity} events behind"));
                }
            }
        }
        if (!matched)
        {
            Drop("event");
        }
    }

    private void QueueInboundCall(Value frame)
    {
        // A CALL that isn't signed by the caller it names gets no reply -- see
        // CallFrameParsing.ParseSignedCall.
        if (CallFrameParsing.ParseSignedCall(frame) is not { } call)
        {
            Drop("call");
            return;
        }
        if (_inboundCalls.Writer.TryWrite(call))
        {
            return;
        }
        // A CALL that doesn't fit gets temporary_relay_failure, as a handler
        // crash does: the handler never ran, so the caller may try again instead
        // of waiting out its deadline. Serving carries on with the calls queued.
        // When the hand-off is full too, the refusal is dropped and the caller's
        // deadline covers it.
        HandOff(CallErrorFrame.Build(new CallErrorSpec { CallId = call.CallId, Code = Bolt4Code.TemporaryRelayFailure, ReportedBy = _identity.NodeId() }));
    }

    // Sends the frames handed off, one at a time, until the channel ends.
    private async Task WriteHandedOffAsync()
    {
        await foreach (var frame in _handOff.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await SendAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Not sent in time, or the session ended. Nothing waits on these
                // frames: a caller's own deadline covers a missing reply, and a
                // fact is best effort.
            }
        }
    }

    // Waits for the turn to write. Once the channel ended, throws its reason.
    private async Task TakeWriteTurnAsync(CancellationToken ct)
    {
        ThrowIfEnded();
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            await _writeTurn.WaitAsync(waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ThrowIfEnded();
            throw;
        }
        if (_ended.Task.IsCompleted)
        {
            _writeTurn.Release();
            ThrowIfEnded();
        }
    }

    // Writes signed while holding the turn, then gives the turn back. A write
    // that stalls past the send timeout may leave a frame half written, so it
    // ends the channel.
    private async Task WriteTakenTurnAsync(Value signed)
    {
        try
        {
            using var bounded = new CancellationTokenSource(_sendTimeout);
            try
            {
                await _frames.SendFrameAsync(signed, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                var stalled = new SendTimeoutException($"a write on the control stream stalled for more than {_sendTimeout}");
                End(stalled);
                throw stalled;
            }
        }
        finally
        {
            _writeTurn.Release();
        }
    }

    // A caller may stop waiting for a write that started; the write still
    // finishes or fails on its own, and its failure is observed here.
    private static Task Observed(Task write)
    {
        _ = write.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return write;
    }

    private void ThrowIfEnded()
    {
        if (_ended.Task.IsCompleted)
        {
            ExceptionDispatchInfo.Throw(_ended.Task.Result);
        }
    }

    private static string TypeOf(Value frame) =>
        frame is Value.MapValue map && map.Get("frame_type") is Value.TextValue t ? t.AsText() : "unknown";

    private static string GoodbyeReason(Value frame) =>
        frame is Value.MapValue map && map.Get("reason") is Value.TextValue reason ? reason.AsText() : "no reason given";

    private void Drop(string frameType)
    {
        _unrouted.AddOrUpdate(frameType, 1, (_, count) => count + 1);
        lock (_logged)
        {
            var (dropped, loggedAt) = _logged.GetValueOrDefault(frameType);
            dropped++;
            var now = Stopwatch.GetTimestamp();
            if (loggedAt is not { } last || Stopwatch.GetElapsedTime(last, now) >= LogInterval)
            {
                Trace.TraceWarning($"macula: dropped {dropped} unrouted {frameType} frame(s) in the last minute (station {Convert.ToHexStringLower(_stationId)})");
                (dropped, loggedAt) = (0, now);
            }
            _logged[frameType] = (dropped, loggedAt);
        }
    }

    private void End(Exception reason)
    {
        if (!_ended.TrySetResult(reason))
        {
            return;
        }
        // Stops the reader and every writer still waiting for a turn; a write
        // in progress finishes within the send timeout.
        _stop.Cancel();
        foreach (var key in _calls.Keys)
        {
            if (_calls.TryRemove(key, out var reply))
            {
                reply.TrySetException(reason);
            }
        }
        lock (_gate)
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.End(reason);
            }
        }
        _inboundCalls.Writer.TryComplete(reason);
        _handOff.Writer.TryComplete();
        _onEnded(reason);
    }
}
