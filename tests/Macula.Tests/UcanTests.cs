using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Macula.Tests;

/// <summary>
/// macula 12's UCANs (D7) through this binding: tokens minted here, proof ids and key ids as macula's vectors
/// have them (fixtures/ucan), and procedures served gated on a policy, whose provider judges every call and
/// stream open as macula's link does. The verdicts themselves are held to macula's vectors in macula-go,
/// whose gate this binding's provider runs; here each is reached end to end, over two in-process stations,
/// in both profiles.
/// </summary>
public sealed class UcanVectorTests
{
    private const string VectorsSha256 = "b64cb27aa639ea7ec103523b808e766c656220610f9a26a7801af31fe7c3b296";

    internal static JsonDocument Vectors()
    {
        var data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "ucan", "ucan_v1.json"));
        Assert.True(Convert.ToHexStringLower(SHA256.HashData(data)) == VectorsSha256, "ucan_v1.json drifted from the pinned bytes");
        return JsonDocument.Parse(data);
    }

    [Fact]
    public void ProofIdsAreMaculas()
    {
        using var vectors = Vectors();
        var profiles = vectors.RootElement.GetProperty("profiles");
        Assert.Equal(["pq_hybrid", "pq_pure"], profiles.EnumerateObject().Select(p => p.Name).Order());
        foreach (var profile in profiles.EnumerateObject())
        {
            var entries = profile.Value.GetProperty("proof_ids").EnumerateArray().ToList();
            Assert.NotEmpty(entries);
            foreach (var entry in entries)
            {
                Assert.Equal(entry.GetProperty("proof_id").GetString(), Ucan.ProofId(entry.GetProperty("token").GetString()!));
            }
        }
    }

    [Fact]
    public void KeyIdsAreMaculas()
    {
        using var vectors = Vectors();
        foreach (var profile in vectors.RootElement.GetProperty("profiles").EnumerateObject())
        {
            var p = profile.Name == "pq_pure" ? Profile.PqPure : Profile.PqHybrid;
            foreach (var key in profile.Value.GetProperty("keys").EnumerateObject())
            {
                var carried = Carried(key.Value.GetProperty("did_key").GetString()!);
                Assert.Equal(key.Value.GetProperty("key_id").GetString(), Ucan.KeyId(carried, p).ToString());
            }
        }
    }

    // The key a did:key carries: base58btc, then past the multicodec varint.
    private static byte[] Carried(string didKey)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        Assert.StartsWith("did:key:z", didKey);
        var text = didKey["did:key:z".Length..];
        var n = System.Numerics.BigInteger.Zero;
        foreach (var c in text)
        {
            n = n * 58 + alphabet.IndexOf(c);
        }
        var raw = n.ToByteArray(isUnsigned: true, isBigEndian: true);
        raw = [.. new byte[text.Length - text.TrimStart('1').Length], .. raw];
        var i = 0;
        while ((raw[i] & 0x80) != 0)
        {
            i++;
        }
        return raw[(i + 1)..];
    }

    [Fact]
    public async Task MintingRefusesWhatItCannotExpress()
    {
        using var key = await NodeKey.GenerateAsync(Profile.PqPure);
        var audience = new MeshId(new byte[32]);
        var expires = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.Throws<ArgumentException>(() => key.CreateUcan(audience, [new Capability("mri:realm:x", "")], expires));
        Assert.Throws<ArgumentException>(() => key.CreateUcan(audience, [new Capability("", "invoke")], expires));
        Assert.Throws<ArgumentException>(() => new RealmMemberRequired(audience, ""));
        var token = key.CreateUcan(audience, [new Capability("mri:realm:x", "invoke")], expires);
        Assert.Equal(96, Ucan.ProofId(token).Length);
        Assert.Throws<ArgumentException>(() => Ucan.ProofId(""));
        // A key of another length than its profile's would name an id no policy ever matches.
        Assert.Throws<ArgumentException>(() => Ucan.KeyId(new byte[2592], Profile.PqHybrid));
        Assert.Throws<ArgumentException>(() => Ucan.KeyId(new byte[3118], Profile.PqPure));
    }
}

/// <summary>The gated serving checks, run over one station harness.</summary>
public abstract class GatedServingTests(TestStations stations)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private async Task<(Pool Provider, Pool Caller, NodeKey Root, NodeKey Alice)> WorldAsync(string name)
    {
        var provider = await stations.JoinAsync(name + " provider");
        await stations.AdmitAsync(provider.NodeId);
        var caller = await stations.JoinAsync(name + " caller", station: 1);
        return (provider, caller, await NodeKey.GenerateAsync(stations.Profile), await NodeKey.GenerateAsync(stations.Profile));
    }

    // What a caller presents, and the provider's answer: null to serve it, else the code it refuses with.
    private Dictionary<string, (UcanPresentation? Ucan, string? Refused)> Presentations(Pool caller, NodeKey root,
        NodeKey alice, string procedure)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        var org = new Capability($"mri:org:{stations.RealmName}/{stations.Org}", "invoke");
        var direct = root.CreateUcan(caller.NodeId, [org], expires);
        var toAlice = root.CreateUcan(alice.NodeId, [org], expires);
        var chained = alice.CreateUcan(caller.NodeId, [new Capability($"mri:proc:{stations.RealmName}/{procedure}", "invoke")],
            expires, new UcanOptions { Parent = Ucan.ProofId(toAlice) });
        var other = alice.CreateUcan(caller.NodeId, [org], expires);
        return new()
        {
            ["a root grant"] = (new UcanPresentation(direct), null),
            ["a delegated chain"] = (new UcanPresentation(chained, [toAlice]), null),
            ["no token"] = (null, "unauthorized"),
            ["another issuer"] = (new UcanPresentation(other), "unauthorized"),
            ["a chain without its proof"] = (new UcanPresentation(chained), "unauthorized"),
            ["a proof no token names"] = (new UcanPresentation(direct, [toAlice]), "malformed_frame"),
        };
    }

    // attempt until the provider, not the DHT's reach, answers.
    private static async Task<T> UntilServed<T>(Func<Task<T>> attempt)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                return await attempt();
            }
            // Retried: anything but a provider's own answer, which ends the attempt. unknown_next_peer
            // comes from a station that has not seen the advertisement yet (a RelayErrorException, retried
            // with the rest) or from a provider not yet serving (a ProviderErrorException with that code).
            catch (Exception e) when (DateTime.UtcNow < deadline && e is not ProviderErrorException { Code: not "unknown_next_peer" })
            {
                await Task.Delay(200);
            }
        }
    }

    [Fact]
    public async Task AGatedProcedureJudgesEachCall()
    {
        var (provider, caller, root, alice) = await WorldAsync("gated call");
        await using var p = provider;
        await using var c = caller;
        using var r = root;
        using var a = alice;
        var procedure = $"{stations.Org}/count";
        await using var served = provider.Serve(stations.Realm, procedure,
            (_, _) => ValueTask.FromResult<JsonNode?>("served"), new UcanRequired(root.NodeId));
        foreach (var (name, (ucan, refused)) in Presentations(caller, root, alice, procedure))
        {
            try
            {
                var result = await UntilServed(() => caller.CallAsync(stations.Realm, procedure, new JsonObject(),
                    new CallOptions { Timeout = Patience, Ucan = ucan }));
                Assert.True(refused is null && result!.GetValue<string>() == "served", name);
            }
            catch (ProviderErrorException e)
            {
                Assert.True(e.Code == refused, $"{name}: {e.Code}");
            }
        }
    }

    [Fact]
    public async Task AGatedStreamJudgesEachOpen()
    {
        var (provider, caller, root, alice) = await WorldAsync("gated stream");
        await using var p = provider;
        await using var c = caller;
        using var r = root;
        using var a = alice;
        var procedure = $"{stations.Org}/watch";
        await using var served = provider.ServeStream(stations.Realm, procedure, StreamMode.Server,
            (stream, _) =>
            {
                stream.Reply("served");
                return Task.CompletedTask;
            }, new UcanRequired(root.NodeId));
        foreach (var (name, (ucan, refused)) in Presentations(caller, root, alice, procedure))
        {
            // The first frame of an open: the provider's reply, or the STREAM_ERROR it refused with. A
            // relay's error (the advertisement not yet reached) is tried again.
            async Task<StreamFrame> Opened()
            {
                await using var stream = await caller.OpenStreamAsync(stations.Realm, procedure, StreamMode.Server,
                    new JsonObject(), timeout: Patience, ucan: ucan);
                using var wait = new CancellationTokenSource(Patience);
                await foreach (var frame in stream.ReadAllAsync(wait.Token))
                {
                    if (frame is StreamFailed { Relay: true } relayed)
                    {
                        throw new RelayErrorException(relayed.Message, relayed.Code);
                    }
                    return frame;
                }
                throw new InvalidOperationException("the stream ended without a frame");
            }
            var first = await UntilServed(Opened);
            if (refused is null)
            {
                Assert.True(first is StreamReply { Payload: var payload } && payload!.GetValue<string>() == "served",
                    $"{name}: {first}");
            }
            else
            {
                Assert.True(first is StreamFailed { Relay: false } failed && failed.Code == refused, $"{name}: {first}");
            }
        }
    }

    [Fact]
    public async Task ARealmMemberProcedureNeedsTheRealmsGrant()
    {
        // The harness's realm key is not this test's to sign with: a key of the test's own stands in for
        // the realm, named by its key id.
        var (provider, caller, realmKey, alice) = await WorldAsync("realm member");
        await using var p = provider;
        await using var c = caller;
        using var r = realmKey;
        using var a = alice;
        var procedure = $"{stations.Org}/member";
        var grant = $"mri:realm:{stations.RealmName}";
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        await using var served = provider.Serve(stations.Realm, procedure,
            (_, _) => ValueTask.FromResult<JsonNode?>("served"),
            new RealmMemberRequired(Ucan.KeyId(realmKey.PublicKey, stations.Profile), "count"));
        var token = realmKey.CreateUcan(caller.NodeId, [new Capability(grant, "count")], expires);
        var result = await UntilServed(() => caller.CallAsync(stations.Realm, procedure, new JsonObject(),
            new CallOptions { Timeout = Patience, Ucan = new UcanPresentation(token) }));
        Assert.Equal("served", result!.GetValue<string>());
        var other = realmKey.CreateUcan(caller.NodeId, [new Capability(grant, "invoke")], expires);
        var refused = await Assert.ThrowsAsync<ProviderErrorException>(() => caller.CallAsync(stations.Realm, procedure,
            new JsonObject(), new CallOptions { Timeout = Patience, Ucan = new UcanPresentation(other) }));
        Assert.Equal("unauthorized", refused.Code);
    }
}

[Collection(StationsCollection.Name)]
public sealed class GatedServingPqPureTests(TestStations stations) : GatedServingTests(stations);

[Collection(HybridStationsCollection.Name)]
public sealed class GatedServingPqHybridTests(HybridTestStations stations) : GatedServingTests(stations);
