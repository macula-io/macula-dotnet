using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Macula.Tests;

/// <summary>
/// macula 13.2's sealed groups (DESIGN_E2E_SEALED_PUBSUB.md, package 5 of macula's seal vectors) through this
/// binding: a .NET app publishes under a realm group and opens its events, pulling the group's epoch keys from
/// the org's distributor over a sealed call, presenting the org's grant.
///
/// The C ABI hands values, never bytes: .NET builds no CALL payload and reads no CBOR, so macula's vectors
/// (fixtures/groups) are reached here as values — the epoch's 8-byte id and 32-byte key as the event proves them
/// (the vector's id, opened under the vector's key), every field of a pull's payloads at the distributor, and the
/// verdict macula's gate gives a grant. What is macula-go's alone is held there and named here: the vectors' CBOR
/// byte for byte (group/vectors_test.go) and the six cases' named refusals (the wire carries only unauthorized,
/// and a refused pull reads to a member as no_distributor).
///
/// The distributor is a stand-in a .NET app serves (macula's own is Elixir): &lt;org&gt;/group_keys_v1, always
/// sealed. The org's grant is real: the test station mints it with the org key, whose key id the distributor's
/// policy names.
/// </summary>
public abstract class GroupTests(TestStations stations)
{
    // macula's test/vectors/e2e_seal_v1_group_keys.json at v15.0.0-rc.2 (42b60269).
    private const string VectorsSha256 = "e2f9f9a35eab8931cae260301f0034fe5a9b6a6b6d5776b480f242b1b2a6d7a2";

    // An epoch's accept_until is its publish_until plus the life of an event (asserted against the vector below).
    private const long EventLifeMs = 65 * 60_000;
    private const int IdSize = 8;
    private const int KeySize = 32;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    // How long an advertisement reaching the DHT is waited for, by a join and by UntilAdvertisedAsync. Generous
    // because the suite runs several station pairs side by side, and a join that is refused for any other reason is
    // not retried at all.
    private static readonly TimeSpan Reach = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static JsonDocument Vectors()
    {
        var data = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "groups", "e2e_seal_v1_group_keys.json"));
        Assert.True(Convert.ToHexStringLower(SHA256.HashData(data)) == VectorsSha256,
            "e2e_seal_v1_group_keys.json drifted from the pinned bytes");
        return JsonDocument.Parse(data);
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [Fact]
    public void TheVectorsAreMaculas()
    {
        using var vectors = Vectors();
        var v = vectors.RootElement;
        Assert.Equal("test/vectors/E2E_SEAL_V1.md", v.GetProperty("spec").GetString());
        Assert.Equal("scripts/generate-seal-group-keys-vectors.sh", v.GetProperty("generator").GetString());
        Assert.Equal("io.macula", v.GetProperty("realm_name").GetString());
        Assert.Equal(["not_a_member", "membership_unknown", "unknown_epoch", "epoch_expired", "unknown_group"],
            v.GetProperty("refusals").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal(["pq_hybrid", "pq_pure"],
            v.GetProperty("profiles").EnumerateObject().Select(p => p.Name).Order().ToArray());
        foreach (var profile in v.GetProperty("profiles").EnumerateObject())
        {
            var cases = profile.Value.GetProperty("cases").EnumerateArray().ToList();
            Assert.Equal(["ok_procedure_grant", "ok_org_grant", "missing_capability", "not_the_issuer",
                "not_the_audience", "expired"], cases.Select(c => c.GetProperty("name").GetString()!).ToArray());
            // Each case's verdict is its name, the two macula admits first.
            Assert.Equal(["ok", "ok", "missing_capability", "not_the_issuer", "not_the_audience", "expired"],
                cases.Select(c => c.GetProperty("verdict").GetString()!).ToArray());
            foreach (var kase in cases)
            {
                Assert.Equal("acme/group_keys_v1", kase.GetProperty("context").GetProperty("procedure").GetString());
                // The gate these cases turn on: the org key's id, and group_keys.
                var policy = kase.GetProperty("policy");
                Assert.Equal("realm_member_required", policy.GetProperty("kind").GetString());
                Assert.Equal("group_keys", policy.GetProperty("can").GetString());
                Assert.Equal(64, policy.GetProperty("key_id").GetString()!.Length);
                Assert.Equal(64, kase.GetProperty("context").GetProperty("realm").GetString()!.Length);
            }
        }
        var result = v.GetProperty("payloads").GetProperty("result");
        var prefix = result.GetProperty("prefix").GetString();
        Assert.Equal("io.macula/acme/chat", prefix);
        Assert.Equal("required", result.GetProperty("policy").GetString());
        Assert.Equal(prefix, v.GetProperty("payloads").GetProperty("call_current").GetProperty("prefix").GetString());
        Assert.Equal(prefix, v.GetProperty("payloads").GetProperty("call_by_id").GetProperty("prefix").GetString());
        Assert.Equal("current", v.GetProperty("payloads").GetProperty("call_current").GetProperty("epoch").GetString());
        var epochs = result.GetProperty("epochs").EnumerateArray().ToList();
        Assert.Equal(epochs[0].GetProperty("id").GetString(),
            v.GetProperty("payloads").GetProperty("call_by_id").GetProperty("epoch_id").GetString());
        foreach (var epoch in epochs)
        {
            Assert.Equal(IdSize, Convert.FromHexString(epoch.GetProperty("id").GetString()!).Length);
            Assert.Equal(KeySize, Convert.FromHexString(epoch.GetProperty("key").GetString()!).Length);
            Assert.Equal(EventLifeMs,
                epoch.GetProperty("accept_until").GetInt64() - epoch.GetProperty("publish_until").GetInt64());
        }
        // The second epoch is issued exactly as the first stops publishing.
        Assert.Equal(epochs[0].GetProperty("publish_until").GetInt64(), epochs[1].GetProperty("issued_at").GetInt64());
    }

    /// <summary>One of a distributor's epochs as this binding's handler answers it: the vector's id and key as
    /// bytes (the ABI's path from JSON to CBOR), the times a distributor sets.</summary>
    private sealed record Epoch(byte[] Id, byte[] Key, long IssuedAt, long PublishUntil, long AcceptUntil)
    {
        public JsonObject Wire() => new()
        {
            ["id"] = Payload.Bytes(Id),
            ["key"] = Payload.Bytes(Key),
            ["issued_at"] = IssuedAt,
            ["publish_until"] = PublishUntil,
            ["accept_until"] = AcceptUntil,
        };
    }

    /// <summary>The vector's epoch <paramref name="index"/>, live: its id, key and durations are macula's, only
    /// its times move. The vector's own window closed in September 2026, so a test issues the epoch when it needs
    /// it — the first as the group's current one, the second as the rotation after it.</summary>
    private static Epoch EpochAt(JsonDocument vectors, int index, long issuedAt)
    {
        var e = vectors.RootElement.GetProperty("payloads").GetProperty("result").GetProperty("epochs")[index];
        var rotation = e.GetProperty("publish_until").GetInt64() - e.GetProperty("issued_at").GetInt64();
        var life = e.GetProperty("accept_until").GetInt64() - e.GetProperty("publish_until").GetInt64();
        return new Epoch(Convert.FromHexString(e.GetProperty("id").GetString()!),
            Convert.FromHexString(e.GetProperty("key").GetString()!), issuedAt, issuedAt + rotation,
            issuedAt + rotation + life);
    }

    /// <summary>
    /// The org's distributor as a .NET app serves it: <c>&lt;org&gt;/group_keys_v1</c> sealed
    /// (<c>required</c>: a member's pull is always sealed to the KEM key the advertisement names), under
    /// <paramref name="policy"/>. What it hands out is <see cref="Serves"/> — a pull of <c>current</c> gets all
    /// of it, a pull by id the epoch that id names, and an id it does not have is refused <c>unknown_epoch</c>.
    /// <see cref="Refuse"/> names a refusal from macula's closed set, which travels as the pull's handler_error
    /// detail and is all a member reads of it. <see cref="Transform"/> is a last look at a reply before it goes
    /// out, for tests of replies that are not the protocol's. <see cref="Seen"/> has every pull's payload — a
    /// member's CALL, field for field — and <see cref="Sealed"/> whether each came sealed.
    /// </summary>
    private sealed class Distributor(string prefix, AuthPolicy policy, IEnumerable<Epoch> serves)
    {
        private readonly List<Epoch> _serves = [.. serves];

        public string Prefix { get; } = prefix;
        public AuthPolicy Policy { get; } = policy;
        public List<JsonNode?> Seen { get; } = [];
        public List<bool> Sealed { get; } = [];
        public string? Refuse { get; set; }
        public Func<JsonObject, JsonObject> Transform { get; set; } = reply => reply;

        public IReadOnlyList<Epoch> Serves
        {
            get => _serves;
            set
            {
                _serves.Clear();
                _serves.AddRange(value);
            }
        }

        /// <summary>The epoch a publisher seals under now: the newest handed out that has started and not
        /// stopped publishing (macula-go's forPublish).</summary>
        public Epoch Publishing => _serves.Last(e => e.IssuedAt <= NowMs() && NowMs() < e.PublishUntil);

        public ValueTask<JsonNode?> Answer(Request request, CancellationToken _)
        {
            Seen.Add(request.Payload?.DeepClone());
            Sealed.Add(request.Sealed);
            if (Refuse is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }
            var asked = request.Payload?["epoch"];
            var epochs = asked is JsonValue value && value.TryGetValue<string>(out var current) && current == "current"
                ? _serves
                : _serves.Where(e => Payload.TryGetBytes(asked, out var id) && id.SequenceEqual(e.Id)).ToList();
            if (epochs.Count == 0)
            {
                throw new InvalidOperationException("unknown_epoch");
            }
            return ValueTask.FromResult<JsonNode?>(Transform(new JsonObject
            {
                ["prefix"] = Prefix,
                ["policy"] = "required",
                ["epochs"] = new JsonArray([.. epochs.Select(e => (JsonNode)e.Wire())]),
            }));
        }

        /// <summary>The distributor's procedure, served sealed under its policy. The policy is what makes a pull
        /// a pull — a distributor with none serves the keys to anyone.</summary>
        public Served Serve(Pool pool, TestStations s) =>
            pool.Serve(s.Realm, $"{s.Org}/group_keys_v1", Answer, Policy, ServedConfidential.Required);
    }

    /// <summary>A group under this harness's own org: a group's second segment is its org, whose distributor
    /// serves the group_keys_v1 procedure.</summary>
    private static string GroupPrefix(TestStations s, string name) => $"{s.RealmName}/{s.Org}/{name}";

    private async Task<Pool> NodeAsync(string name, int station = 0, bool admitted = false, bool kemAdvertise = false)
    {
        using var key = await NodeKey.LoadOrCreateAsync(Path.Combine(stations.KeyDirectory, name + ".key"),
            stations.Profile);
        if (admitted)
        {
            await stations.AdmitAsync(key.NodeId);
        }
        return await Pool.ConnectAsync(key, [stations.Seeds[station]], new PoolOptions
        {
            RealmTrust = new Dictionary<MeshId, byte[]> { [stations.Realm] = stations.RealmKey },
            RespawnDelay = TimeSpan.FromMilliseconds(100),
            ConnectTimeout = TimeSpan.FromSeconds(20),
            KemAdvertise = kemAdvertise,
        });
    }

    /// <summary>A member's group: the prefix, and the org's grant for that node, minted by the org key the
    /// distributor's policy names.</summary>
    private static Group MemberGroup(Distributor distributor, string grant) => new(distributor.Prefix, grant);

    /// <summary>The first event a subscription hears within <paramref name="timeout"/>; a test fails here rather
    /// than downstream when nothing arrives.</summary>
    private static async Task<MeshEvent> NextAsync(Subscription subscription, TimeSpan timeout)
    {
        using var reading = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var e in subscription.ReadAllAsync(reading.Token))
            {
                return e;
            }
        }
        catch (OperationCanceledException)
        {
        }
        throw new InvalidOperationException($"no event arrived within {timeout}");
    }

    /// <summary>
    /// The distributor's advertisement in the DHT, which every join needs and which arrives in its own time: a
    /// pull made before it lands is answered <c>unknown_next_peer</c> and reads to a member as
    /// <c>no_distributor</c>. Waiting for it here is what separates "not advertised yet" from "the group refused
    /// me", so a refused join is the group's verdict rather than the DHT's timing.
    /// </summary>
    private async Task UntilAdvertisedAsync(Pool pool, Pool standin)
    {
        var deadline = DateTime.UtcNow + Reach;
        var procedure = $"{stations.Org}/group_keys_v1";
        while (true)
        {
            try
            {
                var providers = await pool.ProvidersAsync(stations.Realm, procedure, Wait);
                if (providers.Any(p => p.Node == standin.NodeId))
                {
                    return;
                }
            }
            // Any failure is a reason to ask again, not a verdict: the org directory this lookup needs is a DHT
            // record, and a lookup that reached no verified reply in its own time is the same "not yet" as a
            // record that is not there.
            catch (Exception)
            {
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException($"the distributor is not advertised within {Reach}");
            }
            await Task.Delay(200);
        }
    }

    /// <summary><paramref name="attempt"/> until the distributor, not the DHT's reach, answers: an advertisement
    /// reaches the other station's DHT in its own time. A pull a distributor refused for a reason that is not one
    /// of its named refusals reads as no_distributor, so only no_distributor is retried.</summary>
    private static async Task<Subscription> UntilJoinedAsync(Func<Task<Subscription>> attempt)
    {
        var deadline = DateTime.UtcNow + Reach;
        while (true)
        {
            try
            {
                return await attempt();
            }
            catch (GroupException e) when (e.Reason == "no_distributor" && DateTime.UtcNow < deadline)
            {
            }
            await Task.Delay(200);
        }
    }

    /// <summary><paramref name="attempt"/> until the provider, not the DHT's reach, answers. A verdict from the
    /// provider is the answer, not something to wait out: only a call the station could not place yet is retried,
    /// which is what keeps a refusal from being asked for over and over until the station's caller quota fills
    /// and answers caller_quota instead.</summary>
    private static async Task<JsonNode?> UntilServedAsync(Func<Task<JsonNode?>> attempt)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            try
            {
                return await attempt();
            }
            catch (ProviderErrorException e)
            {
                if (e.Code != "unknown_next_peer" || DateTime.UtcNow > deadline)
                {
                    throw;
                }
            }
            catch (Exception)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw;
                }
            }
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task AMemberPublishesUnderTheVectorsEpochAndOpensTheEvent()
    {
        using var vectors = Vectors();
        var prefix = GroupPrefix(stations, "opened");
        var topic = $"{prefix}/said_v1";
        await using var standin = await NodeAsync("group opened distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, new RealmMemberRequired(MeshId.Parse(stations.OrgKeyId), "group_keys"),
            [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var publisher = await NodeAsync("group opened publisher");
        await using var reader = await NodeAsync("group opened reader");
        await UntilAdvertisedAsync(reader, standin);
        var readerGrant = await stations.GrantAsync(reader.NodeId);
        var subscription = await UntilJoinedAsync(() =>
            reader.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, readerGrant)));
        await using (subscription)
        {
            await publisher.PublishGroupAsync(stations.Realm, topic, new JsonObject { ["said"] = "hush" },
                MemberGroup(distributor, await stations.GrantAsync(publisher.NodeId)));
            var e = await NextAsync(subscription, Wait);
            Assert.Equal(topic, e.Topic);
            Assert.Null(e.Unopened);
            Assert.True(e.Sealed);
            // The vector's own id, and the event opened under the key that id names: the epoch's 32 bytes crossed
            // the ABI as the vector has them, or the GCM tag would not verify.
            Assert.Equal(distributor.Publishing.Id, Convert.FromHexString(e.SealKeyId!));
            Assert.Equal(vectors.RootElement.GetProperty("payloads").GetProperty("call_by_id")
                .GetProperty("epoch_id").GetString(), e.SealKeyId);
            Assert.Equal("hush", e.Payload!["said"]!.GetValue<string>());
        }
        // A member's pull is always sealed, and made in the vector's own shape: one per node that joined the
        // group, the reader's and the publisher's — a node holding a group does not pull it again.
        Assert.NotEmpty(distributor.Sealed);
        Assert.All(distributor.Sealed, Assert.True);
        Assert.Equal(2, distributor.Seen.Count);
        Assert.All(distributor.Seen, seen => Assert.Equal("current", seen!["epoch"]!.GetValue<string>()));
        Assert.All(distributor.Seen, seen => Assert.Equal(prefix, seen!["prefix"]!.GetValue<string>()));
    }

    [Fact]
    public async Task APlainSubscriptionHearsTheEventUnopened()
    {
        using var vectors = Vectors();
        var prefix = GroupPrefix(stations, "plain");
        var topic = $"{prefix}/said_v1";
        await using var standin = await NodeAsync("group plain distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, new RealmMemberRequired(MeshId.Parse(stations.OrgKeyId), "group_keys"),
            [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var publisher = await NodeAsync("group plain publisher");
        await using var member = await NodeAsync("group plain member");
        await using var outsider = await NodeAsync("group plain outsider");
        await UntilAdvertisedAsync(member, standin);
        var memberGrant = await stations.GrantAsync(member.NodeId);
        var subscription = await UntilJoinedAsync(() =>
            member.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, memberGrant)));
        await using (subscription)
        await using (var plain = outsider.Subscribe(stations.Realm, topic))
        {
            await publisher.PublishGroupAsync(stations.Realm, topic, new JsonObject { ["said"] = "hush" },
                MemberGroup(distributor, await stations.GrantAsync(publisher.NodeId)));
            var opened = await NextAsync(subscription, Wait);
            var heard = await NextAsync(plain, Wait);
            // A node that holds no key for the group hears the same event once, with nothing of its payload:
            // no_group.
            Assert.Equal(topic, heard.Topic);
            Assert.Null(heard.Payload);
            Assert.Equal("no_group", heard.Unopened);
            Assert.Equal("hush", opened.Payload!["said"]!.GetValue<string>());
            // And a node holding the group may not publish clear under it.
            var refused = Assert.Throws<GroupException>(() =>
                publisher.Publish(stations.Realm, topic, new JsonObject { ["said"] = "clear" }));
            Assert.Equal("group_held", refused.Reason);
            Assert.Equal(prefix, refused.Prefix);
        }
    }

    [Fact]
    public async Task AnEventUnderANewEpochIsPulledByIdAndOpened()
    {
        // The reader joins under one epoch and the distributor rotates to the next, which the reader never
        // receives: the event arrives sealed under a key it does not hold, and the member's CALL names the id the
        // event named (the vector's call_by_id payload, field for field).
        using var vectors = Vectors();
        var prefix = GroupPrefix(stations, "rotated");
        var topic = $"{prefix}/said_v1";
        var started = NowMs();
        var first = EpochAt(vectors, 0, started - 1_000);
        var second = EpochAt(vectors, 1, started);
        await using var standin = await NodeAsync("group rotated distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, new RealmMemberRequired(MeshId.Parse(stations.OrgKeyId), "group_keys"),
            [first]);
        await using var serving = distributor.Serve(standin, stations);
        await using var reader = await NodeAsync("group rotated reader");
        await using var publisher = await NodeAsync("group rotated publisher");
        await UntilAdvertisedAsync(reader, standin);
        var readerGrant = await stations.GrantAsync(reader.NodeId);
        var subscription = await UntilJoinedAsync(() =>
            reader.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, readerGrant)));
        await using (subscription)
        {
            distributor.Serves = [first, second];
            await publisher.PublishGroupAsync(stations.Realm, topic, new JsonObject { ["said"] = "again" },
                MemberGroup(distributor, await stations.GrantAsync(publisher.NodeId)));
            var e = await NextAsync(subscription, Wait);
            Assert.Null(e.Unopened);
            Assert.Equal(second.Id, Convert.FromHexString(e.SealKeyId!));
            Assert.Equal("again", e.Payload!["said"]!.GetValue<string>());
            // The first pull is the reader's join; a later one names the id the rotated event carried, as bytes.
            Assert.Equal("current", distributor.Seen[0]!["epoch"]!.GetValue<string>());
            Assert.Contains(distributor.Seen,
                seen => Payload.TryGetBytes(seen?["epoch"], out var id) && id.SequenceEqual(second.Id));
            Assert.All(distributor.Sealed, Assert.True);
        }
    }

    [Fact]
    public async Task TheStationsOrgGrantIsAccepted()
    {
        // The org's own grant, minted by the test station's org key over mri:org:<realm>/<org>, presented to a
        // distributor whose policy names that key's id: the join pulls an epoch and the group's events open.
        using var vectors = Vectors();
        var prefix = GroupPrefix(stations, "granted");
        var topic = $"{prefix}/said_v1";
        await using var standin = await NodeAsync("group granted distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, new RealmMemberRequired(MeshId.Parse(stations.OrgKeyId), "group_keys"),
            [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var publisher = await NodeAsync("group granted publisher");
        await using var reader = await NodeAsync("group granted reader");
        await UntilAdvertisedAsync(reader, standin);
        var group = MemberGroup(distributor, await stations.GrantAsync(reader.NodeId));
        Assert.NotNull(group.Ucan);
        var subscription = await UntilJoinedAsync(() => reader.SubscribeGroupAsync(stations.Realm, topic, group));
        await using (subscription)
        {
            await publisher.PublishGroupAsync(stations.Realm, topic, new JsonObject { ["said"] = "granted" },
                MemberGroup(distributor, await stations.GrantAsync(publisher.NodeId)));
            var e = await NextAsync(subscription, Wait);
            Assert.Equal("granted", e.Payload!["said"]!.GetValue<string>());
        }
    }

    /// <summary>
    /// The vector's six cases as live tokens for <paramref name="member"/>, by their names: the two macula admits
    /// (its two <c>with</c> forms, one capability) and the four it refuses, each for its own reason. Every refusal
    /// reaches this binding as unauthorized — the wire carries no more of a gated verdict than that — which is why
    /// the cases are named after macula's (its group/vectors_test.go).
    /// </summary>
    private Dictionary<string, (string Token, string? Refused)> Grants(Pool member, NodeKey root, NodeKey impostor)
    {
        var exp = DateTimeOffset.UtcNow.AddSeconds(300);
        var org = $"mri:org:{stations.RealmName}/{stations.Org}";
        var procedure = $"mri:proc:{stations.RealmName}/{stations.Org}/group_keys_v1";
        Capability[] groupKeys = [new(org, "group_keys")];
        return new Dictionary<string, (string, string?)>
        {
            ["ok_procedure_grant"] = (root.CreateUcan(member.NodeId, [new(procedure, "group_keys")], exp), null),
            ["ok_org_grant"] = (root.CreateUcan(member.NodeId, groupKeys, exp), null),
            ["missing_capability"] = (root.CreateUcan(member.NodeId, [new(org, "invoke")], exp), "unauthorized"),
            ["not_the_issuer"] = (impostor.CreateUcan(member.NodeId, groupKeys, exp), "unauthorized"),
            ["not_the_audience"] = (root.CreateUcan(impostor.NodeId, groupKeys, exp), "unauthorized"),
            ["expired"] = (root.CreateUcan(member.NodeId, groupKeys, DateTimeOffset.UtcNow.AddSeconds(-60)),
                "unauthorized"),
        };
    }

    [Fact]
    public async Task EachOfTheVectorsGrantCasesReachesItsVerdict()
    {
        using var vectors = Vectors();
        using var root = await NodeKey.GenerateAsync(stations.Profile);
        using var impostor = await NodeKey.GenerateAsync(stations.Profile);
        var prefix = GroupPrefix(stations, "judged");
        // The harness's org key is not this test's to sign with: a key of the test's own stands in for the org,
        // named by its key id.
        var policy = new RealmMemberRequired(Ucan.KeyId(root.PublicKey, stations.Profile), "group_keys");
        await using var standin = await NodeAsync("group judged distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, policy, [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var member = await NodeAsync("group judged member");
        await UntilAdvertisedAsync(member, standin);
        var published = distributor.Publishing;
        foreach (var (name, (token, refused)) in Grants(member, root, impostor))
        {
            // The pull's own CALL, the vector's call_current payload, so a verdict here is the verdict the join
            // would get.
            var options = new CallOptions
            {
                Timeout = Patience,
                Ucan = new UcanPresentation(token),
                Confidential = Confidential.Required,
            };
            JsonNode? reply;
            try
            {
                reply = await UntilServedAsync(() => member.CallAsync(stations.Realm, $"{stations.Org}/group_keys_v1",
                    new JsonObject { ["prefix"] = prefix, ["epoch"] = "current" }, options));
            }
            catch (ProviderErrorException e)
            {
                Assert.Equal(refused, e.Code);
                continue;
            }
            Assert.Null(refused);
            // The epoch as it crossed the ABI, the vector's bytes: an 8-byte id, the 32-byte key under it.
            var epoch = reply!["epochs"]![0]!;
            Assert.Equal(prefix, reply["prefix"]!.GetValue<string>());
            Assert.Equal("required", reply["policy"]!.GetValue<string>());
            Assert.True(Payload.TryGetBytes(epoch["id"], out var id));
            Assert.True(Payload.TryGetBytes(epoch["key"], out var key));
            Assert.Equal(published.Id, id);
            Assert.Equal(published.Key, key);
            Assert.Equal(IdSize, id.Length);
            Assert.Equal(KeySize, key.Length);
        }
        Assert.All(distributor.Sealed, Assert.True);
    }

    [Fact]
    public async Task AGrantMaculaRefusesDoesNotJoinAtPublishOrSubscribe()
    {
        // The six cases above are pulled through CallAsync, which stands in for the join. This is the same verdict
        // at the API an app calls: after the distributor has answered with a grant of its own (so what refuses
        // next is the token, not the DHT's reach), an expired one leaves the group unjoined, and both
        // PublishGroupAsync and SubscribeGroupAsync raise rather than publish clear or hand back an inbox nothing
        // opens. What the reason reads as is macula-go's (a gated refusal reaches it as the same no_distributor a
        // missing distributor gives), so this holds the raise, not the reason.
        using var vectors = Vectors();
        using var root = await NodeKey.GenerateAsync(stations.Profile);
        var prefix = GroupPrefix(stations, "refused_grant");
        var topic = $"{prefix}/said_v1";
        // A key of the test's own stands in for the org: the harness's org key is not this test's to sign with,
        // which is what leaves it free to mint a token that has already expired.
        var policy = new RealmMemberRequired(Ucan.KeyId(root.PublicKey, stations.Profile), "group_keys");
        await using var standin = await NodeAsync("group refused distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, policy, [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var member = await NodeAsync("group refused member");
        await UntilAdvertisedAsync(member, standin);
        Capability[] groupKeys = [new($"mri:org:{stations.RealmName}/{stations.Org}", "group_keys")];
        var admitted = root.CreateUcan(member.NodeId, groupKeys, DateTimeOffset.UtcNow.AddSeconds(300));
        await UntilServedAsync(() => member.CallAsync(stations.Realm, $"{stations.Org}/group_keys_v1",
            new JsonObject { ["prefix"] = prefix, ["epoch"] = "current" },
            new CallOptions { Timeout = Patience, Ucan = new UcanPresentation(admitted), Confidential = Confidential.Required }));
        var expired = new Group(prefix,
            root.CreateUcan(member.NodeId, groupKeys, DateTimeOffset.UtcNow.AddSeconds(-60)));
        var published = await Assert.ThrowsAsync<GroupException>(() =>
            member.PublishGroupAsync(stations.Realm, topic, new JsonObject { ["said"] = "clear" }, expired,
                timeout: TimeSpan.FromSeconds(5)));
        // The gate's own code, which a dial failure or a dead distributor does not carry: the same reason covers
        // all three, so the message is what says the token was judged.
        Assert.Contains("unauthorized", published.Message);
        var subscribed = await Assert.ThrowsAsync<GroupException>(() =>
            member.SubscribeGroupAsync(stations.Realm, topic, expired, TimeSpan.FromSeconds(5)));
        Assert.Contains("unauthorized", subscribed.Message);
        // The distributor was alive at the verdict: the refused node's pull never reached its handler, and a
        // member with a grant of its own joins the same group right after.
        Assert.Single(distributor.Seen);
        await using var late = await NodeAsync("group refused late member");
        var granted = new Group(prefix,
            root.CreateUcan(late.NodeId, groupKeys, DateTimeOffset.UtcNow.AddSeconds(300)));
        var subscription = await UntilJoinedAsync(() => late.SubscribeGroupAsync(stations.Realm, topic, granted));
        await subscription.DisposeAsync();
    }

    [Fact]
    public async Task ANodeTheDistributorRemovedIsRefused()
    {
        // A distributor may refuse a pull for a node it does not hold as a member (macula's not_a_member), and a
        // member reads it by that name: the refusal travels as the pull's handler_error detail.
        using var vectors = Vectors();
        var prefix = GroupPrefix(stations, "removed");
        var topic = $"{prefix}/said_v1";
        await using var standin = await NodeAsync("group removed distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, new RealmMemberRequired(MeshId.Parse(stations.OrgKeyId), "group_keys"),
            [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var member = await NodeAsync("group removed member");
        await UntilAdvertisedAsync(member, standin);
        var memberGrant = await stations.GrantAsync(member.NodeId);
        var subscription = await UntilJoinedAsync(() =>
            member.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, memberGrant)));
        await subscription.DisposeAsync();
        // The same distributor, finding the next node no member at all.
        distributor.Refuse = "not_a_member";
        await using var removed = await NodeAsync("group removed stranger");
        var removedGrant = await stations.GrantAsync(removed.NodeId);
        var refused = await Assert.ThrowsAsync<GroupException>(() =>
            removed.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, removedGrant)));
        Assert.Equal("not_a_member", refused.Reason);
        Assert.Equal("current", distributor.Seen[^1]!["epoch"]!.GetValue<string>());
    }

    /// <summary>Replies that are not group_keys_v1's, each refused by a member that holds nothing of the group
    /// (macula-go's ErrMalformedReply): the asked prefix not echoed, no policy of the three, no epoch at all, and
    /// an epoch whose accept_until is not an event's life past its publish_until.</summary>
    private static Dictionary<string, Func<JsonObject, JsonObject>> Malformed(Epoch epoch) => new()
    {
        ["another prefix"] = reply =>
        {
            reply["prefix"] = "io.macula/somewhere_else/chat";
            return reply;
        },
        ["no policy of the three"] = reply =>
        {
            reply["policy"] = "maybe";
            return reply;
        },
        ["no epoch"] = reply =>
        {
            reply["epochs"] = new JsonArray();
            return reply;
        },
        ["an epoch that is not the protocol's"] = reply =>
        {
            ((JsonObject)reply["epochs"]![0]!)["accept_until"] = epoch.AcceptUntil + 1;
            return reply;
        },
    };

    [Fact]
    public async Task AReplyThatIsNotTheProtocolsIsRefused()
    {
        using var vectors = Vectors();
        var prefix = GroupPrefix(stations, "malformed");
        var topic = $"{prefix}/said_v1";
        await using var standin = await NodeAsync("group malformed distributor", admitted: true, kemAdvertise: true);
        var distributor = new Distributor(prefix, new RealmMemberRequired(MeshId.Parse(stations.OrgKeyId), "group_keys"),
            [EpochAt(vectors, 0, NowMs() - 1_000)]);
        await using var serving = distributor.Serve(standin, stations);
        await using var member = await NodeAsync("group malformed member");
        await UntilAdvertisedAsync(member, standin);
        // Reachable, answering the protocol's own reply.
        var grant = await stations.GrantAsync(member.NodeId);
        await UntilServedAsync(() => member.CallAsync(stations.Realm, $"{stations.Org}/group_keys_v1",
            new JsonObject { ["prefix"] = prefix, ["epoch"] = "current" },
            new CallOptions { Timeout = Patience, Ucan = new UcanPresentation(grant), Confidential = Confidential.Required }));
        foreach (var (name, transform) in Malformed(distributor.Serves[0]))
        {
            distributor.Transform = transform;
            var seen = distributor.Seen.Count;
            var refused = await Assert.ThrowsAsync<GroupException>(() =>
                member.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, grant)));
            Assert.Equal("no_distributor", refused.Reason);
            // And the refusal is the member rejecting a reply the handler really sent: no_distributor is also
            // what an outage or an advertisement that has not propagated gives, so without this the case would
            // pass on a run where no malformed reply was ever parsed.
            Assert.Equal(seen + 1, distributor.Seen.Count);
        }
        // The control: the same procedure, the same member, only the reply back to the protocol's — and the join
        // is held.
        distributor.Transform = reply => reply;
        var subscription = await UntilJoinedAsync(() =>
            member.SubscribeGroupAsync(stations.Realm, topic, MemberGroup(distributor, grant)));
        await using (subscription)
        {
            await member.PublishGroupAsync(stations.Realm, topic, new JsonObject { ["said"] = "held" },
                MemberGroup(distributor, grant));
        }
    }

    [Fact]
    public async Task TheGroupAndItsOptionsAreChecked()
    {
        // What this binding refuses before the library sees it (a group is a Macula.Group, a topic names one), and
        // what it hands the library to refuse: a prefix with no org segment is macula's own invalid_argument.
        var prefix = GroupPrefix(stations, "checked");
        await using var pool = await NodeAsync("group checked");
        Assert.Throws<ArgumentException>(() => new Group(""));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            pool.SubscribeGroupAsync(stations.Realm, "chat/said_v1", new Group("chat")));
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            pool.SubscribeGroupAsync(stations.Realm, $"{prefix}/said_v1", null!));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            pool.PublishGroupAsync(stations.Realm, "somewhere_else/said_v1", new JsonObject(), new Group(prefix),
                timeout: TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void AGroupDoesNotPrintItsGrant()
    {
        // The org's grant is a bearer credential: what a group prints is where one leaks.
        var group = new Group("io.macula/acme/chat", "a.bearer.grant.token");
        Assert.Contains("io.macula/acme/chat", group.ToString());
        Assert.Contains("(set)", group.ToString());
        Assert.DoesNotContain("a.bearer.grant.token", group.ToString());
        Assert.Contains("(none)", new Group("io.macula/acme/chat").ToString());
    }
}

[Collection(GroupStationsCollection.Name)]
public sealed class GroupPqPureTests(GroupTestStations stations) : GroupTests(stations);

[Collection(GroupHybridStationsCollection.Name)]
public sealed class GroupPqHybridTests(GroupHybridTestStations stations) : GroupTests(stations);
