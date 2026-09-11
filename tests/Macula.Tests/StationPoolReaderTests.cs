using System.Runtime.Versioning;
using System.Threading.Channels;
using Macula.Connection;
using Macula.Frame;
using static Macula.Tests.FakeStation;

namespace Macula.Tests;

/// <summary>
/// A <see cref="StationPool"/>'s calls and subscription consumers on session
/// channels, against in-memory stations (<see cref="FakeStation"/>). The names
/// match the Go tests.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class StationPoolReaderTests
{
    [Fact]
    public async Task Call_falls_through_to_next_connected_link()
    {
        var (gone, _, _) = Connect();
        gone.Stop(new IOException("the station went away"));
        var (live, station, _) = Connect();

        var call = StationPool.CallUntilSentAsync(new Func<Task<CallResponse>>[]
        {
            () => gone.CallAsync(Call("app/echo"), Wait, CancellationToken.None),
            () => live.CallAsync(Call("app/echo"), Wait, CancellationToken.None),
        });
        await station.ReplyAsync(await station.NextAsync("call"), "echoed");

        Assert.Equal("echoed", ReplyText(await call.WaitAsync(Wait)));
    }

    [Fact]
    public async Task A_pool_call_that_timed_out_after_its_write_started_is_not_tried_on_another_link()
    {
        var (stalled, stalledStation, _) = Connect();
        var (other, otherStation, _) = Connect();
        stalledStation.StallSessionWrites();
        try
        {
            var timedOut = await Assert.ThrowsAsync<CallTimeoutException>(() => StationPool.CallUntilSentAsync(new Func<Task<CallResponse>>[]
            {
                () => stalled.CallAsync(Call("app/echo"), TimeSpan.FromMilliseconds(100), CancellationToken.None),
                () => other.CallAsync(Call("app/echo"), Wait, CancellationToken.None),
            }));

            Assert.True(timedOut.WriteStarted);
            Assert.True(await otherStation.NothingSentWithinAsync(TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            stalledStation.ResumeSessionWrites();
        }
    }

    [Fact]
    public async Task An_overflowed_subscription_is_replaced_before_it_is_closed()
    {
        var (channel, station, _) = Connect();
        var spec = Subscribe("app/ticks");
        var subscription = await channel.SubscribeAsync(spec, CancellationToken.None);
        Assert.Equal("subscribe", TypeOf(await station.NextFrameAsync()));

        // The handler holds the first event until every tick has been routed,
        // so the consumer's copy falls behind.
        var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = Channel.CreateUnbounded<string>();
        var consuming = StationPool.ConsumeSubscriptionAsync(subscription, () => channel.SubscribeAsync(spec, CancellationToken.None), async evt =>
        {
            await routed.Task;
            handled.Writer.TryWrite(evt.Payload.AsText());
        });

        var call = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var sent = await station.NextFrameAsync();
        for (var i = 0; i <= ControlChannel.EventQueueCapacity + 1; i++)
        {
            await station.SendEventAsync("app/ticks", $"tick {i}");
        }
        await station.ReplyAsync(sent, "echoed");
        await call.WaitAsync(Wait);
        routed.TrySetResult();

        // Events reach the handler again once the fresh copy is in place.
        var deadline = DateTime.UtcNow + Wait;
        var resumed = false;
        while (!resumed && DateTime.UtcNow < deadline)
        {
            await station.SendEventAsync("app/ticks", "after the overflow");
            resumed = await SawAsync(handled.Reader, "after the overflow", TimeSpan.FromMilliseconds(100));
        }
        Assert.True(resumed);

        // A call right after shows the replacement sent neither SUBSCRIBE nor UNSUBSCRIBE.
        var probe = channel.CallAsync(Call("app/echo"), Wait, CancellationToken.None);
        var next = await station.NextFrameAsync();
        Assert.Equal("call", TypeOf(next));
        await station.ReplyAsync(next, "echoed");
        await probe.WaitAsync(Wait);

        channel.Stop(new IOException("the test is done"));
        await consuming.WaitAsync(Wait);
    }

    // Whether text arrives on reader within window, skipping anything else.
    private static async Task<bool> SawAsync(ChannelReader<string> reader, string text, TimeSpan window)
    {
        using var within = new CancellationTokenSource(window);
        try
        {
            while (await reader.ReadAsync(within.Token) != text)
            {
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
