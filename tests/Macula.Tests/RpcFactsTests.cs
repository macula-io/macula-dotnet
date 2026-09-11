using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Live proof that RPC telemetry auto-facts (rpc.sent_v1/rpc.completed_v1
/// caller-side, rpc.received_v1/rpc.replied_v1 provider-side) actually
/// land, confirmed via independent watcher sessions, not the caller's/
/// provider's own bookkeeping. These are FIXED, well-known topic names on
/// a shared PUBLIC demo fleet -- unlike this test file's siblings, which
/// dodge collisions with real third-party traffic by randomizing the topic
/// string, the watchers here collect everything and the test picks out the
/// facts it caused: published by its own caller or provider, in pairs that
/// share one request_id, as macula-go's equivalent test does. Taking the
/// latest fact instead failed whenever another call on the fleet published
/// in between. Same fleet-flakiness caveat as <see cref="LiveStationTests"/>.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class RpcFactsTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Rpc_telemetry_facts_land_with_correct_request_ids()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var callerId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.rpc_facts_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        await provider.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerId.NodeId() });

        var served = Task.Run(async () =>
        {
            CallLookup lookup = (_, proc) => proc != procedure ? null : payload => Task.FromResult(payload);
            await provider.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(15));
        });

        // Independent watchers, one per fact topic, subscribed BEFORE the
        // call happens.
        var sentWatcher = new FactWatcher(realm, "rpc.sent_v1");
        var completedWatcher = new FactWatcher(realm, "rpc.completed_v1");
        var receivedWatcher = new FactWatcher(realm, "rpc.received_v1");
        var repliedWatcher = new FactWatcher(realm, "rpc.replied_v1");
        await Task.WhenAll(
            sentWatcher.StartAsync(StationHost, StationPort),
            completedWatcher.StartAsync(StationHost, StationPort),
            receivedWatcher.StartAsync(StationHost, StationPort),
            repliedWatcher.StartAsync(StationHost, StationPort));

        await Task.Delay(TimeSpan.FromMilliseconds(500)); // let subscriptions land

        await using var caller = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki);
        var response = await caller.CallAsync(procedure, realm, Value.Text("ping"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, TimeSpan.FromSeconds(10));
        Assert.IsType<CallResponse.Result>(response);
        await served;

        var (_, completedFact) = await PairAsync(sentWatcher, completedWatcher, callerId.NodeId(), TimeSpan.FromSeconds(10));
        Assert.Equal("completed", completedFact.Get("outcome")!.AsText());

        var (_, repliedFact) = await PairAsync(receivedWatcher, repliedWatcher, providerId.NodeId(), TimeSpan.FromSeconds(10));
        Assert.Equal("replied", repliedFact.Get("outcome")!.AsText());

        await sentWatcher.StopAsync();
        await completedWatcher.StopAsync();
        await receivedWatcher.StopAsync();
        await repliedWatcher.StopAsync();
    }

    /// <summary>
    /// A fact from firsts and a fact from seconds that share one request_id,
    /// both published by publisher, waiting up to timeout for such a pair to
    /// arrive.
    /// </summary>
    private static async Task<(Value.MapValue First, Value.MapValue Second)> PairAsync(FactWatcher firsts, FactWatcher seconds, byte[] publisher, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var candidates = seconds.PublishedBy(publisher);
            foreach (var first in firsts.PublishedBy(publisher))
            {
                if (candidates.FirstOrDefault(candidate => RequestId(candidate).AsSpan().SequenceEqual(RequestId(first))) is { } second)
                {
                    return (first, second);
                }
            }
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail($"no {firsts.Topic}/{seconds.Topic} pair from this publisher shared a request_id within {timeout}; {firsts.Count} and {seconds.Count} fact(s) seen from anyone");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }

    private static byte[] RequestId(Value.MapValue fact)
    {
        var id = fact.Get("request_id")!.AsBytes();
        Assert.Equal(16, id.Length);
        return id;
    }

    /// <summary>
    /// Subscribes to one fixed, shared topic and buffers whatever real
    /// events arrive (including unrelated third-party traffic on this
    /// public fleet), so the test above can pick out the ones it actually
    /// caused rather than trusting the latest arrival.
    /// </summary>
    private sealed class FactWatcher
    {
        private readonly byte[] _realm;
        private readonly List<EventInfo> _received = new();
        private readonly object _lock = new();
        private Session? _session;
        private CancellationTokenSource? _cts;
        private Task? _task;

        public FactWatcher(byte[] realm, string topic)
        {
            _realm = realm;
            Topic = topic;
        }

        public string Topic { get; }

        /// <summary>How many facts arrived on this topic, from anyone.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _received.Count;
                }
            }
        }

        public async Task StartAsync(string host, int port)
        {
            var id = KeyPair.GenerateWithDefaultPuzzle();
            _session = await Session.ConnectAsync(host, port, id, Connection.Trust.UseWebPki);
            _cts = new CancellationTokenSource();
            _task = SupervisedPubSub.RunSubscriberAsync(
                _session,
                new SubscribeSpec { Topic = Topic, Realm = _realm, Subscriber = id.NodeId() },
                id,
                evt =>
                {
                    lock (_lock)
                    {
                        _received.Add(evt);
                    }
                    return Task.CompletedTask;
                },
                _cts.Token);
        }

        /// <summary>The payloads of the facts so far that publisher published.</summary>
        public List<Value.MapValue> PublishedBy(byte[] publisher)
        {
            lock (_lock)
            {
                return _received
                    .Where(evt => evt.Publisher.AsSpan().SequenceEqual(publisher))
                    .Select(evt => (Value.MapValue)evt.Payload)
                    .ToList();
            }
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            if (_task is not null)
            {
                try
                {
                    await _task;
                }
                catch (OperationCanceledException)
                {
                    // expected
                }
            }
            if (_session is not null)
            {
                await _session.DisposeAsync();
            }
        }
    }
}
