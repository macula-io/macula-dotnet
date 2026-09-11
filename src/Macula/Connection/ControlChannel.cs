using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Macula.Bolt4;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Connection;

/// <summary>
/// A consumer fell further behind than its queue holds: a
/// <see cref="Subscription"/> after 256 events, or a call server after 64
/// inbound calls. It ends that consumer only; the session stays up.
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
/// Reads a session's control stream and routes every frame to whatever waits
/// for it: a RESULT or ERROR to its call, an EVENT to each subscription whose
/// realm and topic match, and a CALL signed by its caller to the inbound call
/// queue. GOODBYE, or HELLO or CONNECT after the handshake, ends the channel.
/// Any other frame is dropped and counted by type, with at most one trace line
/// per type per minute.
///
/// When the channel ends, every waiting call fails with the reason, every
/// subscription and the call queue end with it, and onEnded runs once.
/// </summary>
internal sealed class ControlChannel
{
    internal const int EventQueueCapacity = 256;
    internal const int CallQueueCapacity = 64;

    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SideWriteTimeout = TimeSpan.FromSeconds(5);

    private readonly FrameStream _frames;
    private readonly KeyPair _identity;
    private readonly byte[] _stationId;
    private readonly Action<Exception> _onEnded;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<CallResponse>> _calls = new();
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = new();
    private Channel<CallInfo> _inboundCalls = NewCallQueue();
    private readonly ConcurrentDictionary<string, long> _unrouted = new();
    private readonly Dictionary<string, (long Dropped, long? LoggedAt)> _logged = new();
    private readonly TaskCompletionSource<Exception> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();

    internal ControlChannel(FrameStream frames, KeyPair identity, byte[] stationId, Action<Exception> onEnded)
    {
        _frames = frames;
        _identity = identity;
        _stationId = stationId;
        _onEnded = onEnded;
    }

    /// <summary>Completes with the reason once the channel has ended.</summary>
    internal Task<Exception> Ended => _ended.Task;

    /// <summary>How many frames of each type arrived with nothing to route them to.</summary>
    internal IReadOnlyDictionary<string, long> UnroutedFrames => new Dictionary<string, long>(_unrouted);

    /// <summary>
    /// Starts reading the control stream. The read loop ends the channel with
    /// whatever stops it, so nothing it throws goes unobserved.
    /// </summary>
    internal void Start() => _ = Task.Run(() => ReadAsync(_stop.Token));

    /// <summary>Ends the channel with reason and stops reading.</summary>
    internal void Stop(Exception reason)
    {
        End(reason);
        _stop.Cancel();
    }

    /// <summary>Signs frame with this session's identity and sends it.</summary>
    internal Task SendAsync(Value.MapValue frame, CancellationToken ct) =>
        _frames.SendFrameAsync(Envelope.Sign(frame, _identity), ct);

    /// <summary>
    /// Sends the CALL and waits for the RESULT or ERROR with its call_id. Other
    /// calls, events and inbound calls on the same session carry on meanwhile.
    /// </summary>
    internal async Task<CallResponse> CallAsync(CallSpec spec, TimeSpan timeout, CancellationToken ct)
    {
        var key = Convert.ToHexStringLower(spec.CallId);
        var reply = new TaskCompletionSource<CallResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[key] = reply;
        try
        {
            // A channel that ended before this call registered never saw it.
            if (_ended.Task.IsCompleted)
            {
                ExceptionDispatchInfo.Throw(_ended.Task.Result);
            }
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(timeout);
            try
            {
                await SendAsync(CallFrame.Build(spec), within.Token).ConfigureAwait(false);
                return await reply.Task.WaitAsync(within.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no response for call_id {key} within {timeout}");
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
        bool first;
        lock (_gate)
        {
            if (_ended.Task.IsCompleted)
            {
                ExceptionDispatchInfo.Throw(_ended.Task.Result);
            }
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

    /// <summary>
    /// Ends a subscription, and sends UNSUBSCRIBE when no other subscription on
    /// this session still holds its realm and topic.
    /// </summary>
    internal async ValueTask RemoveAsync(Subscription subscription)
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
        using var bounded = new CancellationTokenSource(SideWriteTimeout);
        try
        {
            await SendAsync(UnsubscribeFrame.Build(spec), bounded.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session may already be unusable; a station drops a
            // connection's subscriptions together with the connection.
        }
    }

    /// <summary>
    /// The next inbound CALL signed by its caller. After the call queue
    /// overflowed, the calls already queued come first, then one
    /// <see cref="ConsumerOverflowException"/>, and later reads start a fresh
    /// queue.
    /// </summary>
    internal async Task<CallInfo> NextInboundCallAsync(CancellationToken ct)
    {
        Channel<CallInfo> calls;
        lock (_gate)
        {
            calls = _inboundCalls;
        }
        try
        {
            return await calls.Reader.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (ChannelClosedException e) when (e.InnerException is ConsumerOverflowException overflow)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_inboundCalls, calls) && !_ended.Task.IsCompleted)
                {
                    _inboundCalls = NewCallQueue();
                }
            }
            ExceptionDispatchInfo.Throw(overflow);
            throw;
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
        var type = frame is Value.MapValue map && map.Get("frame_type") is Value.TextValue t ? t.AsText() : "unknown";
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
        Channel<CallInfo> calls;
        lock (_gate)
        {
            calls = _inboundCalls;
        }
        if (calls.Writer.TryWrite(call))
        {
            return;
        }
        calls.Writer.TryComplete(new ConsumerOverflowException($"more than {CallQueueCapacity} inbound calls were waiting to be served"));
        _ = RefuseAsync(call);
    }

    // An inbound call that doesn't fit the queue gets temporary_relay_failure
    // at once, as a handler crash does: the handler never ran, so the caller
    // may try again instead of waiting out its deadline.
    private async Task RefuseAsync(CallInfo call)
    {
        var refusal = CallErrorFrame.Build(new CallErrorSpec { CallId = call.CallId, Code = Bolt4Code.TemporaryRelayFailure, ReportedBy = _identity.NodeId() });
        using var bounded = new CancellationTokenSource(SideWriteTimeout);
        try
        {
            await SendAsync(refusal, bounded.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session is ending or its stream is stuck; the caller's own
            // deadline covers the missing reply.
        }
    }

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
            _inboundCalls.Writer.TryComplete(reason);
        }
        _onEnded(reason);
    }

    private static Channel<CallInfo> NewCallQueue() =>
        Channel.CreateBounded<CallInfo>(new BoundedChannelOptions(CallQueueCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
}
