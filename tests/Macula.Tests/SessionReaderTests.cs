using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Macula.Bolt4;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;
using static Macula.Tests.FakeStation;

namespace Macula.Tests;

/// <summary>
/// A session's control stream read by one reader that routes every frame, so
/// calls, subscriptions and serving work at the same time on one session. The
/// station is an in-memory stream (<see cref="FakeStation"/>). The names match
/// the Rust and Go tests.
/// </summary>
public class SessionReaderTests
{
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
    public async Task Closing_the_last_subscription_for_a_topic_unsubscribes()
    {
        var (channel, station, _) = Connect();
        var first = await channel.SubscribeAsync(Subscribe("app/orders"), CancellationToken.None);
        var second = await channel.SubscribeAsync(Subscribe("app/orders"), CancellationToken.None);
        Assert.Equal("subscribe", TypeOf(await station.NextFrameAsync()));

        await first.DisposeAsync();
        // A call right after shows what the session sent in between: nothing.
        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextFrameAsync();
        Assert.Equal("call", TypeOf(sent));
        await station.ReplyAsync(sent, "echoed");
        await call.WaitAsync(Wait);

        await second.DisposeAsync();
        Assert.Equal("unsubscribe", TypeOf(await station.NextFrameAsync()));
    }

    [Fact]
    public async Task An_overflowed_subscription_keeps_the_station_subscribed_until_it_is_closed()
    {
        var (channel, station, _) = Connect();
        var behind = await channel.SubscribeAsync(Subscribe("app/ticks"), CancellationToken.None);
        Assert.Equal("subscribe", TypeOf(await station.NextFrameAsync()));

        // The call's reply comes after every event, so by the time it arrives
        // the reader has routed all of them.
        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextFrameAsync();
        for (var i = 0; i <= ControlChannel.EventQueueCapacity; i++)
        {
            await station.SendEventAsync("app/ticks", $"tick {i}");
        }
        await station.ReplyAsync(sent, "echoed");
        await call.WaitAsync(Wait);
        Assert.True(behind.Overflowed);

        // A call right after shows what the session sent since: nothing.
        var probe = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var next = await station.NextFrameAsync();
        Assert.Equal("call", TypeOf(next));
        await station.ReplyAsync(next, "echoed");
        await probe.WaitAsync(Wait);

        await behind.DisposeAsync();
        Assert.Equal("unsubscribe", TypeOf(await station.NextFrameAsync()));
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
    public async Task An_overflowing_call_queue_answers_the_extra_call_and_keeps_serving()
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

        // Serving carries on: the queued calls, then the next one to arrive.
        for (var i = 0; i < ControlChannel.CallQueueCapacity; i++)
        {
            Assert.Equal($"app/job_{i}", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
        }
        await station.SendInboundCallAsync("app/after_the_overflow");
        Assert.Equal("app/after_the_overflow", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
    }

    [Fact]
    public async Task A_call_that_cannot_get_the_write_lock_in_time_times_out_and_the_session_stays_up()
    {
        var (channel, station, ended) = Connect();
        station.StallSessionWrites();
        var publish = channel.SendAsync(Publish("app/ticks"), CancellationToken.None);

        var timedOut = await Assert.ThrowsAsync<CallTimeoutException>(() => channel.CallAsync(Call("app/echo"), TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.False(timedOut.WriteStarted);
        Assert.False(ended.Task.IsCompleted);

        station.ResumeSessionWrites();
        await publish.WaitAsync(Wait);
        Assert.Equal("publish", TypeOf(await station.NextFrameAsync()));
        var next = Call("app/echo");
        var call = channel.CallAsync(next, Wait, CancellationToken.None);
        var sent = await station.NextFrameAsync();
        Assert.Equal(next.CallId, ((Value.BytesValue)sent.Get("call_id")!).Value);
        await station.ReplyAsync(sent, "echoed");
        Assert.Equal("echoed", ReplyText(await call.WaitAsync(Wait)));
    }

    [Fact]
    public async Task A_write_stalled_past_the_send_timeout_ends_the_session()
    {
        var (channel, station, ended) = Connect(sendTimeout: TimeSpan.FromMilliseconds(200));
        station.StallSessionWrites();

        await Assert.ThrowsAsync<SendTimeoutException>(() => channel.SendAsync(Publish("app/ticks"), CancellationToken.None).WaitAsync(Wait));
        Assert.IsType<SendTimeoutException>(await ended.Task.WaitAsync(Wait));
        var failure = await Assert.ThrowsAsync<SessionEndedException>(() => channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None));
        Assert.False(failure.WriteStarted);
        Assert.IsType<SendTimeoutException>(failure.InnerException);
    }

    [Fact]
    public async Task The_reader_keeps_delivering_while_a_write_is_stalled()
    {
        var (channel, station, _) = Connect();
        await using var ticks = await channel.SubscribeAsync(Subscribe("app/ticks"), CancellationToken.None);
        await station.NextAsync("subscribe");
        station.StallSessionWrites();
        try
        {
            // The extra calls' refusals can't go out while writes are stalled.
            for (var i = 0; i < ControlChannel.CallQueueCapacity + 4; i++)
            {
                await station.SendInboundCallAsync($"app/job_{i}");
            }
            await station.SendEventAsync("app/ticks", "tick 1");

            Assert.Equal("tick 1", (await ticks.RecvEventAsync(Wait)).Payload.AsText());
        }
        finally
        {
            station.ResumeSessionWrites();
        }
    }

    [Fact]
    public async Task A_call_timing_out_while_its_frame_is_being_written_reports_it_may_have_been_sent()
    {
        var (channel, station, ended) = Connect();
        station.StallSessionWrites();
        try
        {
            var timedOut = await Assert.ThrowsAsync<CallTimeoutException>(() => channel.CallAsync(Call("app/echo"), TimeSpan.FromMilliseconds(100), CancellationToken.None));
            Assert.True(timedOut.WriteStarted);
            Assert.False(ended.Task.IsCompleted);
        }
        finally
        {
            station.ResumeSessionWrites();
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    [SupportedOSPlatform("windows")]
    public async Task A_queued_call_past_its_deadline_is_still_served()
    {
        var (channel, station, _) = Connect();

        await station.SendInboundCallAsync("app/echo", deadlineMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000);

        var call = await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait);
        CallLookup lookup = (_, procedure) => procedure != "app/echo" ? null : payload => Task.FromResult(payload);
        var reply = await Session.BuildCallReplyAsync(null, call, lookup, (_, _) => Policy.Open, KeyPair.Generate());
        Assert.IsType<CallResponse.Result>(CallFrameParsing.ParseCallResponse(reply));
    }

    [Fact]
    public async Task A_frame_that_cannot_be_decoded_ends_the_session()
    {
        var (channel, station, ended) = Connect();
        var call = channel.CallAsync(Call("app/echo"), TimeSpan.FromSeconds(10), CancellationToken.None);
        await station.NextAsync("call");

        // A one-byte frame whose CBOR initial byte uses a reserved value.
        await station.SendRawAsync(new byte[] { 0, 0, 0, 1, 0x1C });

        Assert.IsType<MalformedFrameException>(await ended.Task.WaitAsync(Wait));
        var failure = await Assert.ThrowsAsync<SessionEndedException>(() => call.WaitAsync(Wait));
        Assert.True(failure.WriteStarted);
    }

    [Fact]
    public async Task A_call_on_a_session_that_has_ended_reports_it_was_not_sent()
    {
        var (channel, station, ended) = Connect();
        station.Hangup();
        await ended.Task.WaitAsync(Wait);

        var failure = await Assert.ThrowsAsync<SessionEndedException>(() => channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None));
        Assert.False(failure.WriteStarted);
    }

    [Fact]
    public async Task A_call_waiting_for_the_write_lock_when_the_session_ends_reports_it_was_not_sent()
    {
        var (channel, station, _) = Connect();
        station.StallSessionWrites();
        try
        {
            var publish = channel.SendAsync(Publish("app/ticks"), CancellationToken.None);
            var call = channel.CallAsync(Call("app/echo"), TimeSpan.FromSeconds(30), CancellationToken.None);
            await station.SendAsync(GoodbyeFrame.Build("maintenance"));

            // Well before the call's own 30 second deadline.
            var failure = await Assert.ThrowsAsync<SessionEndedException>(() => call.WaitAsync(Wait));
            Assert.False(failure.WriteStarted);
            GC.KeepAlive(publish);
        }
        finally
        {
            station.ResumeSessionWrites();
        }
    }

    [Fact]
    public async Task A_goodbye_from_the_station_fails_pending_calls_and_ends_the_session()
    {
        var (channel, station, ended) = Connect();

        var call = channel.CallAsync(Call("app/echo"), TimeSpan.FromSeconds(10), CancellationToken.None);
        await station.NextAsync("call");
        await station.SendAsync(GoodbyeFrame.Build("maintenance"));

        var failure = await Assert.ThrowsAsync<SessionEndedException>(() => call.WaitAsync(Wait));
        Assert.True(failure.WriteStarted);
        Assert.Contains("maintenance", failure.InnerException!.Message);
        Assert.Contains("maintenance", (await ended.Task.WaitAsync(Wait)).Message);
    }

    [Fact]
    public async Task A_hello_after_the_handshake_ends_the_session()
    {
        var (channel, station, ended) = Connect();

        var call = channel.CallAsync(Call("app/echo"), TimeSpan.FromSeconds(10), CancellationToken.None);
        await station.NextAsync("call");
        await station.SendAsync(Envelope.Base("hello", 0, Envelope.FreshFrameId(), Envelope.CurrentMillis()));

        var failure = await Assert.ThrowsAsync<SessionEndedException>(() => call.WaitAsync(Wait));
        Assert.IsType<ProtocolViolationException>(failure.InnerException);
        Assert.IsType<ProtocolViolationException>(await ended.Task.WaitAsync(Wait));
    }

    [Fact]
    public async Task A_session_end_is_logged_once_with_its_reason()
    {
        var goodbye = $"maintenance {Guid.NewGuid():N}";
        var afterTheGoodbye = $"closed after the goodbye {Guid.NewGuid():N}";
        var closedHere = $"closed here {Guid.NewGuid():N}";
        var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            var (endedByStation, station, ended) = Connect();
            await station.SendAsync(GoodbyeFrame.Build(goodbye));
            await ended.Task.WaitAsync(Wait);
            endedByStation.Stop(new IOException(afterTheGoodbye));

            var (endedHere, _, _) = Connect();
            endedHere.Stop(new IOException(closedHere));

            var byStation = Assert.Single(listener.Events, e => e.Message.Contains(goodbye));
            Assert.Equal(TraceEventType.Warning, byStation.Level);
            Assert.Contains(Convert.ToHexStringLower(FakeStation.NodeId), byStation.Message);
            Assert.DoesNotContain(listener.Events, e => e.Message.Contains(afterTheGoodbye));
            var byUs = Assert.Single(listener.Events, e => e.Message.Contains(closedHere));
            Assert.Equal(TraceEventType.Information, byUs.Level);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
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

    [Fact]
    public async Task An_inbound_call_with_a_wrongly_typed_field_is_dropped_and_the_next_one_served()
    {
        var (channel, station, ended) = Connect();
        var caller = KeyPair.Generate();
        var wronglyTyped = CallFrame.Build(InboundCall("app/wrongly_typed", caller)).WithField("ucan_token", Value.UInt(7));

        await station.SendAsync(Envelope.Sign(wronglyTyped, caller));
        await station.SendInboundCallAsync("app/genuine");

        Assert.Equal("app/genuine", (await channel.NextInboundCallAsync(CancellationToken.None).WaitAsync(Wait)).Procedure);
        Assert.False(ended.Task.IsCompleted, "the session carries on");
        Assert.Equal(1, channel.UnroutedFrames["call"]);
    }

    private static Value.MapValue Publish(string topic) => PublishFrame.Build(new PublishSpec
    {
        Topic = topic,
        Realm = Realm,
        Publisher = KeyPair.Generate().NodeId(),
        Seq = 1,
        Payload = Value.Text("tick"),
        PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
}
