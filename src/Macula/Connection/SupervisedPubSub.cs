using System.Runtime.Versioning;
using System.Threading;
using Macula.Cbor;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Connection;

/// <summary>Processes one inbound EVENT. Returning normally continues the loop; throwing stops <see cref="SupervisedPubSub.RunSubscriberAsync"/> and the exception propagates to its caller -- the C# equivalent of macula_subscriber.erl's handle_event/4 returning {stop, Reason, State}.</summary>
public delegate Task EventHandler(EventInfo eventInfo);

/// <summary>Delivered to a <see cref="SupervisedPubSub.RunPublisherAsync"/> callback once its publish attempt resolves -- either it completed (Error null), it failed (Error set), or it was cancelled before the underlying publish ran.</summary>
public sealed record PublishOutcome(Exception? Error, bool Cancelled);

/// <summary>
/// The supervised counterparts to the bare Publish/Subscribe/RecvEvent
/// primitives on <see cref="Session"/>, matching macula_publisher.erl/
/// macula_subscriber.erl in spirit -- not a literal port of their
/// gen_server-behind-a-pid shape, since C# has no OTP process either;
/// CancellationToken plays the role a pid's supervision plays in Erlang,
/// the same idiom this SDK's own <see cref="Dht.DirectDial.KeepAdvertisedDirectAsync"/>
/// already established for a comparable long-lived background loop.
/// Ported from macula-go's connection/publisher.go + subscriber.go.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public static class SupervisedPubSub
{
    private const string PublishStartedTopic = "pubsub.publish_started_v1";
    private const string PublishCompletedTopic = "pubsub.publish_completed_v1";

    // Bounds how long a single control-stream read blocks between checking
    // ct -- not a wire timeout, just how promptly a cancelled token is
    // noticed when nothing is arriving.
    private static readonly TimeSpan SubscriberPollInterval = TimeSpan.FromSeconds(2);

    private static long _factSeq;

    /// <summary>
    /// Runs spec's publish in a background Task, delivers the outcome to
    /// onDone instead of blocking the caller, and publishes
    /// pubsub.publish_started_v1/pubsub.publish_completed_v1 mesh facts
    /// around it using spec's own realm -- exactly what
    /// macula_publisher.erl's internal publish/5 helper does.
    ///
    /// Session's PublishAsync is already a synchronous, near-instant frame
    /// send (no network round-trip to await -- PUBLISH has no ack on this
    /// wire), so there is no meaningful window in which cancelling can
    /// abort an in-flight wire write. The returned cancellation only
    /// prevents the publish from running at all if triggered before the
    /// background Task reaches it, right after sending the started fact;
    /// once the publish has started, cancellation is a no-op and the real
    /// outcome (success or failure) is what gets delivered.
    ///
    /// onDone is invoked from a different Task than the caller's -- do not
    /// assume it runs synchronously with this method's return.
    /// </summary>
    public static CancellationTokenSource RunPublisherAsync(Session session, PublishSpec spec, KeyPair identity, bool announce, Action<PublishOutcome> onDone) =>
        RunPublisherOn(session, spec, identity, announce, onDone);

    /// <summary>RunPublisherAsync over any frame sink, so its sends can be observed without a station.</summary>
    internal static CancellationTokenSource RunPublisherOn(IFrameSink session, PublishSpec spec, KeyPair identity, bool announce, Action<PublishOutcome> onDone)
    {
        var cts = new CancellationTokenSource();
        var publishId = RandomId();

        _ = Task.Run(async () =>
        {
            // The started fact's send finishes before the publish starts, so
            // the two never overlap on the session and arrive in order.
            await AnnounceFact(session, announce, spec.Realm, identity, PublishStartedTopic,
                Value.Map(new List<KeyValuePair<Value, Value>>
                {
                    new(Value.Text("publish_id"), Value.Bytes(publishId)),
                    new(Value.Text("topic"), Value.Bytes(System.Text.Encoding.UTF8.GetBytes(spec.Topic))),
                })).ConfigureAwait(false);
            if (cts.IsCancellationRequested)
            {
                await AnnounceCompletedAsync(session, announce, spec.Realm, identity, publishId, new PublishOutcome(null, true)).ConfigureAwait(false);
                onDone(new PublishOutcome(null, true));
                return;
            }
            Exception? err = null;
            try
            {
                await session.PublishAsync(spec).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                err = e;
            }
            var outcome = new PublishOutcome(err, false);
            await AnnounceCompletedAsync(session, announce, spec.Realm, identity, publishId, outcome).ConfigureAwait(false);
            onDone(outcome);
        });

        return cts;
    }

    /// <summary>
    /// The supervised counterpart to the bare Subscribe/RecvEvent
    /// primitives: subscribes once, then dispatches every inbound EVENT to
    /// handler for as long as this runs, instead of requiring the caller
    /// to hand-roll a poll loop. Unsubscribes on return, including on
    /// cancellation.
    ///
    /// Blocks the calling Task until ct is cancelled, handler throws
    /// (propagated here unchanged), or the control stream fails with
    /// something other than a timeout (also propagated, wrapped).
    ///
    /// Mirrors ServeOneCallAsync's own frame loop, not RecvEventAsync: a
    /// shared control stream can carry other frame types between one EVENT
    /// and the next, so a wrong-frame-type parse failure is skipped and
    /// polling continues, exactly like ServeOneCallAsync skips a
    /// non-"call" frame -- it is NOT treated as fatal the way
    /// RecvEventAsync's own contract treats any parse failure. Without
    /// this, a single non-EVENT frame arriving on the control stream would
    /// abort the whole subscriber loop (the exact bug macula-go's own
    /// first draft of RunSubscriber hit and had to fix -- avoided here
    /// from the start).
    /// </summary>
    public static async Task RunSubscriberAsync(Session session, SubscribeSpec spec, KeyPair identity, EventHandler handler, CancellationToken ct = default)
    {
        await session.SubscribeAsync(spec, ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                EventInfo evt;
                try
                {
                    evt = await session.RecvEventAsync(SubscriberPollInterval, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (ParseFrameException)
                {
                    continue; // a non-EVENT or malformed frame -- ignore and keep listening
                }

                await handler(evt).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await session.UnsubscribeAsync(new UnsubscribeSpec { Topic = spec.Topic, Realm = spec.Realm, Subscriber = spec.Subscriber }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best-effort -- the connection may already be unusable
            }
        }
    }

    private static byte[] RandomId()
    {
        var b = new byte[16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return b;
    }

    // A no-op if session is null -- real callers always pass a live
    // Session; only network-free unit tests exercising pure dispatch logic
    // would pass null, matching the same nil-session-safe design
    // macula-go's own announceFact uses.
    private static async Task AnnounceFact(IFrameSink? session, bool announce, byte[] realm, KeyPair identity, string topic, Value payload)
    {
        if (!announce || session is null)
        {
            return;
        }
        var spec = new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = identity.NodeId(),
            Seq = (ulong)Interlocked.Increment(ref _factSeq),
            Payload = payload,
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        try
        {
            await session.PublishAsync(spec).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // fire-and-forget telemetry -- a failure to announce must never
            // fail the operation it's describing
        }
    }

    private static Task AnnounceCompletedAsync(IFrameSink? session, bool announce, byte[] realm, KeyPair identity, byte[] publishId, PublishOutcome outcome)
    {
        var fields = new List<KeyValuePair<Value, Value>> { new(Value.Text("publish_id"), Value.Bytes(publishId)) };
        if (outcome.Cancelled)
        {
            fields.Add(new KeyValuePair<Value, Value>(Value.Text("outcome"), Value.Text("cancelled")));
        }
        else if (outcome.Error is { } err)
        {
            fields.Add(new KeyValuePair<Value, Value>(Value.Text("outcome"), Value.Text("failed")));
            fields.Add(new KeyValuePair<Value, Value>(Value.Text("reason"), Value.Text(err.Message)));
        }
        else
        {
            fields.Add(new KeyValuePair<Value, Value>(Value.Text("outcome"), Value.Text("completed")));
        }
        return AnnounceFact(session, announce, realm, identity, PublishCompletedTopic, Value.Map(fields));
    }
}
