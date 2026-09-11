using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// The sends <see cref="SupervisedPubSub.RunPublisherAsync"/> makes, observed
/// on a sink that takes one send at a time, the contract a plain
/// <see cref="Session"/> documents: a send that starts while another is still
/// in flight fails.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class SupervisedPublisherTests
{
    private const string Topic = "macula_dotnet.publisher_test.thing_happened";

    [Fact]
    public async Task The_started_fact_is_sent_before_the_publish_and_never_alongside_it()
    {
        var sink = new OneSendAtATimeSink();

        var outcome = await PublishAsync(sink);

        Assert.Null(outcome.Error);
        Assert.Equal(new[] { "pubsub.publish_started_v1", Topic, "pubsub.publish_completed_v1" }, sink.Sent);
    }

    [Fact]
    public async Task A_started_fact_that_fails_to_send_does_not_stop_the_publish()
    {
        var sink = new OneSendAtATimeSink
        {
            FailingTopic = "pubsub.publish_started_v1",
        };

        var outcome = await PublishAsync(sink);

        Assert.Null(outcome.Error);
        Assert.Equal(new[] { Topic, "pubsub.publish_completed_v1" }, sink.Sent);
    }

    private static async Task<PublishOutcome> PublishAsync(IFrameSink sink)
    {
        var identity = KeyPair.Generate();
        var spec = new PublishSpec
        {
            Topic = Topic,
            Realm = new byte[32],
            Publisher = identity.NodeId(),
            Seq = 1,
            Payload = Value.Text("hello"),
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var done = new TaskCompletionSource<PublishOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        SupervisedPubSub.RunPublisherOn(sink, spec, identity, announce: true, outcome => done.SetResult(outcome));

        return await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class OneSendAtATimeSink : IFrameSink
    {
        private readonly List<string> _sent = new();
        private int _sending;

        public string? FailingTopic { get; init; }

        public IReadOnlyList<string> Sent
        {
            get
            {
                lock (_sent)
                {
                    return _sent.ToList();
                }
            }
        }

        public async Task PublishAsync(PublishSpec spec, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _sending, 1) == 1)
            {
                throw new InvalidOperationException("a send is already in progress on this session");
            }
            try
            {
                // Long enough that a second send started meanwhile overlaps this one.
                await Task.Delay(100, ct).ConfigureAwait(false);
                if (spec.Topic == FailingTopic)
                {
                    throw new IOException($"{spec.Topic} could not be sent");
                }
                lock (_sent)
                {
                    _sent.Add(spec.Topic);
                }
            }
            finally
            {
                Volatile.Write(ref _sending, 0);
            }
        }
    }
}
