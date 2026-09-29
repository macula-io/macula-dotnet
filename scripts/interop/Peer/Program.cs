// The Macula package's side of the live checks against macula (scripts/interop/sealed.sh and v5.sh), the .NET twin of
// macula-go's gosealed and gov5link:
//
//   Peer serve <host:port@node_id hex> <profile> <realm hex> <hold s>
//   Peer call <host:port@node_id hex> <profile> <realm hex> <provider node_id hex> [<peer>]
//   Peer v5link <host> <port> <node_id hex> <profile> <hold s>
//
// serve connects with KemAdvertise, serves ~<self>/vault (a call) and ~<self>/watch (a server stream), both
// Required, refuses any request that did not arrive sealed, prints "node <hex>" and "serving" and holds. call calls
// ~<provider>/vault with CallReportAsync and opens ~<provider>/watch, both Required, expecting the peer's texts
// ("kept by <peer>", "chunk from <peer>", "streamed by <peer>", peer "erlang" by default), and requires each seal
// report to say sealed, the provider called and a 16-hex key id. v5link links to one station, holds the link while
// the station probes it, and requires it still up. Each prints its outcomes and "verdict: PASS" or exits 1.
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Macula;

return await Run(args);

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
        default:
            Console.Error.WriteLine("Peer serve|call|v5link ...");
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
    var called = reported?.Result?.GetValue<string>();
    Console.WriteLine($"sealed call: {(failed is null ? called : $"{failed.GetType().Name} {failed.Message}")}");
    Console.WriteLine($"call report: {reported?.Report}");
    var ok = called == $"kept by {peer}" && SealedTo(reported?.Report, provider);

    string? chunk = null;
    string? reply = null;
    SealReport? streamReport = null;
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
        streamReport = stream.Report();
    }
    catch (Exception e)
    {
        Console.WriteLine($"sealed stream error: {e.GetType().Name} {e.Message}");
    }
    Console.WriteLine($"sealed stream: chunk {chunk}, reply {reply}");
    Console.WriteLine($"stream report: {streamReport}");
    ok = ok && chunk == $"chunk from {peer}" && reply == $"streamed by {peer}" && SealedTo(streamReport, provider);
    Console.WriteLine($"verdict: {(ok ? "PASS" : "FAIL")}");
    return ok ? 0 : 1;
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
