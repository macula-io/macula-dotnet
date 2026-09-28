using System.Text.Json.Nodes;

namespace Macula.Tests;

[Collection(StationsCollection.Name)]
public sealed class PoolTests(TestStations stations)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task APoolReportsItsLinkAndItsEvents()
    {
        await using var pool = await stations.JoinAsync("links");
        var link = Assert.Single(pool.Status());
        Assert.True(link.Up);
        Assert.Equal(stations.Seeds[0].NodeId, link.Station);
        using var wait = new CancellationTokenSource(Patience);
        await foreach (var e in pool.EventsAsync(wait.Token))
        {
            var changed = Assert.IsType<LinkChanged>(e);
            Assert.True(changed.Up);
            break;
        }
    }

    [Fact]
    public async Task ACallReachesAProcedureInTheProvidersOwnNamespace()
    {
        await using var provider = await stations.JoinAsync("provider");
        await using var caller = await stations.JoinAsync("caller", station: 1);
        var procedure = provider.OwnProcedure("echo");
        Request? seen = null;
        await using var served = provider.Serve(stations.Realm, procedure, (request, _) =>
        {
            seen = request;
            return ValueTask.FromResult<JsonNode?>(new JsonObject
            {
                ["n"] = request.Payload!["n"]!.GetValue<long>(),
                ["raw"] = Payload.Bytes([1, 2, 255]),
                ["max"] = long.MaxValue,
            });
        });
        var result = await caller.CallAsync(stations.Realm, procedure, new JsonObject { ["n"] = 9007199254740993 },
            new CallOptions { Timeout = Patience });
        Assert.Equal(9007199254740993, result!["n"]!.GetValue<long>());
        Assert.Equal(long.MaxValue, result["max"]!.GetValue<long>());
        Assert.True(Payload.TryGetBytes(result["raw"], out var raw));
        Assert.Equal(new byte[] { 1, 2, 255 }, raw);
        Assert.Equal(caller.NodeId, seen!.Caller);
    }

    [Fact]
    public async Task AHandlersExceptionReachesTheCallerAsAProviderError()
    {
        await using var provider = await stations.JoinAsync("failing provider");
        await using var caller = await stations.JoinAsync("disappointed caller", station: 1);
        var procedure = provider.OwnProcedure("fail");
        await using var served = provider.Serve(stations.Realm, procedure,
            (_, _) => throw new InvalidOperationException("no such thing"));
        var error = await Assert.ThrowsAsync<ProviderErrorException>(() =>
            caller.CallAsync(stations.Realm, procedure, null, new CallOptions { Timeout = Patience }));
        Assert.Equal("handler_error", error.Code);
        Assert.Equal("no such thing", error.Detail);
    }

    [Fact]
    public async Task AResultTheWireCannotCarryIsAnsweredAsAnError()
    {
        await using var provider = await stations.JoinAsync("boolean provider");
        await using var caller = await stations.JoinAsync("boolean caller", station: 1);
        var procedure = provider.OwnProcedure("yes");
        await using var served = provider.Serve(stations.Realm, procedure,
            (_, _) => ValueTask.FromResult<JsonNode?>(JsonValue.Create(true)));
        var error = await Assert.ThrowsAsync<ProviderErrorException>(() =>
            caller.CallAsync(stations.Realm, procedure, null, new CallOptions { Timeout = Patience }));
        Assert.Contains("boolean", error.Detail);
    }

    [Fact]
    public async Task APayloadTheWireCannotCarryIsRefusedBeforeSending()
    {
        await using var caller = await stations.JoinAsync("careless caller");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            caller.CallAsync(stations.Realm, caller.OwnProcedure("x"), new JsonObject { ["ok"] = true }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            caller.CallAsync(stations.Realm, caller.OwnProcedure("x"), JsonNode.Parse("18446744073709551615")));
    }

    [Fact]
    public async Task ACallCanBeCancelledAndCanTimeOut()
    {
        await using var provider = await stations.JoinAsync("silent provider");
        await using var caller = await stations.JoinAsync("impatient caller", station: 1);
        var procedure = provider.OwnProcedure("never");
        var release = new TaskCompletionSource();
        await using var served = provider.Serve(stations.Realm, procedure, async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return null;
        });

        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var started = DateTime.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            caller.CallAsync(stations.Realm, procedure, null, new CallOptions { Timeout = TimeSpan.FromSeconds(30) },
                cancel.Token));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            caller.CallAsync(stations.Realm, procedure, null, new CallOptions { Timeout = TimeSpan.FromSeconds(1) }));
        release.SetResult();
    }

    // macula-go#8, fixed in libmacula v0.18.1: a call moves to the next provider only when it cannot reach a station,
    // never after its CALL went out. Two providers serve one procedure from the two stations and answer slower than a
    // call's share of its deadline: the call is answered and exactly one handler is entered. On v0.17.0 the call timed
    // out at the first, was sent again to the second, and both handlers ran.
    [Fact]
    public async Task ACallEntersAProvidersHandlerAtMostOnce()
    {
        await using var first = await stations.JoinAsync("once first");
        await using var second = await stations.JoinAsync("once second", station: 1);
        await using var caller = await stations.JoinAsync("once caller", station: 1);
        await stations.AdmitAsync(first.NodeId);
        await stations.AdmitAsync(second.NodeId);
        var procedure = stations.Org + "/once";
        var entered = 0;
        Func<Request, CancellationToken, ValueTask<JsonNode?>> slow = async (_, token) =>
        {
            Interlocked.Increment(ref entered);
            await Task.Delay(TimeSpan.FromSeconds(2.5), token);
            return "answered";
        };
        await using var servedFirst = first.Serve(stations.Realm, procedure, slow);
        await using var servedSecond = second.Serve(stations.Realm, procedure, slow);
        using var patience = new CancellationTokenSource(Patience);
        while ((await caller.ProvidersAsync(stations.Realm, procedure, Patience)).Count < 2)
        {
            await Task.Delay(200, patience.Token);
        }

        var result = await caller.CallAsync(stations.Realm, procedure, null,
            new CallOptions { Timeout = TimeSpan.FromSeconds(4) });
        Assert.Equal("answered", result!.GetValue<string>());
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref entered));
    }

    [Fact]
    public async Task AnOrgProcedureIsCalledOnceTheOrgDelegatesIt()
    {
        await using var provider = await stations.JoinAsync("org provider");
        await using var caller = await stations.JoinAsync("org caller", station: 1);
        await stations.AdmitAsync(provider.NodeId);
        var procedure = stations.Org + "/greet";
        await using var served = provider.Serve(stations.Realm, procedure,
            (_, _) => ValueTask.FromResult<JsonNode?>("hello"));
        using var patience = new CancellationTokenSource(Patience);
        while (true)
        {
            try
            {
                var result = await caller.CallAsync(stations.Realm, procedure, null,
                    new CallOptions { Timeout = TimeSpan.FromSeconds(5) }, patience.Token);
                Assert.Equal("hello", result!.GetValue<string>());
                break;
            }
            catch (MaculaException) when (!patience.IsCancellationRequested)
            {
                await Task.Delay(200, patience.Token);
            }
        }
        var providers = await caller.ProvidersAsync(stations.Realm, procedure, Patience);
        Assert.Contains(providers, p => p.Node == provider.NodeId);
    }

    [Fact]
    public async Task ASubscriptionHearsPublicationsUntilItsPoolCloses()
    {
        var publisher = await stations.JoinAsync("publisher");
        var listener = await stations.JoinAsync("listener");
        var subscription = listener.Subscribe(stations.Realm, "dotnet.news");
        using var patience = new CancellationTokenSource(Patience);
        var heard = Task.Run(async () =>
        {
            await foreach (var e in subscription.ReadAllAsync(patience.Token))
            {
                return e;
            }
            return null;
        });
        while (!heard.IsCompleted)
        {
            publisher.Publish(stations.Realm, "dotnet.news", new JsonObject { ["headline"] = "cabi" });
            await Task.WhenAny(heard, Task.Delay(200, patience.Token));
        }
        var e = (await heard)!;
        Assert.Equal("cabi", e.Payload!["headline"]!.GetValue<string>());
        Assert.Equal(publisher.NodeId, e.Publisher);
        Assert.Equal(0UL, subscription.Dropped);

        await listener.DisposeAsync();
        await foreach (var _ in subscription.ReadAllAsync(patience.Token))
        {
        }
        await subscription.DisposeAsync();
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task AServerStreamCarriesValuesBytesAndAReply()
    {
        await using var provider = await stations.JoinAsync("stream provider");
        await using var caller = await stations.JoinAsync("stream caller", station: 1);
        var procedure = provider.OwnProcedure("count");
        await using var served = provider.ServeStream(stations.Realm, procedure, StreamMode.Server, (stream, _) =>
        {
            var to = stream.Request.Payload!["to"]!.GetValue<long>();
            for (var i = 1; i <= to; i++)
            {
                stream.Send(new JsonObject { ["n"] = i });
            }
            stream.Send([7, 8]);
            stream.Reply("done");
            return Task.CompletedTask;
        });
        await using var opened = await caller.OpenStreamAsync(stations.Realm, procedure, StreamMode.Server,
            new JsonObject { ["to"] = 2 }, timeout: Patience);
        var frames = new List<StreamFrame>();
        using var patience = new CancellationTokenSource(Patience);
        await foreach (var frame in opened.ReadAllAsync(patience.Token))
        {
            frames.Add(frame);
        }
        var values = frames.OfType<StreamData>().Where(d => d.Encoding == "msgpack").Select(d => d.Body!["n"]!.GetValue<long>());
        Assert.Equal([1L, 2L], values);
        Assert.Contains(frames.OfType<StreamData>(), d => d.Encoding == "raw" &&
            Payload.TryGetBytes(d.Body, out var bytes) && bytes.SequenceEqual(new byte[] { 7, 8 }));
        Assert.Equal("done", frames.OfType<StreamReply>().Single().Payload!.GetValue<string>());
        Assert.IsType<StreamEof>(frames[^1]);
    }

    [Fact]
    public async Task ContentIsSharedFetchedAndUnshared()
    {
        await using var sharer = await stations.JoinAsync("sharer");
        await using var fetcher = await stations.JoinAsync("fetcher", station: 1);
        var data = new byte[600_000];
        Random.Shared.NextBytes(data);
        var mcid = await sharer.ShareContentAsync(stations.Realm, data, "blob", Patience);
        var fetched = await fetcher.GetContentAsync(stations.Realm, mcid, new ContentOptions { Parallel = 2 }, Patience);
        Assert.Equal(data, fetched);
        Assert.Equal(mcid, Mcid.Parse(mcid.ToString()));
        await sharer.UnshareContentAsync(stations.Realm, mcid, Patience);
        await Assert.ThrowsAsync<NotSharedException>(() => fetcher.GetContentAsync(stations.Realm, mcid, timeout: Patience));
    }

    [Fact]
    public async Task TheDhtAnswersWhatItHolds()
    {
        await using var pool = await stations.JoinAsync("reader");
        var missing = new byte[32];
        missing[0] = 0xee;
        Assert.Null(await pool.FindRecordAsync(new MeshId(missing), Patience));
        var endpoints = await pool.FindRecordsByTypeAsync(RecordType.StationEndpoint, Patience);
        Assert.NotEmpty(endpoints.Records);
        Assert.All(endpoints.Records, r => Assert.NotEmpty(r.Wire));
    }

    [Fact]
    public async Task AProcedureNobodyServesIsNoProvider()
    {
        await using var caller = await stations.JoinAsync("lonely caller");
        var error = await Assert.ThrowsAsync<MaculaException>(() =>
            caller.CallAsync(stations.Realm, caller.OwnProcedure("nobody"), null, new CallOptions { Timeout = Patience }));
        Assert.Equal(ErrorKind.NoProvider, error.Kind);
    }

    [Fact]
    public async Task ADisposedPoolIsRefused()
    {
        var pool = await stations.JoinAsync("gone");
        await pool.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => pool.Status());
    }
}
