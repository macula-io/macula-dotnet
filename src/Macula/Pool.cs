using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Macula.Native;

namespace Macula;

/// <summary>A seed station: where to dial it, and the node id it must prove.</summary>
public sealed record Seed(string Host, ushort Port, MeshId NodeId);

/// <summary>How a pool connects. Every field left at its default takes macula's.</summary>
public sealed class PoolOptions
{
    /// <summary>
    /// The realm key, as carried, each realm is trusted under: calls are made only to providers its org
    /// directory authorizes, and procedures under an org are served only in a realm named here. A
    /// node's own namespace needs no realm key.
    /// </summary>
    public IDictionary<MeshId, byte[]> RealmTrust { get; init; } = new Dictionary<MeshId, byte[]>();

    /// <summary>How many station links the pool keeps up.</summary>
    public int ReplicationFactor { get; init; }

    /// <summary>The most seeds the pool accepts.</summary>
    public int MaxSeeds { get; init; }

    /// <summary>The most direct links to serving stations the pool holds at once.</summary>
    public int MaxDirectLinks { get; init; }

    /// <summary>How long a lost link waits before it is dialled again.</summary>
    public TimeSpan RespawnDelay { get; init; }

    /// <summary>How long connecting may take before it fails: 30 s when zero.</summary>
    public TimeSpan ConnectTimeout { get; init; }

    /// <summary>
    /// Gives the node a KEM keyring, in memory only, and names its current key in the advertisements of the procedures
    /// it serves confidentially, so callers seal to it (macula 13's E2E seal scheme 1). Off by default. Enable it only
    /// once every station runs macula 12.11 or later and every caller can seal (macula 13, macula-go 0.18, Macula .NET
    /// 0.7 or later).
    /// </summary>
    public bool KemAdvertise { get; init; }
}

/// <summary>One of a pool's station links.</summary>
public sealed record LinkStatus(MeshId Station, string Host, ushort Port, bool Direct, bool Up);

/// <summary>Something that happened to a pool on its own.</summary>
public abstract record PoolEvent;

/// <summary>A link came up, or ended or failed to dial (<see cref="Error"/>).</summary>
public sealed record LinkChanged(MeshId Station, bool Direct, bool Up, string? Error) : PoolEvent;

/// <summary>The node's status statements could not be reissued; left failing, its links end.</summary>
public sealed record IssuerFailed(string Error) : PoolEvent;

/// <summary>A provider of a procedure, and the station it is served through.</summary>
public sealed record Provider(MeshId Node, MeshId Station);

/// <summary>
/// A sealed group: a topic prefix whose second segment is its org
/// (macula's DESIGN_E2E_SEALED_PUBSUB.md). Its members pull the group's epoch keys from the org's
/// distributor over a sealed call, presenting the org's grant <see cref="Ucan"/> (a UCAN with <c>can</c>
/// <c>group_keys</c>, minted by the org). <see cref="Distributor"/> names the node that must be the
/// group's, required for a <c>~&lt;node_id&gt;</c> group.
/// </summary>
public sealed record Group
{
    /// <summary>A group whose members present <paramref name="ucan"/>, under <paramref name="prefix"/>.</summary>
    public Group(string prefix, string? ucan = null, MeshId? distributor = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        Prefix = prefix;
        Ucan = ucan;
        Distributor = distributor;
    }

    /// <summary>The group's topic prefix.</summary>
    public string Prefix { get; }

    /// <summary>
    /// The org's grant for the node that presents it. Kept out of what a group prints: the grant is a bearer
    /// credential, and a log line or a traceback is where one leaks.
    /// </summary>
    public string? Ucan { get; }

    /// <summary>The group's distributor, when one is pinned; any the realm trusts when null.</summary>
    public MeshId? Distributor { get; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Prefix = ").Append(Prefix);
        builder.Append(", Ucan = ").Append(Ucan is null ? "(none)" : "(set)");
        builder.Append(", Distributor = ").Append(Distributor?.ToString() ?? "(any)");
        return true;
    }
}

/// <summary>How a call is made.</summary>
public sealed class CallOptions
{
    /// <summary>The provider to call; any the realm trusts when null.</summary>
    public MeshId? Provider { get; init; }

    /// <summary>The call's deadline on the wire, and how long it is waited for: macula's default when null.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// A UCAN and its chain's proofs, for a gated procedure (<see cref="Macula.Ucan"/>); a provider that
    /// refuses it answers a <see cref="ProviderErrorException"/> of code <c>unauthorized</c>.
    /// </summary>
    public UcanPresentation? Ucan { get; init; }

    /// <summary>
    /// Whether the call is sealed to the provider's KEM key: <see cref="Macula.Confidential.Preferred"/> (the library's
    /// default, when null) whenever its advertisement names one, <see cref="Macula.Confidential.Required"/> never calls a
    /// provider that names none. A call that could not be kept confidential throws a
    /// <see cref="ConfidentialityException"/>.
    /// </summary>
    public Confidential? Confidential { get; init; }
}

/// <summary>A DHT record type.</summary>
public enum RecordType
{
    /// <summary>A node record.</summary>
    NodeRecord = 0x01,
    /// <summary>A realm directory.</summary>
    RealmDirectory = 0x03,
    /// <summary>A realm's stations.</summary>
    RealmStations = 0x04,
    /// <summary>A procedure advertisement.</summary>
    ProcedureAdvertisement = 0x06,
    /// <summary>A tombstone.</summary>
    Tombstone = 0x0C,
    /// <summary>A content announcement.</summary>
    ContentAnnouncement = 0x11,
    /// <summary>A station's endpoint.</summary>
    StationEndpoint = 0x12,
    /// <summary>An org directory.</summary>
    OrgDirectory = 0x15,
    /// <summary>A procedure delegation.</summary>
    ProcedureDelegation = 0x16,
}

/// <summary>A verified DHT record.</summary>
public sealed record DhtRecord(int Type, string KeyId, long CreatedAt, long ExpiresAt, JsonNode? Payload, byte[] Wire);

/// <summary>Verified DHT records, and how many found did not verify.</summary>
public sealed record DhtRecords(IReadOnlyList<DhtRecord> Records, int Dropped);

/// <summary>
/// A node's pool of links to macula 15 stations: calls and streams by direct dial, serving, publish and
/// subscribe, node-served content and the DHT. Dispose it to close every link, subscription, served
/// procedure and stream it holds.
/// </summary>
public sealed partial class Pool : IAsyncDisposable
{
    private readonly PoolHandle _handle;

    private Pool(PoolHandle handle) => _handle = handle;

    /// <summary>Connects <paramref name="key"/>'s node to <paramref name="seeds"/>, returning once one link is up.</summary>
    public static async Task<Pool> ConnectAsync(NodeKey key, IEnumerable<Seed> seeds, PoolOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(seeds);
        var seedsJson = JsonSerializer.Serialize(seeds.Select(s => new Dictionary<string, object>
        {
            ["host"] = s.Host, ["port"] = s.Port, ["node_id"] = s.NodeId.ToString(),
        }));
        var optionsJson = OptionsJson(options ?? new PoolOptions());
        var handle = await NativeCall.RunAsync(token =>
        {
            nint err = 0;
            var h = Libmacula.macula_pool_connect(key.Handle, seedsJson, optionsJson, token, ref err);
            NativeCall.Check(err, cancellationToken);
            return h;
        }, cancellationToken).ConfigureAwait(false);
        return new Pool(handle);
    }

    private static string OptionsJson(PoolOptions o)
    {
        var json = new JsonObject
        {
            ["realm_trust"] = new JsonObject(o.RealmTrust.Select(kv =>
                KeyValuePair.Create(kv.Key.ToString(), (JsonNode?)Convert.ToHexStringLower(kv.Value)))),
        };
        if (o.ReplicationFactor != 0) json["replication_factor"] = o.ReplicationFactor;
        if (o.MaxSeeds != 0) json["max_seeds"] = o.MaxSeeds;
        if (o.MaxDirectLinks != 0) json["max_direct_links"] = o.MaxDirectLinks;
        if (o.RespawnDelay != TimeSpan.Zero) json["respawn_delay_ms"] = (long)o.RespawnDelay.TotalMilliseconds;
        if (o.ConnectTimeout != TimeSpan.Zero) json["timeout_ms"] = (long)o.ConnectTimeout.TotalMilliseconds;
        json["kem_advertise"] = o.KemAdvertise ? 1 : 0;
        return json.ToJsonString();
    }

    // A group's options as macula_pool_publish_group and
    // macula_pool_subscribe_group take them: the prefix, the org's grant when
    // there is one, and the distributor a ~<node_id> group requires, as hex.
    private static string GroupOptionsJson(Group group)
    {
        var json = new JsonObject { ["prefix"] = group.Prefix };
        if (group.Ucan is { } ucan)
        {
            json["ucan"] = ucan;
        }
        if (group.Distributor is { } distributor)
        {
            json["distributor"] = distributor.ToString();
        }
        return json.ToJsonString();
    }

    /// <summary>The pool's node id.</summary>
    public unsafe MeshId NodeId
    {
        get
        {
            Span<byte> id = stackalloc byte[32];
            nint err = 0;
            fixed (byte* p = id)
            {
                Libmacula.macula_pool_node_id(_handle, p, ref err);
            }
            NativeCall.Check(err);
            return new MeshId(id);
        }
    }

    /// <summary>
    /// <paramref name="name"/> in this node's own namespace, <c>~&lt;node id&gt;/&lt;name&gt;</c>: a
    /// procedure only this node can serve, with no org or realm to vouch for it.
    /// </summary>
    public string OwnProcedure(string name) => $"~{NodeId}/{name}";

    /// <summary>The pool's links.</summary>
    public IReadOnlyList<LinkStatus> Status()
    {
        nint err = 0;
        var text = NativeCall.TakeString(Libmacula.macula_pool_status(_handle, ref err));
        NativeCall.Check(err);
        using var json = JsonDocument.Parse(text!);
        return [.. json.RootElement.EnumerateArray().Select(l => new LinkStatus(
            MeshId.Parse(l.GetProperty("station").GetString()!), l.GetProperty("host").GetString()!,
            l.GetProperty("port").GetUInt16(), l.GetProperty("direct").GetInt32() == 1,
            l.GetProperty("up").GetInt32() == 1))];
    }

    /// <summary>
    /// The pool's events as they happen: links coming up and going down. Only the latest 256 are kept
    /// for a reader that falls behind.
    /// </summary>
    public async IAsyncEnumerable<PoolEvent> EventsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var (text, closed) = await NativeCall.RunAsync(token =>
            {
                nint err = 0;
                var item = Libmacula.macula_pool_events_next(_handle, 0, token, out var ended, ref err);
                NativeCall.Check(err, cancellationToken);
                return (NativeCall.TakeString(item), ended == 1);
            }, cancellationToken).ConfigureAwait(false);
            if (closed || text is null)
            {
                yield break;
            }
            yield return PoolEventOf(text);
        }
    }

    private static PoolEvent PoolEventOf(string text)
    {
        using var json = JsonDocument.Parse(text);
        var e = json.RootElement;
        return e.GetProperty("kind").GetString() switch
        {
            "link" => new LinkChanged(MeshId.Parse(e.GetProperty("station").GetString()!),
                e.GetProperty("direct").GetInt32() == 1, e.GetProperty("up").GetInt32() == 1,
                e.GetProperty("error").ValueKind == JsonValueKind.Null ? null : e.GetProperty("error").GetString()),
            _ => new IssuerFailed(e.GetProperty("error").GetString()!),
        };
    }

    /// <summary>
    /// Calls <paramref name="procedure"/> in <paramref name="realm"/> by direct dial, and returns its result, sealed as
    /// <see cref="CallOptions.Confidential"/> says.
    /// </summary>
    public async Task<JsonNode?> CallAsync(MeshId realm, string procedure, JsonNode? payload = null,
        CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reply = await CallTextAsync(realm, procedure, payload, options, false, cancellationToken).ConfigureAwait(false);
        return Payload.FromJson(reply);
    }

    /// <summary>
    /// <see cref="CallAsync"/>, returning its result with its seal report: whether the exchange behind the result was
    /// sealed, to which provider and key (<see cref="SealReport"/>). After a <c>sealed_refused</c> and one reseal, the
    /// report names the reseal's key. An error is thrown as <see cref="CallAsync"/> throws it, with no report.
    /// </summary>
    public async Task<Reported> CallReportAsync(MeshId realm, string procedure, JsonNode? payload = null,
        CallOptions? options = null, CancellationToken cancellationToken = default)
    {
        var reply = await CallTextAsync(realm, procedure, payload, options, true, cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(reply);
        var root = json.RootElement;
        return new Reported(Payload.FromElement(root.GetProperty("result")), SealReport.FromElement(root));
    }

    private unsafe Task<string> CallTextAsync(MeshId realm, string procedure, JsonNode? payload, CallOptions? options,
        bool report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        var payloadJson = Payload.ToJson(payload);
        var timeoutMs = Milliseconds(options?.Timeout);
        var optionsJson = Sealing.CallOptionsJson(options?.Provider, options?.Ucan, options?.Confidential, report);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            nint result;
            fixed (byte* r = realm.Bytes)
            {
                result = Libmacula.macula_pool_call_opts(_handle, r, procedure, payloadJson, optionsJson, timeoutMs, token,
                    ref err);
            }
            NativeCall.Check(err, cancellationToken);
            return NativeCall.TakeString(result)!;
        }, cancellationToken);
    }

    /// <summary>The providers the realm trusts for <paramref name="procedure"/>, freshest first.</summary>
    public unsafe Task<IReadOnlyList<Provider>> ProvidersAsync(MeshId realm, string procedure, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(procedure);
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync<IReadOnlyList<Provider>>(token =>
        {
            nint err = 0;
            nint text;
            fixed (byte* r = realm.Bytes)
            {
                text = Libmacula.macula_pool_providers(_handle, r, procedure, timeoutMs, token, ref err);
            }
            NativeCall.Check(err, cancellationToken);
            using var json = JsonDocument.Parse(NativeCall.TakeString(text)!);
            return [.. json.RootElement.EnumerateArray().Select(p => new Provider(
                MeshId.Parse(p.GetProperty("node").GetString()!), MeshId.Parse(p.GetProperty("station").GetString()!)))];
        }, cancellationToken);
    }

    /// <summary>Publishes <paramref name="payload"/> on <paramref name="topic"/> in <paramref name="realm"/>.</summary>
    public unsafe void Publish(MeshId realm, string topic, JsonNode? payload, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(topic);
        nint err = 0;
        fixed (byte* r = realm.Bytes)
        {
            Libmacula.macula_pool_publish(_handle, r, topic, Payload.ToJson(payload), Milliseconds(ttl), ref err);
        }
        NativeCall.Check(err);
    }

    /// <summary>
    /// Subscribes to <paramref name="topic"/> in <paramref name="realm"/>. Read the events with
    /// <see cref="Subscription.ReadAllAsync"/>; dispose the subscription to stop it.
    /// </summary>
    public unsafe Subscription Subscribe(MeshId realm, string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        nint err = 0;
        SubscriptionHandle handle;
        fixed (byte* r = realm.Bytes)
        {
            handle = Libmacula.macula_pool_subscribe(_handle, r, topic, ref err);
        }
        NativeCall.Check(err);
        return new Subscription(handle);
    }

    /// <summary>
    /// Publishes <paramref name="payload"/> on <paramref name="topic"/> in <paramref name="realm"/>, sealed under
    /// <paramref name="group"/>'s current epoch; <paramref name="topic"/> must be under the group's prefix. The group
    /// is joined first, which pulls its epoch from the org's distributor over a sealed call, so this takes a timeout
    /// like a call. A refused join, or a publication that cannot be sealed, throws a <see cref="GroupException"/>
    /// with the reason macula gave.
    /// </summary>
    public unsafe Task PublishGroupAsync(MeshId realm, string topic, JsonNode? payload, Group group,
        TimeSpan? ttl = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(topic);
        var payloadJson = Payload.ToJson(payload);
        var optionsJson = GroupOptionsJson(group);
        var ttlMs = Milliseconds(ttl);
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            fixed (byte* r = realm.Bytes)
            {
                Libmacula.macula_pool_publish_group(_handle, r, topic, payloadJson, optionsJson, ttlMs, timeoutMs,
                    token, ref err);
            }
            NativeCall.Check(err, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Subscribes to <paramref name="topic"/> in <paramref name="realm"/> as a member of <paramref name="group"/>:
    /// every event the group seals is opened, and one that cannot be opened arrives once with a null payload and
    /// <see cref="MeshEvent.Unopened"/> naming why. A <em>clear</em> event under the prefix is not refused for you:
    /// only a group whose own policy is <c>required</c> has that refused, and the policy is the distributor's to
    /// set, so a subscriber that wants sealed events alone must check <see cref="MeshEvent.Sealed"/>. The group is
    /// joined first, pulling its epoch from the org's distributor over a sealed call. A join stays held once it
    /// succeeded, also when this subscribe then fails, so the pool keeps refusing clear publishes under the prefix
    /// (<see cref="GroupException"/>, reason <c>group_held</c>) from then on.
    /// </summary>
    public unsafe Task<Subscription> SubscribeGroupAsync(MeshId realm, string topic, Group group,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(topic);
        var optionsJson = GroupOptionsJson(group);
        var timeoutMs = Milliseconds(timeout);
        return NativeCall.RunAsync(token =>
        {
            nint err = 0;
            SubscriptionHandle handle;
            fixed (byte* r = realm.Bytes)
            {
                handle = Libmacula.macula_pool_subscribe_group(_handle, r, topic, optionsJson, timeoutMs, token,
                    ref err);
            }
            NativeCall.Check(err, cancellationToken);
            return new Subscription(handle);
        }, cancellationToken);
    }

    internal static long Milliseconds(TimeSpan? span)
    {
        if (span is not { } s)
        {
            return 0;
        }
        if (s <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(span), s, "a timeout is positive");
        }
        return (long)Math.Ceiling(s.TotalMilliseconds);
    }

    internal PoolHandle Handle => _handle;

    /// <summary>Closes every link, subscription, served procedure and stream of the pool.</summary>
    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
