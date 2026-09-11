using System.Security.Cryptography;
using System.Text;
using Macula.Bolt4;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// A session's control stream read by one reader that routes every frame, so
/// calls, subscriptions and serving work at the same time on one session. The
/// station is an in-memory stream. The names match the Rust and Go tests.
/// </summary>
public class SessionReaderTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);
    private static readonly byte[] Realm = Enumerable.Repeat((byte)7, 32).ToArray();

    [Fact]
    public async Task Concurrent_calls_on_one_session_each_get_their_own_reply()
    {
        var (channel, station, _) = Connect();
        var procedures = Enumerable.Range(0, 10).Select(i => $"app/echo_{i}").ToList();

        var calls = procedures.Select(p => channel.CallAsync(Call(p), Wait, CancellationToken.None)).ToList();
        var sent = new List<Value.MapValue>();
        foreach (var _ in procedures)
        {
            sent.Add(await station.NextAsync("call"));
        }
        foreach (var call in Enumerable.Reverse(sent))
        {
            await station.ReplyAsync(call, Encoding.UTF8.GetString(((Value.BytesValue)call.Get("procedure")!).Value));
        }

        var replies = await Task.WhenAll(calls).WaitAsync(Wait);
        Assert.Equal(procedures, replies.Select(ReplyText));
    }

    [Fact]
    public async Task An_event_arriving_during_a_call_reaches_its_subscriber()
    {
        var (channel, station, _) = Connect();
        await using var subscription = await channel.SubscribeAsync(Subscribe("app/orders/placed"), CancellationToken.None);

        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextAsync("call");
        await station.SendEventAsync("app/orders/placed", "order 1");
        await station.ReplyAsync(sent, "echoed");

        Assert.Equal("echoed", ReplyText(await call.WaitAsync(Wait)));
        Assert.Equal("order 1", (await subscription.RecvEventAsync(Wait)).Payload.AsText());
    }

    [Fact]
    public async Task Serving_a_call_while_calling_on_the_same_session()
    {
        var (channel, station, _) = Connect();

        var served = channel.NextInboundCallAsync(CancellationToken.None);
        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextAsync("call");
        await station.SendInboundCallAsync("app/greet");
        await station.ReplyAsync(sent, "echoed");

        Assert.Equal("app/greet", (await served.WaitAsync(Wait)).Procedure);
        Assert.Equal("echoed", ReplyText(await call.WaitAsync(Wait)));
    }

    [Fact]
    public async Task Two_subscribers_with_different_topics_each_get_only_their_events()
    {
        var (channel, station, _) = Connect();
        await using var orders = await channel.SubscribeAsync(Subscribe("app/orders"), CancellationToken.None);
        await using var invoices = await channel.SubscribeAsync(Subscribe("app/invoices"), CancellationToken.None);

        await station.SendEventAsync("app/orders", "order 1");
        await station.SendEventAsync("app/invoices", "invoice 1");

        Assert.Equal("order 1", (await orders.RecvEventAsync(Wait)).Payload.AsText());
        Assert.Equal("invoice 1", (await invoices.RecvEventAsync(Wait)).Payload.AsText());
        await Assert.ThrowsAsync<TimeoutException>(() => orders.RecvEventAsync(TimeSpan.FromMilliseconds(200)));
        await Assert.ThrowsAsync<TimeoutException>(() => invoices.RecvEventAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_wildcard_subscription_matches_exactly_one_segment()
    {
        var (channel, station, _) = Connect();
        await using var placed = await channel.SubscribeAsync(Subscribe("app/*/placed"), CancellationToken.None);

        await station.SendEventAsync("app/orders/eu/placed", "two segments");
        await station.SendEventAsync("app/placed", "no segment");
        await station.SendEventAsync("app/orders/placed", "one segment");

        Assert.Equal("one segment", (await placed.RecvEventAsync(Wait)).Payload.AsText());
        await Assert.ThrowsAsync<TimeoutException>(() => placed.RecvEventAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_stalled_event_consumer_does_not_stall_call_replies()
    {
        var (channel, station, _) = Connect();
        await using var stalled = await channel.SubscribeAsync(Subscribe("app/ticks"), CancellationToken.None);

        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextAsync("call");
        for (var i = 0; i < ControlChannel.EventQueueCapacity + 44; i++)
        {
            await station.SendEventAsync("app/ticks", $"tick {i}");
        }
        await station.ReplyAsync(sent, "echoed");

        Assert.Equal("echoed", ReplyText(await call.WaitAsync(Wait)));
    }

    [Fact]
    public async Task An_overflowing_event_consumer_ends_with_an_overflow_error_and_the_session_stays_up()
    {
        var (channel, station, _) = Connect();
        await using var behind = await channel.SubscribeAsync(Subscribe("app/ticks"), CancellationToken.None);

        // The call's reply comes after every event, so by the time it arrives
        // the reader has routed all of them.
        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextAsync("call");
        for (var i = 0; i <= ControlChannel.EventQueueCapacity; i++)
        {
            await station.SendEventAsync("app/ticks", $"tick {i}");
        }
        await station.ReplyAsync(sent, "echoed");
        await call.WaitAsync(Wait);

        for (var i = 0; i < ControlChannel.EventQueueCapacity; i++)
        {
            Assert.Equal($"tick {i}", (await behind.RecvEventAsync(Wait)).Payload.AsText());
        }
        await Assert.ThrowsAsync<ConsumerOverflowException>(() => behind.RecvEventAsync(Wait));

        await using var fresh = await channel.SubscribeAsync(Subscribe("app/ticks"), CancellationToken.None);
        await station.SendEventAsync("app/ticks", "after the overflow");
        Assert.Equal("after the overflow", (await fresh.RecvEventAsync(Wait)).Payload.AsText());
    }

    [Fact]
    public async Task An_overflowing_call_queue_answers_the_extra_call_with_an_error()
    {
        var (channel, station, _) = Connect();

        var inbound = new List<byte[]>();
        for (var i = 0; i <= ControlChannel.CallQueueCapacity; i++)
        {
            inbound.Add(await station.SendInboundCallAsync($"app/job_{i}"));
        }

        var refusal = await station.NextAsync("error");
        Assert.Equal(inbound[^1], ((Value.BytesValue)refusal.Get("call_id")!).Value);
        Assert.Equal(Bolt4Code.TemporaryRelayFailure.Name, Assert.IsType<CallResponse.Error>(CallFrameParsing.ParseCallResponse(refusal)).Name);

        for (var i = 0; i < ControlChannel.CallQueueCapacity; i++)
        {
            Assert.Equal($"app/job_{i}", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
        }
        await Assert.ThrowsAsync<ConsumerOverflowException>(() => channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait));

        await station.SendInboundCallAsync("app/after_the_overflow");
        Assert.Equal("app/after_the_overflow", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
    }

    [Fact]
    public async Task A_goodbye_from_the_station_fails_pending_calls_and_ends_the_session()
    {
        var (channel, station, ended) = Connect();

        var call = channel.CallAsync(Call("app/echo"), TimeSpan.FromSeconds(10), CancellationToken.None);
        await station.NextAsync("call");
        await station.SendAsync(GoodbyeFrame.Build("maintenance"));

        var failure = await Assert.ThrowsAsync<IOException>(() => call.WaitAsync(Wait));
        Assert.Contains("maintenance", failure.Message);
        Assert.Contains("maintenance", (await ended.Task.WaitAsync(Wait)).Message);
    }

    [Fact]
    public async Task A_hello_after_the_handshake_ends_the_session()
    {
        var (channel, station, ended) = Connect();

        var call = channel.CallAsync(Call("app/echo"), TimeSpan.FromSeconds(10), CancellationToken.None);
        await station.NextAsync("call");
        await station.SendAsync(Envelope.Base("hello", 0, Envelope.FreshFrameId(), Envelope.CurrentMillis()));

        await Assert.ThrowsAsync<ProtocolViolationException>(() => call.WaitAsync(Wait));
        Assert.IsType<ProtocolViolationException>(await ended.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task An_unrouted_frame_is_counted_by_type()
    {
        var (channel, station, _) = Connect();

        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextAsync("call");
        await station.SendAsync(Envelope.Base("advertise", 0, Envelope.FreshFrameId(), Envelope.CurrentMillis()));
        await station.SendAsync(Envelope.Base("advertise", 0, Envelope.FreshFrameId(), Envelope.CurrentMillis()));
        await station.SendAsync(ResultFrame.Build(new ResultSpec { CallId = RandomBytes(16), Payload = Value.Null, RespondedBy = FakeStation.NodeId }));
        await station.ReplyAsync(sent, "echoed");
        await call.WaitAsync(Wait);

        Assert.Equal(2, channel.UnroutedFrames["advertise"]);
        Assert.Equal(1, channel.UnroutedFrames["result"]);
    }

    [Fact]
    public async Task A_session_whose_connection_ends_is_no_longer_found_for_reuse()
    {
        var identity = KeyPair.Generate();
        var open = new OpenSessions<string>();
        open.Register(identity.NodeId(), FakeStation.NodeId, "the session");
        var (_, station, ended) = Connect(identity, _ => open.Unregister(identity.NodeId(), FakeStation.NodeId, "the session"));

        station.Hangup();

        Assert.IsAssignableFrom<IOException>(await ended.Task.WaitAsync(Wait));
        Assert.Null(open.Find(identity.NodeId(), FakeStation.NodeId));
    }

    // Guards moved here from ServeCallerTests with the signature check itself:
    // a CALL that isn't signed by the caller it names never reaches serving
    // and gets no reply, as in macula_station_link.erl's on_inbound_call/3.

    [Fact]
    public async Task An_inbound_call_not_signed_by_its_caller_is_dropped()
    {
        var (channel, station, _) = Connect();

        await station.SendInboundCallAsync("app/forged", signer: KeyPair.Generate());
        await station.SendInboundCallAsync("app/genuine");

        Assert.Equal("app/genuine", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
        Assert.Equal(1, channel.UnroutedFrames["call"]);
    }

    [Fact]
    public async Task An_unsigned_inbound_call_is_dropped()
    {
        var (channel, station, _) = Connect();

        await station.SendAsync(CallFrame.Build(InboundCall("app/unsigned", KeyPair.Generate())));
        await station.SendInboundCallAsync("app/genuine");

        Assert.Equal("app/genuine", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
        Assert.Equal(1, channel.UnroutedFrames["call"]);
    }

    private static (ControlChannel Channel, FakeStation Station, TaskCompletionSource<Exception> Ended) Connect(KeyPair? identity = null, Action<Exception>? onEnded = null)
    {
        var (client, station) = InMemoryPipe.CreatePair();
        var ended = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new ControlChannel(new FrameStream(client), identity ?? KeyPair.Generate(), FakeStation.NodeId, reason =>
        {
            onEnded?.Invoke(reason);
            ended.TrySetResult(reason);
        });
        channel.Start();
        return (channel, new FakeStation(station), ended);
    }

    private static CallSpec Call(string procedure) => new()
    {
        CallId = RandomBytes(16),
        Procedure = procedure,
        Realm = Realm,
        Payload = Value.Null,
        DeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000,
        Caller = KeyPair.Generate().NodeId(),
    };

    private static CallSpec InboundCall(string procedure, KeyPair caller) => new()
    {
        CallId = RandomBytes(16),
        Procedure = procedure,
        Realm = Realm,
        Payload = Value.Null,
        DeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 5_000,
        Caller = caller.NodeId(),
    };

    private static SubscribeSpec Subscribe(string topic) => new() { Topic = topic, Realm = Realm, Subscriber = KeyPair.Generate().NodeId() };

    private static string ReplyText(CallResponse response) => Assert.IsType<CallResponse.Result>(response).Payload.AsText();

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    /// <summary>The station end of the control stream: reads what the session sends and sends it frames.</summary>
    private sealed class FakeStation
    {
        internal static readonly byte[] NodeId = KeyPair.Generate().NodeId();

        private readonly InMemoryPipe _pipe;
        private readonly FrameStream _frames;

        internal FakeStation(InMemoryPipe pipe)
        {
            _pipe = pipe;
            _frames = new FrameStream(pipe);
        }

        internal Task SendAsync(Value.MapValue frame) => _frames.SendFrameAsync(frame);

        internal async Task<Value.MapValue> NextAsync(string frameType)
        {
            while (true)
            {
                var frame = (Value.MapValue)await _frames.RecvFrameAsync().WaitAsync(Wait);
                if (frame.Get("frame_type") is Value.TextValue t && t.AsText() == frameType)
                {
                    return frame;
                }
            }
        }

        internal Task ReplyAsync(Value.MapValue call, string text) =>
            SendAsync(ResultFrame.Build(new ResultSpec { CallId = ((Value.BytesValue)call.Get("call_id")!).Value, Payload = Value.Text(text), RespondedBy = NodeId }));

        internal Task SendEventAsync(string topic, string payload) =>
            SendAsync(Envelope.Base("event", 0, Envelope.FreshFrameId(), Envelope.CurrentMillis())
                .WithField("realm", Value.Bytes(Realm))
                .WithField("topic", Value.Bytes(Encoding.UTF8.GetBytes(topic)))
                .WithField("publisher", Value.Bytes(NodeId))
                .WithField("seq", Value.UInt(1))
                .WithField("payload", Value.Text(payload))
                .WithField("delivered_via", Value.Text("direct")));

        // An inbound CALL signed by its caller, as a station relays one. Returns its call_id.
        // Signed by its caller unless another signer is given.
        internal async Task<byte[]> SendInboundCallAsync(string procedure, KeyPair? signer = null)
        {
            var caller = KeyPair.Generate();
            var call = InboundCall(procedure, caller);
            await SendAsync(Envelope.Sign(CallFrame.Build(call), signer ?? caller));
            return call.CallId;
        }

        internal void Hangup() => _pipe.Hangup();
    }
}
