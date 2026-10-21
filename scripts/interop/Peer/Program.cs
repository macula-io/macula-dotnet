// The Macula package's side of the live checks against macula (scripts/interop/sealed.sh and v5.sh), the .NET twin of
// macula-go's gosealed and gov5link, and of sealed groups (scripts/interop/groups.sh), the .NET twin of
// macula-go's gogroups:
//
//   Peer serve <host:port@node_id hex> <profile> <realm hex> <hold s>
//   Peer call <host:port@node_id hex> <profile> <realm hex> <provider node_id hex> [<peer>]
//   Peer v5link <host> <port> <node_id hex> <profile> <hold s>
//   Peer groups <host:port@node_id hex> <profile> <realm hex> <realm key hex> <work dir> <prefix> <topic> member
//   Peer groups ... stranger
//
// serve connects with KemAdvertise, serves ~<self>/vault (a call) and ~<self>/watch (a server stream), both
// Required, refuses any request that did not arrive sealed, prints "node <hex>" and "serving" and holds. call calls
// ~<provider>/vault with CallReportAsync and opens ~<provider>/watch, both Required, expecting the peer's texts
// ("kept by <peer>", "chunk from <peer>", "streamed by <peer>", peer "erlang" by default), and requires each seal
// report to say sealed, the provider called and a 16-hex key id. v5link links to one station, holds the link while
// the station probes it, and requires it still up. groups connects a pool pinning the realm key, prints
// "node <hex>", waits for <work>/grant.<hex> (the org's grant the script minted for this node) and takes the seat
// macula-go's gogroups takes: member subscribes to topic under the group prefix, pulling its epoch from the Erlang
// distributor over a sealed call, writes <work>/ready.go, waits for <work>/ready.erlang, publishes "from go" sealed
// under the group, and waits for the Erlang member's "from erlang" to arrive opened; stranger, a node the
// distributor's removed set names, checks its subscribe is refused not_a_member. Each prints its outcomes and
// "verdict: PASS" or exits 1.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Macula;

internal static class Program
{
    // The seat macula-go's gogroups takes for sealed groups: the Erlang member writes ready.erlang and waits for
    // ready.go, so the .NET member owns ready.go, and the texts are the pair's.
    const string BarrierOwn = "ready.go";
    const string BarrierOther = "ready.erlang";
    const string SaysOwn = "from go";
    const string SaysOther = "from erlang";


    static Task<int> Main(string[] args) => Run(args);

    static async Task<int> Run(string[] args)
    {
        switch (args)
        {
            case ["serve", var station, var profile, var realm, var hold]:
                return await Serve(Seed(station), ProfileOf(profile), MeshId.Parse(realm), TimeSpan.FromSeconds(int.Parse(hold)));
            case ["call", var station, var profile, var realm, var provider, .. var rest]:
                return await Call(Seed(station), ProfileOf(profile), MeshId.Parse(realm), MeshId.Parse(provider),
                    rest.Length > 0 ? rest[0] : "erlang");
            case ["v5link", var host, var port, var node, var profile, var hold]:
                return await V5Link(new Seed(host, ushort.Parse(port), MeshId.Parse(node)), ProfileOf(profile),
                    TimeSpan.FromSeconds(int.Parse(hold)));
            case ["groups", var station, var profile, var realm, var realmKey, var work, var prefix, var topic, var mode]:
                return await Groups(Seed(station), ProfileOf(profile), MeshId.Parse(realm), realmKey, work, prefix, topic,
                    mode);
            default:
                Console.Error.WriteLine("Peer serve|call|v5link|groups ...");
                return 2;
        }
    }

    static Seed Seed(string text)
    {
        var m = Regex.Match(text, "^(.+):(\\d+)@([0-9a-fA-F]{64})$");
        if (!m.Success)
        {
            throw new ArgumentException($"the station is host:port@<node_id hex>, not {text}");
        }
        return new Seed(m.Groups[1].Value.Trim('[', ']'), ushort.Parse(m.Groups[2].Value), MeshId.Parse(m.Groups[3].Value));
    }

    static Profile ProfileOf(string name) => name switch
    {
        "pq_pure" => Profile.PqPure,
        "pq_hybrid" => Profile.PqHybrid,
        _ => throw new ArgumentException($"a profile is pq_pure or pq_hybrid, not {name}"),
    };

    static async Task<Pool> Connect(Seed seed, Profile profile, bool kemAdvertise)
    {
        using var key = await NodeKey.GenerateAsync(profile);
        var pool = await Pool.ConnectAsync(key, [seed], new PoolOptions
        {
            KemAdvertise = kemAdvertise, ConnectTimeout = TimeSpan.FromSeconds(60),
        });
        Console.WriteLine($"node {pool.NodeId}");
        return pool;
    }

    static async Task<int> Serve(Seed seed, Profile profile, MeshId realm, TimeSpan hold)
    {
        await using var pool = await Connect(seed, profile, kemAdvertise: true);
        await using var vault = pool.Serve(realm, pool.OwnProcedure("vault"), (request, _) =>
            request.Sealed
                ? ValueTask.FromResult<JsonNode?>("kept by dotnet")
                : throw new InvalidOperationException("a clear request reached the handler"),
            confidential: ServedConfidential.Required);
        await using var watch = pool.ServeStream(realm, pool.OwnProcedure("watch"), StreamMode.Server, (stream, _) =>
        {
            if (!stream.Request.Sealed)
            {
                throw new InvalidOperationException("a clear session reached the handler");
            }
            stream.Send("chunk from dotnet"u8);
            stream.Reply("streamed by dotnet");
            return Task.CompletedTask;
        }, confidential: ServedConfidential.Required);
        Console.WriteLine("serving");
        await Task.Delay(hold);
        return 0;
    }

    static bool SealedTo(SealReport? report, MeshId provider) =>
        report is { Sealed: true } r && r.Provider == provider && Regex.IsMatch(r.SealKeyId ?? "", "^[0-9a-f]{16}$");

    static async Task<int> Call(Seed seed, Profile profile, MeshId realm, MeshId provider, string peer)
    {
        await using var pool = await Connect(seed, profile, kemAdvertise: false);
        var options = new CallOptions { Timeout = TimeSpan.FromSeconds(10), Confidential = Confidential.Required };
        var vault = $"~{provider}/vault";
        var watch = $"~{provider}/watch";
        var (reported, failed) = await CallWhileNoProvider(pool, realm, vault, options);
        var called = reported?.Result?.GetValue<string>();
        PrintCall(called, failed, reported?.Report);
        var ok = called == $"kept by {peer}" && SealedTo(reported?.Report, provider);

        var streamed = await ReadStream(pool, realm, watch, provider);
        ok = ok && streamed.Ok(peer, provider);
        Console.WriteLine($"verdict: {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// The sealed call's answer, or how it failed, and its seal report.
    static void PrintCall(string? called, Exception? failed, SealReport? report)
    {
        var said = failed is null ? called : $"{failed.GetType().Name} {failed.Message}";
        Console.WriteLine($"sealed call: {said}");
        Console.WriteLine($"call report: {report}");
    }

    /// The call, retried while the provider has not joined the mesh yet: up to
    /// thirty seconds of NoProvider, then whatever answer or failure came.
    static async Task<(Reported? Reported, Exception? Failed)> CallWhileNoProvider(Pool pool, MeshId realm,
        string vault, CallOptions options)
    {
        Reported? reported = null;
        Exception? failed = null;
        for (var i = 0; i < 150 && reported is null; i++)
        {
            try
            {
                reported = await pool.CallReportAsync(realm, vault, new JsonObject { ["n"] = 1 }, options);
                failed = null;
            }
            catch (MaculaException e) when (e.Kind == ErrorKind.NoProvider)
            {
                failed = e;
                await Task.Delay(200);
            }
            catch (Exception e)
            {
                failed = e;
                break;
            }
        }
        return (reported, failed);
    }

    /// What the provider's server stream said: its first chunk, its reply and
    /// the seal report the stream settled on.
    readonly record struct Streamed(string? Chunk, string? Reply, SealReport? Report, string? Error)
    {
        /// True when every part of the stream is the peer's, and sealed to it.
        public bool Ok(string peer, MeshId provider) =>
            Chunk == $"chunk from {peer}" && Reply == $"streamed by {peer}" && SealedTo(Report, provider);

        public void Print()
        {
            Console.WriteLine($"sealed stream: chunk {Chunk}, reply {Reply}");
            Console.WriteLine($"stream report: {Report}");
        }
    }

    static async Task<Streamed> ReadStream(Pool pool, MeshId realm, string watch, MeshId provider)
    {
        string? chunk = null;
        string? reply = null;
        SealReport? report = null;
        try
        {
            await using var stream = await pool.OpenStreamAsync(realm, watch, StreamMode.Server, new JsonObject(),
                timeout: TimeSpan.FromSeconds(10), confidential: Confidential.Required);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await foreach (var frame in stream.ReadAllAsync(wait.Token))
            {
                if (frame is StreamData { Body: var body } && Payload.TryGetBytes(body, out var bytes))
                {
                    chunk = Encoding.UTF8.GetString(bytes);
                }
                if (frame is StreamReply { Payload: var payload })
                {
                    reply = payload?.GetValue<string>();
                }
            }
            // Settled on the provider's first chunk opened under the stream's key, and kept after the end.
            report = stream.Report();
        }
        catch (Exception e)
        {
            Console.WriteLine($"sealed stream error: {e.GetType().Name} {e.Message}");
        }
        var streamed = new Streamed(chunk, reply, report, null);
        streamed.Print();
        return streamed;
    }

    static async Task<int> V5Link(Seed seed, Profile profile, TimeSpan hold)
    {
        bool up;
        await using (var pool = await Connect(seed, profile, kemAdvertise: false))
        {
            Console.WriteLine($"linked: {string.Join(", ", pool.Status())}");
            await Task.Delay(hold);
            up = pool.Status().Any(s => s.Station == seed.NodeId && s.Up);
            Console.WriteLine($"after {hold.TotalSeconds} s: {string.Join(", ", pool.Status())}");
        }
        Console.WriteLine($"verdict: {(up ? "PASS" : "FAIL")}");
        return up ? 0 : 1;
    }

    // ---- Sealed groups (scripts/interop/groups.sh) ----

    static async Task<int> Groups(Seed seed, Profile profile, MeshId realm, string realmKey, string work, string prefix,
        string topic, string mode)
    {
        using var key = await NodeKey.GenerateAsync(profile);
        await using var pool = await Pool.ConnectAsync(key, [seed], new PoolOptions
        {
            RealmTrust = new Dictionary<MeshId, byte[]> { [realm] = Convert.FromHexString(realmKey) },
            ConnectTimeout = TimeSpan.FromSeconds(60),
        });
        Console.WriteLine($"node {pool.NodeId}");
        Console.Out.Flush();
        var grant = (await Waited(work, $"grant.{pool.NodeId}")).Trim();
        var outcome = mode switch
        {
            "member" => await GroupMember(pool, realm, work, prefix, topic, grant),
            "stranger" => await GroupStranger(pool, realm, prefix, topic, grant),
            _ => throw new ArgumentException($"a mode is member or stranger, not {mode}"),
        };
        Console.WriteLine($"verdict: {(outcome ? "PASS" : "FAIL")}");
        return outcome ? 0 : 1;
    }

    // A file the orchestrating script writes, read once it is there.
    static async Task<string> Waited(string work, string name)
    {
        var path = Path.Combine(work, name);
        for (var i = 0; i < 900; i++)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }
            await Task.Delay(200);
        }
        throw new FileNotFoundException($"no {path}");
    }

    // Subscribing under group, waiting while the distributor's advertisement has not reached the DHT yet (macula-go's
    // subscribed: only no_distributor is the DHT's own reach, every other reason is macula's).
    static async Task<Subscription> Subscribed(Pool pool, MeshId realm, string topic, Macula.Group group)
    {
        for (var i = 0; i < 150; i++)
        {
            try
            {
                return await pool.SubscribeGroupAsync(realm, topic, group);
            }
            catch (GroupException e) when (e.Reason == "no_distributor")
            {
            }
            await Task.Delay(200);
        }
        return await pool.SubscribeGroupAsync(realm, topic, group);
    }

    // The payload as the text a member published, when it is one.
    static string? Text(JsonNode? payload) =>
        payload is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static async Task<bool> GroupMember(Pool pool, MeshId realm, string work, string prefix, string topic, string grant)
    {
        var group = new Macula.Group(prefix, grant);
        await using var subscription = await Subscribed(pool, realm, topic, group);
        Console.WriteLine("subscribed");
        await File.WriteAllTextAsync(Path.Combine(work, BarrierOwn), "");
        await Waited(work, BarrierOther);
        await pool.PublishGroupAsync(realm, topic, JsonValue.Create(SaysOwn), group);
        Console.WriteLine("published");
        using var watching = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await foreach (var e in subscription.ReadAllAsync(watching.Token))
            {
                if (Text(e.Payload) == SaysOwn)
                {
                    continue;
                }
                if (e.Unopened is { } unopened)
                {
                    Console.WriteLine($"unopened: {unopened}");
                    return false;
                }
                if (Text(e.Payload) != SaysOther)
                {
                    Console.WriteLine($"unexpected: sealed {e.Sealed}, payload {Text(e.Payload)}");
                    return false;
                }
                // The point of the run: the peer's event arrives because the member opened it under the group, not
                // because it was published clear.
                if (!e.Sealed || !Regex.IsMatch(e.SealKeyId ?? "", "^[0-9a-f]{16}$"))
                {
                    Console.WriteLine($"unexpected: \"{SaysOther}\" arrived unsealed ({e.Sealed}), key id {e.SealKeyId}");
                    return false;
                }
                Console.WriteLine($"opened from erlang under {e.SealKeyId}");
                return true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("nothing from erlang");
        return false;
    }

    // A node the Erlang distributor's removed set names: the group it holds no key for must not be joined, and the
    // refusal must travel by name.
    static async Task<bool> GroupStranger(Pool pool, MeshId realm, string prefix, string topic, string grant)
    {
        try
        {
            await using var subscription = await Subscribed(pool, realm, topic, new Macula.Group(prefix, grant));
            Console.WriteLine("stranger subscribe: served");
            return false;
        }
        catch (GroupException e)
        {
            Console.WriteLine($"stranger subscribe: {e.Message}");
            return e.Reason == "not_a_member";
        }
    }
}
