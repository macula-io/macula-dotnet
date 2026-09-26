using System.Text.Json.Nodes;

namespace Macula.Examples;

// Each example is one short, complete use of the mesh. They connect a fresh
// node to the stations Mesh names (the public fleet by default).

/// <summary>A node key, a pool, and its links.</summary>
internal static class Quickstart
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pool = await Mesh.JoinAsync(cancellationToken);
        foreach (var link in pool.Status())
        {
            Console.WriteLine($"{link.Host}:{link.Port} station {link.Station} up={link.Up} direct={link.Direct}");
        }
    }
}

/// <summary>Calls a service under an org by direct dial: mcl-echo, in io.macula.</summary>
internal static class CallAService
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pool = await Mesh.JoinAsync(cancellationToken);
        var providers = await pool.ProvidersAsync(Mesh.Realm, "mcl-echo/echo", TimeSpan.FromSeconds(15), cancellationToken);
        Console.WriteLine($"{providers.Count} provider(s) of mcl-echo/echo");
        var reply = await pool.CallAsync(Mesh.Realm, "mcl-echo/echo", new JsonObject { ["message"] = "hello from .NET" },
            new CallOptions { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken);
        Console.WriteLine($"reply: {reply?.ToJsonString()}");
    }
}

/// <summary>
/// Serves a procedure in the node's own namespace, <c>~&lt;node id&gt;/greet</c>, which only it can
/// serve, and calls it from a second node.
/// </summary>
internal static class ServeAndCall
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var provider = await Mesh.JoinAsync(cancellationToken);
        await using var caller = await Mesh.JoinAsync(cancellationToken);
        var greet = provider.OwnProcedure("greet");
        await using var served = provider.Serve(Mesh.Realm, greet, (request, _) =>
            ValueTask.FromResult<JsonNode?>(new JsonObject
            {
                ["greeting"] = $"hello, {request.Payload?["name"]?.GetValue<string>() ?? "stranger"}",
                ["caller"] = request.Caller.ToString(),
            }));
        var reply = await caller.CallAsync(Mesh.Realm, greet, new JsonObject { ["name"] = ".NET" },
            new CallOptions { Timeout = TimeSpan.FromSeconds(15) }, cancellationToken);
        Console.WriteLine($"{greet} answered {reply?.ToJsonString()}");
    }
}

/// <summary>
/// Serves a procedure gated on a UCAN, in the node's own namespace under a throwaway realm named for the run
/// (a realm's id is its name's SHA-256, so a grant names it), and calls it without a token (refused
/// <c>unauthorized</c>) and with the grant a root key made for the caller.
/// </summary>
internal static class GatedProcedure
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        var realmName = $"macula-dotnet-example-{Guid.NewGuid():N}"[..34];
        var realm = new MeshId(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(realmName)));
        using var root = await NodeKey.GenerateAsync(Profile.PqHybrid, cancellationToken);
        await using var provider = await Mesh.JoinAsync(cancellationToken);
        await using var caller = await Mesh.JoinAsync(cancellationToken);
        var gated = provider.OwnProcedure("gated");
        await using var served = provider.Serve(realm, gated, (_, _) => ValueTask.FromResult<JsonNode?>("served"),
            new UcanRequired(root.NodeId));
        var grant = root.CreateUcan(caller.NodeId, [new Capability($"mri:realm:{realmName}", "invoke")],
            DateTimeOffset.UtcNow.AddMinutes(5));
        var options = new CallOptions { Timeout = TimeSpan.FromSeconds(15), Ucan = new UcanPresentation(grant) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JsonNode? reply;
        while (true)
        {
            try
            {
                reply = await caller.CallAsync(realm, gated, new JsonObject(), options, cancellationToken);
                break;
            }
            // unknown_next_peer: the caller's station has not seen the advertisement yet (a relay error), or
            // the provider is not serving yet (its own answer).
            catch (MaculaException e) when (e is RelayErrorException { Code: "unknown_next_peer" }
                                                 or ProviderErrorException { Code: "unknown_next_peer" }
                                             && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500, cancellationToken); // the advertisement is still on its way
            }
        }
        Console.WriteLine($"{gated} with the root's grant: {reply?.ToJsonString()}");
        try
        {
            await caller.CallAsync(realm, gated, new JsonObject(), new CallOptions { Timeout = TimeSpan.FromSeconds(15) },
                cancellationToken);
            throw new InvalidOperationException("a call without a token was served");
        }
        catch (ProviderErrorException e) when (e.Code == "unauthorized")
        {
            Console.WriteLine($"{gated} without a token: {e.Code}");
        }
    }
}

/// <summary>Publishes on a topic and hears it back through a subscription.</summary>
internal static class PublishSubscribe
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pool = await Mesh.JoinAsync(cancellationToken);
        var topic = $"examples.dotnet.{pool.NodeId.ToString()[..8]}";
        await using var subscription = pool.Subscribe(Mesh.Realm, topic);
        using var heard = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listening = Task.Run(async () =>
        {
            await foreach (var e in subscription.ReadAllAsync(heard.Token))
            {
                Console.WriteLine($"heard seq {e.Seq} from {e.Publisher} via {e.DeliveredVia}: {e.Payload?.ToJsonString()}");
                return;
            }
        }, cancellationToken);
        while (!listening.IsCompleted)
        {
            pool.Publish(Mesh.Realm, topic, new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await Task.WhenAny(listening, Task.Delay(500, cancellationToken));
        }
        await listening;
    }
}

/// <summary>A server stream: the provider sends values and bytes, then its reply.</summary>
internal static class Streams
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var provider = await Mesh.JoinAsync(cancellationToken);
        await using var caller = await Mesh.JoinAsync(cancellationToken);
        var count = provider.OwnProcedure("count");
        await using var served = provider.ServeStream(Mesh.Realm, count, StreamMode.Server, (stream, _) =>
        {
            var to = stream.Request.Payload?["to"]?.GetValue<long>() ?? 3;
            for (var i = 1; i <= to; i++)
            {
                stream.Send(new JsonObject { ["n"] = i });
            }
            stream.Send("raw bytes"u8);
            stream.Reply("counted");
            return Task.CompletedTask;
        });
        await using var opened = await caller.OpenStreamAsync(Mesh.Realm, count, StreamMode.Server,
            new JsonObject { ["to"] = 3 }, timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);
        await foreach (var frame in opened.ReadAllAsync(cancellationToken))
        {
            Console.WriteLine(frame);
        }
    }
}

/// <summary>Shares content from one node and fetches it, verified, from another.</summary>
internal static class ShareContent
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var sharer = await Mesh.JoinAsync(cancellationToken);
        await using var fetcher = await Mesh.JoinAsync(cancellationToken);
        var data = new byte[300_000];
        Random.Shared.NextBytes(data);
        var mcid = await sharer.ShareContentAsync(Mesh.Realm, data, "example.bin", TimeSpan.FromSeconds(15), cancellationToken);
        Console.WriteLine($"shared {data.Length} bytes as {mcid}");
        var fetched = await fetcher.GetContentAsync(Mesh.Realm, mcid, timeout: TimeSpan.FromSeconds(30),
            cancellationToken: cancellationToken);
        Console.WriteLine($"fetched {fetched.Length} bytes, identical: {fetched.AsSpan().SequenceEqual(data)}");
        await sharer.UnshareContentAsync(Mesh.Realm, mcid, TimeSpan.FromSeconds(15), cancellationToken);
    }
}

/// <summary>Reads the DHT: every station's own endpoint record.</summary>
internal static class ReadTheDht
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pool = await Mesh.JoinAsync(cancellationToken);
        var found = await pool.FindRecordsByTypeAsync(RecordType.StationEndpoint, TimeSpan.FromSeconds(15), cancellationToken);
        Console.WriteLine($"{found.Records.Count} station endpoint record(s), {found.Dropped} that did not verify");
        foreach (var record in found.Records)
        {
            Console.WriteLine($"  signed by {record.KeyId}: {record.Payload?.ToJsonString()}");
        }
    }
}

/// <summary>What each kind of failure looks like.</summary>
internal static class ErrorHandling
{
    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var pool = await Mesh.JoinAsync(cancellationToken);
        var nobody = pool.OwnProcedure("nobody-serves-this");
        try
        {
            await pool.CallAsync(Mesh.Realm, nobody, null, new CallOptions { Timeout = TimeSpan.FromSeconds(5) }, cancellationToken);
        }
        catch (MaculaException e)
        {
            Console.WriteLine($"a procedure nobody serves: {e.Kind}: {e.Message}");
        }

        try
        {
            await pool.CallAsync(Mesh.Realm, nobody, new JsonObject { ["flag"] = true }, cancellationToken: cancellationToken);
        }
        catch (ArgumentException e)
        {
            Console.WriteLine($"a boolean in a payload: {e.Message}");
        }

        // A procedure that answers only when told to, to cancel a call that is
        // really waiting, and to let another run out of time.
        var slow = pool.OwnProcedure("slow");
        var release = new TaskCompletionSource();
        await using (pool.Serve(Mesh.Realm, slow, async (_, deadline) =>
        {
            await release.Task.WaitAsync(deadline);
            return null;
        }))
        {
            try
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await pool.CallAsync(Mesh.Realm, slow, null, new CallOptions { Timeout = TimeSpan.FromSeconds(30) }, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("a call cancelled while it waited: OperationCanceledException");
            }
            try
            {
                await pool.CallAsync(Mesh.Realm, slow, null, new CallOptions { Timeout = TimeSpan.FromSeconds(2) }, cancellationToken);
            }
            catch (TimeoutException e)
            {
                Console.WriteLine($"a call past its timeout: TimeoutException: {e.Message}");
            }
            release.SetResult();
        }

        try
        {
            var mcid = Mcid.Parse("0255" + new string('0', 96));
            await pool.GetContentAsync(Mesh.Realm, mcid, timeout: TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);
        }
        catch (NotSharedException e)
        {
            Console.WriteLine($"content nobody shares: NotSharedException: {e.Message}");
        }
    }
}
