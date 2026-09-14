using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Macula.Frame;

namespace Macula.Connection;

/// <summary>
/// One subscription on a <see cref="Session"/>: every EVENT whose realm equals
/// this subscription's realm and whose topic matches its topic (see
/// <see cref="Session.SubscribeAsync"/> for the matching rule) is copied into
/// this subscription's own queue of 256 events. Several subscriptions on one
/// session each get their own copy.
///
/// A subscription that falls more than 256 events behind ends: its reader
/// first gets every event still queued, in order, then a
/// <see cref="ConsumerOverflowException"/>. The session and its other
/// subscriptions carry on, and the station stays subscribed until the
/// overflowed subscription is disposed, so a replacement subscribed first
/// takes over without a gap. Dispose it to stop receiving; the session sends
/// UNSUBSCRIBE once no other subscription on it wants that realm and topic.
/// </summary>
public sealed class Subscription : IAsyncDisposable
{
    private readonly ControlChannel _channel;
    private readonly Channel<EventInfo> _events = Channel.CreateBounded<EventInfo>(
        new BoundedChannelOptions(ControlChannel.EventQueueCapacity) { SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private int _disposed;

    internal Subscription(ControlChannel channel, SubscribeSpec spec)
    {
        _channel = channel;
        Realm = spec.Realm;
        Topic = spec.Topic;
        Subscriber = spec.Subscriber;
    }

    public byte[] Realm { get; }

    public string Topic { get; }

    internal byte[] Subscriber { get; }

    // Set by the control channel's reader, under its lock, once this
    // subscription overflowed: it no longer receives events, but still counts
    // as holding the station-side subscription until it is disposed.
    internal bool Overflowed { get; private set; }

    internal bool SameStationSubscription(byte[] realm, string topic) =>
        Topic == topic && Realm.AsSpan().SequenceEqual(realm);

    internal bool Matches(EventInfo evt) =>
        evt.Realm.AsSpan().SequenceEqual(Realm) && TopicPattern.Matches(Topic, evt.Topic);

    internal bool TryDeliver(EventInfo evt) => _events.Writer.TryWrite(evt);

    internal void Overflow(ConsumerOverflowException overflow)
    {
        Overflowed = true;
        _events.Writer.TryComplete(overflow);
    }

    internal void End(Exception? reason) => _events.Writer.TryComplete(reason);

    /// <summary>
    /// Waits up to timeout for the next event. Throws TimeoutException when
    /// none arrives in time, <see cref="ConsumerOverflowException"/> once this
    /// subscription fell behind and its queued events are read,
    /// <see cref="SessionEndedException"/> once the session ended, and
    /// ObjectDisposedException after the subscription is disposed.
    /// </summary>
    public async Task<EventInfo> RecvEventAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(timeout);
        try
        {
            return await _events.Reader.ReadAsync(within.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no event on {Topic} within {timeout}");
        }
        catch (ChannelClosedException e) when (e.InnerException is { } reason)
        {
            ExceptionDispatchInfo.Throw(reason);
            throw;
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(Subscription));
        }
    }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0 ? _channel.RemoveAsync(this) : ValueTask.CompletedTask;
}
