using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Connection;

/// <summary>Delivered to a subscriber for each inbound EVENT the pool has not already delivered from another link.</summary>
public delegate Task PoolEventHandler(EventInfo eventInfo);

/// <summary>One station a <see cref="StationPool"/> dials -- a plain host:port, no identity attached (every link in a pool shares the pool's one identity).</summary>
public sealed record Seed(string Host, int Port);

/// <summary>Snapshot of one configured seed's current link. `NodeId` is null until CONNECT/HELLO completes.</summary>
public sealed record LinkInfo(Seed Seed, bool Connected, byte[]? NodeId);

/// <summary>Aggregate health snapshot of a pool. Lock-free best-effort read -- suitable for a `/health` endpoint, not hot-loop polling.</summary>
public sealed record PoolStatus(int HealthyLinks, int FailedLinks, int Subscriptions);

/// <summary>No link in the pool has completed its CONNECT/HELLO handshake. Mirrors macula_client's `{error, no_healthy_station}`.</summary>
public sealed class NoHealthyStationException : Exception
{
    public NoHealthyStationException()
        : base("no link in the pool has completed its CONNECT/HELLO handshake")
    {
    }
}

/// <summary>Every link targeted by a publish rejected it. Mirrors macula_client's `summarize_publish` all-failed case.</summary>
public sealed class PublishFailedException : Exception
{
    public PublishFailedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Picks how <see cref="StationPool.CallAsync"/>/<see cref="StationPool.PublishAsync"/>
/// order the pool's currently-connected links before applying their own
/// existing first-match/replication-factor logic -- it changes ORDER
/// only, never how many links get used. Matches macula_client.erl's own
/// link_selection option exactly (first_success/random), so config
/// ported from the Erlang reference (or another SDK) doesn't need
/// re-learning. SubscribeAsync/AdvertiseAsync are out of scope: they
/// already fan out to every connected link, no ordering decision to
/// make. PickConnectedSession is also out of scope -- it hands back a
/// Session for driving dedicated-stream operations directly, not a
/// pool-mediated Call/Publish.
/// </summary>
public enum LinkSelection
{
    /// <summary>
    /// (The default.) Derives the actual policy from
    /// <see cref="StationDiscoveryOptions.Enabled"/>: <see cref="FirstSuccess"/>
    /// if discovery is off (today's original behavior, unchanged), <see cref="Random"/>
    /// if it's on. Set LinkSelection explicitly to override that pairing
    /// either way.
    /// </summary>
    Auto,

    /// <summary>
    /// Tries links in Seed-list order (bootstrap Seeds in the order the
    /// caller gave them, then discovered links in discovery order --
    /// see <see cref="PooledLink.Ordinal"/>'s own doc for why this
    /// ordering has to be tracked explicitly rather than relying on
    /// enumeration order) -- this SDK's original, pre-existing
    /// behavior, left completely untouched on purpose for zero behavior
    /// drift when a caller doesn't opt into StationDiscovery.
    /// </summary>
    FirstSuccess,

    /// <summary>
    /// Uniformly shuffles the connected-links list before the same
    /// first-match (Call) or take-first-N (Publish) logic runs -- a
    /// real, deliberate rotation, not an accident of iteration order.
    /// Composes safely with a small ReplicationFactor: shuffling ahead
    /// of a 1-element slice is a no-op.
    /// </summary>
    Random,
}

/// <summary>
/// Configures opt-in discovery of additional stations via
/// hecate_stations.list_stations, layered on top of the caller-supplied
/// bootstrap Seeds. Default (Enabled == false) is a complete no-op --
/// zero config means zero behavior change, matching macula_client.erl's
/// own station_discovery option.
///
/// Bootstrap Seeds keep their exact current meaning: dialed first,
/// permanent fallback if discovery never succeeds, never replaced.
/// Discovery only ADDS links -- a station missing from a later refresh
/// does NOT tear down an existing link; removal stays tied to the
/// existing respawn/backoff cleanup only, never to absence from a
/// discovery response (replication lag in the station directory isn't
/// evidence a station is gone).
/// </summary>
public sealed class StationDiscoveryOptions
{
    public bool Enabled { get; init; } = false;

    /// <summary>Interval between discovery attempts once at least one bootstrap link is up. Default 30 minutes.</summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Bounds discovery's OWN adds only, not this pool's total link
    /// count: discovery adds a link only while the pool's total link
    /// count (Seeds + previously discovered, healthy or not) is below
    /// MaxLinks. More bootstrap Seeds than MaxLinks are all still
    /// dialed regardless (Connect never turns any of them away) -- that
    /// just means discovery adds nothing, ever, for this pool. A link
    /// discovery added that never connects (e.g. a station whose only
    /// known address isn't dialable under this pool's Trust) still
    /// occupies a slot against this cap even while permanently
    /// unhealthy -- there is no separate "healthy slots" budget.
    /// Default 5.
    /// </summary>
    public int MaxLinks { get; init; } = 5;
}

/// <summary>Tunables for <see cref="StationPool"/>. Defaults match macula_client.erl's own.</summary>
public sealed class StationPoolOptions
{
    /// <summary>Link ordering policy for Call/Publish -- see <see cref="LinkSelection"/>'s own doc. Default Auto (derives from StationDiscovery.Enabled).</summary>
    public LinkSelection LinkSelection { get; init; } = LinkSelection.Auto;

    /// <summary>Opt-in station discovery via hecate_stations.list_stations -- see <see cref="StationDiscoveryOptions"/>'s own doc. Default off, a complete no-op.</summary>
    public StationDiscoveryOptions StationDiscovery { get; init; } = new();


    /// <summary>How many currently-connected links a single publish fans out to. Partial success counts as success. Default 1.</summary>
    public int ReplicationFactor { get; init; } = 1;

    /// <summary>Flat delay before redialing a seed after its link dies (or fails to dial in the first place). Default 1s, matching ?LINK_RESPAWN_DELAY_MS -- flat, not exponential, per the reference and the rest of this SDK family's non-TS ports.</summary>
    public TimeSpan RespawnDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-link CONNECT/HELLO deadline. Default 30s, matching Session.ConnectAsync's own default.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Inbound-EVENT dedup window. Default 60s.</summary>
    public TimeSpan DedupWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the dedup table is swept for entries older than <see cref="DedupWindow"/>. Default 30s.</summary>
    public TimeSpan DedupSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Upper bound on a dying link's GOODBYE-send before respawn proceeds
    /// anyway. Session.CloseAsync has no timeout of its own; on an
    /// already-half-dead connection its GOODBYE write can block up to the
    /// QUIC idle timeout, which would silently turn a "flat 1s backoff"
    /// into "1s plus however long that takes." Default 2s.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on any single wire-level write issued through a link's
    /// send gate outside an application call (replay, Subscribe/Advertise/
    /// Unadvertise). Matters most during respawn's replay step, which runs
    /// under the pool's single state lock -- a stall there would block
    /// every OTHER Subscribe/Advertise/Publish/Call across the whole pool,
    /// not just this one link, for as long as the write hangs. Default 5s.
    /// </summary>
    public TimeSpan WireWriteTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// N concurrently-dialed links to N seed stations, all under one shared
/// identity (stations see the pool as a single peer, matching
/// macula_client.erl's own design). A dead link respawns after a flat
/// backoff and replays every tracked subscription/advertisement onto the
/// fresh connection. Inbound EVENTs are deduped across links before local
/// delivery. Port of macula_client.erl (macula-io/macula, src/client/) --
/// see that module's own doc for the design this mirrors.
///
/// == One link, one Session, every role at once ==
///
/// A pool link needs every role at once over ONE connection: deliver
/// EVENTs to subscribers, serve inbound CALLs against advertised
/// procedures, and make this pool's own outbound calls. A second Session
/// to the same seed under the pool's shared identity would get the link
/// kicked (a station closes a connection when a second one arrives under
/// the same identity). Each <see cref="Session"/> reads its own control
/// stream and routes every frame, so the pool uses the link's one Session
/// for all of them: a <see cref="Subscription"/> per tracked (realm,
/// topic), whose events are deduped across links and fanned out to
/// subscribers; the Session's inbound CALL queue; and its calls.
///
/// Handler dispatch for an inbound CALL is spawned OFF the serving loop,
/// never awaited inline -- awaiting it inline is exactly the
/// macula_link_inline_handler_deadlock shape macula itself shipped and
/// fixed in 10.18.0 (macula_station_link.erl spawns a child per CALL for
/// the same reason): a handler that calls back through this same link
/// would otherwise hold up every other inbound CALL on it.
///
/// == What is NOT ported this pass (documented, not silent) ==
///
/// No per-publisher `ordered` reorder buffer (macula_pubsub_order.erl):
/// two links race independently, so cross-link delivery order for one
/// publisher is not guaranteed. Delivery is as-arrives only.
///
/// Advertised STREAMING procedures get wire-level ADVERTISE replay (so a
/// provider's station-side registration survives a respawn) but the pool
/// does not run its own STREAM_OPEN accept-and-dispatch loop -- use
/// <see cref="PickConnectedSession"/> to get a live Session and drive
/// <see cref="Streaming.StreamHandle"/> against it directly, exactly as a
/// caller would against a single Session today. A dedicated stream is a
/// separate QUIC stream from the control stream, so this is safe to do
/// concurrently with everything else on the link.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed partial class StationPool : IAsyncDisposable
{
    private readonly KeyPair _identity;
    private readonly Trust _trust;
    private readonly StationPoolOptions _options;
    // ConcurrentDictionary, not Dictionary: StationDiscovery.cs adds
    // entries post-construction from its own background task while
    // Status/Links/PickConnectedSession read _links.Values lock-free
    // (by design, see their own docs) -- a plain Dictionary is not
    // safe for concurrent add-while-enumerate, unlike every OTHER
    // pool-wide collection here, which was already ConcurrentDictionary
    // for exactly this reason.
    private readonly ConcurrentDictionary<Seed, PooledLink> _links;
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly ConcurrentDictionary<(string RealmHex, string Topic), ConcurrentDictionary<Guid, PoolEventHandler>> _subs = new();
    private readonly ConcurrentDictionary<Guid, (string RealmHex, string Topic)> _subIndex = new();
    private readonly ConcurrentDictionary<(string RealmHex, string Procedure), (CallHandler Handler, Policy Policy)> _procs = new();
    private readonly ConcurrentDictionary<(string RealmHex, string Procedure), byte> _streamProcs = new();
    private readonly EventDedup _dedup;
    private readonly CancellationTokenSource _poolCts = new();
    // ConcurrentQueue, not List: StationDiscovery.cs adds tasks for newly
    // discovered links post-construction, concurrently with
    // DisposeAsync's own Task.WhenAll(_linkTasks) enumeration -- a plain
    // List is not safe for concurrent add-while-enumerate either.
    // ConcurrentQueue over ConcurrentBag specifically (found in
    // adversarial review 2026-09-05): this collection is pure
    // append-then-drain-all, never removed from piecemeal, which is
    // exactly ConcurrentQueue's own shape -- ConcurrentBag's per-thread
    // partitioning exists to make single-thread add-then-take-from-the-
    // same-thread fast, a pattern this never uses.
    private readonly ConcurrentQueue<Task> _linkTasks = new();
    // Resolved once at construction (LinkSelectionAuto's pairing with
    // StationDiscovery.Enabled doesn't change after Connect), matching
    // every other Options field's "read once, use forever" treatment.
    private readonly LinkSelection _resolvedLinkSelection;
    // Backs PooledLink.Ordinal's assignment -- see that property's own
    // doc. Starts past every bootstrap seed's own ordinal (assigned
    // 0..N-1 in the constructor below), so the first discovered link
    // always sorts after every bootstrap one.
    private long _nextLinkOrdinal;
    private long _publishSeq;
    private Timer? _dedupSweepTimer;
    // Int, not bool, so DisposeAsync can flip it with Interlocked.Exchange --
    // a plain bool double-checked-if would let two concurrent DisposeAsync
    // calls both pass the check and both call _poolCts.Cancel() on an
    // already-cancelled-then-disposed source. 0 = live, 1 = disposed.
    private int _disposed;

    public KeyPair Identity => _identity;

    /// <summary>
    /// Every public method that touches pool state calls this first. Found
    /// in adversarial review 2026-09-05: without it, a call after (or
    /// racing) DisposeAsync fell through to _stateLock.WaitAsync and threw
    /// a bare SemaphoreSlim ObjectDisposedException with no pool-level
    /// context -- or, once _stateLock stopped being disposed (see
    /// DisposeAsync's own doc), would have silently succeeded against a
    /// pool with every link already torn down instead of failing clearly.
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(StationPool));
        }
    }

    private StationPool(KeyPair identity, Trust trust, StationPoolOptions options, IReadOnlyList<Seed> seeds)
    {
        _identity = identity;
        _trust = trust;
        _options = options;
        _dedup = new EventDedup(options.DedupWindow);
        var distinctSeeds = seeds.Distinct().ToList();
        _links = new ConcurrentDictionary<Seed, PooledLink>(
            distinctSeeds.Select((s, i) => new KeyValuePair<Seed, PooledLink>(s, new PooledLink(s, i))));
        _nextLinkOrdinal = distinctSeeds.Count;
        _resolvedLinkSelection = ResolveLinkSelection(options.LinkSelection, options.StationDiscovery.Enabled);
        // Wall-clock-seeded, matching macula_client.erl's init/1: a
        // restart under a persisted identity must not re-issue seqs a
        // station's own dedup window from the pre-restart tail would
        // still be holding.
        _publishSeq = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    }

    /// <summary>
    /// Spawn one link's RunLinkAsync task --
    /// shared by Connect's own bootstrap-seed loop and
    /// StationDiscovery.cs's SpawnSeedLinkIfAbsent, so both paths spawn
    /// a link identically.
    /// </summary>
    private void SpawnLinkTasks(PooledLink link)
    {
        // Captured into a local BEFORE Task.Run, not read as _poolCts.Token
        // inside the lambdas -- found in adversarial review 2026-09-05:
        // when this is called from StationDiscovery's background task (not
        // Connect's own bootstrap loop, which always finishes before
        // DisposeAsync could possibly run), there is a real window where
        // DisposeAsync's _poolCts.Dispose() (which runs AFTER its
        // Task.WhenAll(_linkTasks) -- see that method's own doc) races the
        // threadpool actually dequeuing these two lambdas. Reading
        // _poolCts.Token lazily inside an orphaned lambda after Dispose()
        // has run throws ObjectDisposedException in a task nobody
        // observes; capturing the token eagerly here (while _poolCts is
        // still guaranteed alive, since SpawnLinkTasks itself is only ever
        // called from Connect or from the awaited discovery task) closes
        // that window entirely.
        var token = _poolCts.Token;
        _linkTasks.Enqueue(Task.Run(() => RunLinkAsync(link, token)));
    }

    /// <summary>
    /// Spawn a pool with one link per seed. Returns as soon as every
    /// link's dial has STARTED, not once any is connected -- handshakes
    /// complete asynchronously, matching macula_client:connect/2.
    /// </summary>
    public static StationPool Connect(IReadOnlyList<Seed> seeds, Trust trust, KeyPair? identity = null, StationPoolOptions? options = null)
    {
        if (seeds.Count == 0)
        {
            throw new ArgumentException("at least one seed is required", nameof(seeds));
        }

        var pool = new StationPool(identity ?? KeyPair.GenerateWithDefaultPuzzle(), trust, options ?? new StationPoolOptions(), seeds);
        foreach (var link in pool._links.Values)
        {
            pool.SpawnLinkTasks(link);
        }
        pool._dedupSweepTimer = new Timer(_ => pool._dedup.Sweep(), null, pool._options.DedupSweepInterval, pool._options.DedupSweepInterval);
        if (pool._options.StationDiscovery.Enabled)
        {
            // Eager capture, matching SpawnLinkTasks -- this spawn is safe
            // either way today (it's always registered in _linkTasks before
            // Connect returns, so DisposeAsync's Task.WhenAll can't miss it),
            // but capturing lazily here would silently reintroduce the same
            // race class Fix 3 closed elsewhere if this spawn is ever moved
            // off the synchronous Connect path (e.g. periodic re-discovery).
            var discoveryToken = pool._poolCts.Token;
            pool._linkTasks.Enqueue(Task.Run(() => pool.DiscoverStationsAsync(discoveryToken)));
        }
        return pool;
    }

    /// <summary>
    /// Publish to <see cref="StationPoolOptions.ReplicationFactor"/>
    /// currently-connected links (default 1). Partial success = success.
    /// Throws <see cref="NoHealthyStationException"/> if no link is
    /// connected, or <see cref="PublishFailedException"/> if every
    /// targeted link rejected the frame.
    /// </summary>
    public async Task PublishAsync(byte[] realm, string topic, Value payload, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        List<PooledLink> connected;
        long seq;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SelectLinks changes ORDER only, never count: ReplicationFactor
            // (applied below via Take(n)) stays the sole count control, so
            // this composes safely with a small ReplicationFactor --
            // shuffling ahead of a single-element Take is a no-op.
            connected = SelectLinks(ConnectedLinksSnapshot());
            seq = _publishSeq++;
        }
        finally
        {
            _stateLock.Release();
        }

        if (connected.Count == 0)
        {
            throw new NoHealthyStationException();
        }

        var n = Math.Clamp(_options.ReplicationFactor, 0, connected.Count);
        var targets = connected.Take(n).ToList();
        if (targets.Count == 0)
        {
            return; // replication factor capped at 0 by config -- a no-op, not a failure.
        }

        var spec = new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = _identity.NodeId(),
            Seq = (ulong)seq,
            Payload = payload,
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var results = await Task.WhenAll(targets.Select(link => TryPublishAsync(link, spec, ct))).ConfigureAwait(false);
        if (!results.Any(ok => ok))
        {
            throw new PublishFailedException("every targeted link rejected the publish");
        }
    }

    private static async Task<bool> TryPublishAsync(PooledLink link, PublishSpec spec, CancellationToken ct)
    {
        try
        {
            await link.PublishAsync(spec, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuine caller cancellation, not a link rejecting the write --
            // let it propagate through Task.WhenAll rather than being
            // folded into "every targeted link rejected the publish."
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Subscribe to (realm, topic). The pool wire-subscribes every
    /// currently-connected link the FIRST time a given (realm, topic) is
    /// tracked, and replays it onto every link that respawns afterward.
    /// Returns a subscription id for <see cref="UnsubscribeAsync"/>.
    /// </summary>
    public async Task<Guid> SubscribeAsync(byte[] realm, string topic, PoolEventHandler handler, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var subId = Guid.NewGuid();
        var key = (ToHex(realm), topic);
        bool isNewTopic;
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            isNewTopic = !_subs.ContainsKey(key);
            var byId = _subs.GetOrAdd(key, _ => new ConcurrentDictionary<Guid, PoolEventHandler>());
            byId[subId] = handler;
            _subIndex[subId] = key;
            connected = isNewTopic ? ConnectedLinksSnapshot() : new List<PooledLink>();
        }
        finally
        {
            _stateLock.Release();
        }

        if (isNewTopic)
        {
            var spec = new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = _identity.NodeId() };
            foreach (var link in connected)
            {
                await SubscribeLinkAsync(link, spec, ct).ConfigureAwait(false);
            }
        }

        return subId;
    }

    /// <summary>
    /// Drop a subscription. Idempotent. Matching macula_client.erl's own
    /// unsubscribe/2 exactly: the wire-level SUBSCRIBE is NOT retracted --
    /// it persists for the pool's lifetime, multiplexed across whichever
    /// local subscribers remain tracked for that (realm, topic).
    /// </summary>
    public async Task UnsubscribeAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_subIndex.TryRemove(subscriptionId, out var key))
            {
                return;
            }
            if (_subs.TryGetValue(key, out var byId))
            {
                byId.TryRemove(subscriptionId, out _);
                if (byId.IsEmpty)
                {
                    _subs.TryRemove(key, out _);
                }
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Register a handler for (realm, procedure) on every currently-live
    /// link, and replay it onto every link that respawns afterward. Unlike
    /// <see cref="SubscribeAsync"/>, this always fans out on every call
    /// (matching macula_client.erl's own advertise/5), not just the first
    /// time this (realm, procedure) is seen.
    /// </summary>
    public async Task AdvertiseAsync(byte[] realm, string procedure, CallHandler handler, Policy? policy = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _procs[key] = (handler, policy ?? Policy.Open);
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>Drop a previously-advertised procedure on every live link and remove it from replay state. Idempotent.</summary>
    public async Task UnadvertiseAsync(byte[] realm, string procedure, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _procs.TryRemove(key, out _);
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new UnadvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.UnadvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Wire-level ADVERTISE replay only for a streaming procedure -- see
    /// this class's own doc for why the pool does not run a STREAM_OPEN
    /// accept loop itself. Use <see cref="PickConnectedSession"/> plus
    /// <see cref="Streaming.StreamHandle.AcceptAsync"/> to actually serve
    /// inbound streams for this procedure.
    /// </summary>
    public async Task AdvertiseStreamAsync(byte[] realm, string procedure, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _streamProcs[key] = 0;
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    public async Task UnadvertiseStreamAsync(byte[] realm, string procedure, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var key = (ToHex(realm), procedure);
        List<PooledLink> connected;

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _streamProcs.TryRemove(key, out _);
            connected = ConnectedLinksSnapshot();
        }
        finally
        {
            _stateLock.Release();
        }

        var spec = new UnadvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = _identity.NodeId() };
        foreach (var link in connected)
        {
            await TrySendReplayAsync(link, ct, (s, c) => s.UnadvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Issue a CALL on the first connected link, moving to the next link only
    /// when the CALL was not sent on this one: its Session had ended, or its
    /// turn to write didn't come within <paramref name="timeout"/>. A reply,
    /// RESULT or ERROR, is returned from the link that gave it, and a call that
    /// timed out after its write started throws
    /// <see cref="CallTimeoutException"/>, so a provider never runs one call
    /// twice. When no link could take it, the last link's own failure is
    /// thrown. <paramref name="timeout"/> applies PER LINK, not to the call as
    /// a whole, so the worst case is N * timeout across N connected links.
    /// Pool calls publish no RPC telemetry facts, as macula's pool doesn't.
    /// </summary>
    public async Task<CallResponse> CallAsync(byte[] realm, string procedure, Value payload, TimeSpan timeout, byte[]? ucanToken = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        List<PooledLink> connected;
        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SelectLinks (FirstSuccess by default, Random when
            // StationDiscovery is enabled) changes which link is tried
            // FIRST, never the first-non-error-wins semantics below.
            connected = SelectLinks(ConnectedLinksSnapshot());
        }
        finally
        {
            _stateLock.Release();
        }

        if (connected.Count == 0)
        {
            throw new NoHealthyStationException();
        }

        return await CallUntilSentAsync(connected
            .Select(link => (Func<Task<CallResponse>>)(() => CallOnLinkAsync(link, realm, procedure, payload, timeout, ucanToken ?? Array.Empty<byte>(), ct)))
            .ToList()).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs calls in turn until one is sent, and returns its reply. It moves
    /// on only when a call's failure says its CALL was never sent
    /// (<see cref="ControlChannel.NotSent"/>); any other failure, a caller's
    /// cancellation included, and the last call's failure are thrown.
    /// </summary>
    internal static async Task<CallResponse> CallUntilSentAsync(IReadOnlyList<Func<Task<CallResponse>>> calls)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return await calls[i]().ConfigureAwait(false);
            }
            catch (Exception e) when (i < calls.Count - 1 && ControlChannel.NotSent(e))
            {
                // Never sent on this link, so the next one can't run it twice.
            }
        }
    }

    /// <summary>
    /// One CALL on one link, the whole of it bounded by
    /// <paramref name="timeout"/>: the turn to write on the link's Session,
    /// the write and the wait for a reply. A slow write by anything else on
    /// the link can't stretch it. Running out of time throws
    /// <see cref="CallTimeoutException"/>, and a link that isn't connected
    /// throws <see cref="SessionEndedException"/>, as its Session has ended.
    /// </summary>
    private async Task<CallResponse> CallOnLinkAsync(PooledLink link, byte[] realm, string procedure, Value payload, TimeSpan timeout, byte[] ucanToken, CancellationToken ct)
    {
        var session = link.Session ?? throw new SessionEndedException(new IOException($"link to {link.Seed.Host}:{link.Seed.Port} is not connected"), writeStarted: false);
        var callId = new byte[16];
        Random.Shared.NextBytes(callId);

        var spec = new CallSpec
        {
            CallId = callId,
            Procedure = procedure,
            Realm = realm,
            Payload = payload,
            DeadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds,
            Caller = _identity.NodeId(),
            UcanToken = ucanToken,
        };

        return await session.LinkCallAsync(spec, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A live Session from a currently-connected link, for driving
    /// dedicated-stream operations (<see cref="Streaming.StreamHandle"/>,
    /// content transfer) or its own calls and subscriptions directly against
    /// a pooled connection, concurrently with the pool's own use of it. Don't
    /// serve inbound CALLs on it: the pool already serves that link's queue.
    /// Returns null if no link is currently connected.
    /// </summary>
    public Session? PickConnectedSession()
    {
        ThrowIfDisposed();
        // OrderBy(Ordinal): see ConnectedLinksSnapshot's own doc on why
        // _links.Values' own enumeration order can't be relied on since
        // it became a ConcurrentDictionary.
        return _links.Values.Where(l => l.Connected).OrderBy(l => l.Ordinal).FirstOrDefault()?.Session;
    }

    /// <summary>Aggregate health snapshot. Lock-free best-effort -- not for hot-loop polling.</summary>
    public PoolStatus Status
    {
        get
        {
            ThrowIfDisposed();
            var healthy = _links.Values.Count(l => l.Connected);
            return new PoolStatus(healthy, _links.Count - healthy, _subIndex.Count);
        }
    }

    /// <summary>Per-seed snapshot, in Seed-list/discovery order (see PooledLink.Ordinal's own doc). Lock-free best-effort -- not for hot-loop polling.</summary>
    public IReadOnlyList<LinkInfo> Links
    {
        get
        {
            ThrowIfDisposed();
            return _links.Values.OrderBy(l => l.Ordinal).Select(l => new LinkInfo(l.Seed, l.Connected, l.Connected ? l.Session?.RemoteInfo.NodeId : null)).ToList();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dedupSweepTimer?.Dispose();
        _poolCts.Cancel();
        await Task.WhenAll(_linkTasks).ConfigureAwait(false);
        _poolCts.Dispose();
        // _stateLock is deliberately NEVER disposed. Found in adversarial
        // review 2026-09-05: SemaphoreSlim.Dispose() does not complete or
        // release outstanding async waiters -- a WaitAsync() call already
        // blocked on this lock at the instant of dispose would hang
        // forever instead of throwing, and every caller of a public method
        // above still holds a reference to this same instance for its own
        // process lifetime. ThrowIfDisposed() above is what actually
        // prevents post-dispose use; the semaphore itself is left alive
        // (a bounded, tiny handle, not a real leak) rather than risk that
        // hang for the sake of disposing it.
    }

    //====================================================================
    // Link lifecycle
    //====================================================================

    private async Task RunLinkAsync(PooledLink link, CancellationToken poolCt)
    {
        while (!poolCt.IsCancellationRequested)
        {
            Session session;
            try
            {
                session = await Session.ConnectAsync(link.Seed.Host, link.Seed.Port, _identity, _trust, _options.HandshakeTimeout, poolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (poolCt.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Dial failure -- first attempt or a later one, same
                // respawn cycle either way (mirrors after_link_start's
                // error path in the reference, which schedules a respawn
                // timer even for the very first dial).
                await DelayRespawnAsync(poolCt).ConfigureAwait(false);
                continue;
            }

            List<(string RealmHex, string Topic)> subsSnapshot;
            List<(string RealmHex, string Procedure)> procsSnapshot;
            List<(string RealmHex, string Procedure)> streamProcsSnapshot;
            try
            {
                await _stateLock.WaitAsync(poolCt).ConfigureAwait(false);
                try
                {
                    link.Session = session;
                    link.LastKnownNodeId = session.RemoteInfo.NodeId;
                    subsSnapshot = _subs.Keys.ToList();
                    procsSnapshot = _procs.Keys.ToList();
                    streamProcsSnapshot = _streamProcs.Keys.ToList();
                    // Flipped HERE, before the actual wire replay below runs
                    // (and while still under _stateLock) -- found in
                    // adversarial review 2026-09-05 that awaiting every
                    // replay write while HOLDING the pool-wide lock (the
                    // original shape) meant a single degraded seed with many
                    // tracked subscriptions could block every OTHER link's
                    // Publish/Call/Subscribe/Advertise for
                    // N * WireWriteTimeout -- exactly when a respawn is
                    // most likely (a station is degraded). Flipping
                    // Connected here, then releasing the lock BEFORE
                    // running the writes, preserves "never both, never
                    // neither" from this method's own original doc: a
                    // Subscribe/Advertise/Unadvertise for a NEW (realm,
                    // topic/procedure) that lands after this point sees
                    // this link as connected (ConnectedLinksSnapshot filters
                    // on Connected) and wire-issues to it directly, while
                    // this link's own replay below only re-sends what was
                    // in the snapshot taken at this exact instant -- never
                    // both (the new entry isn't in the snapshot) and never
                    // neither (the immediate wire-issue path covers it).
                    link.Connected = true;
                }
                finally
                {
                    _stateLock.Release();
                }
            }
            catch (OperationCanceledException) when (poolCt.IsCancellationRequested)
            {
                // link.Connected/Session were never flipped to reflect this
                // session on the pool-shutdown path (the state lock wait
                // itself was what got cancelled), so there is nothing to
                // unmark -- just close it and stop.
                await CloseSessionAsync(session).ConfigureAwait(false);
                return;
            }

            var serving = Task.CompletedTask;
            try
            {
                await ReplayAsync(link, session, subsSnapshot, procsSnapshot, streamProcsSnapshot, poolCt).ConfigureAwait(false);
                serving = ServeLinkCallsAsync(session, poolCt);
                // The link lives as long as its Session's control stream. An
                // unrecognized frame (e.g. the station's own unprompted
                // content-procedure advertise broadcasts) doesn't end it: the
                // Session counts those.
                await session.Ended.WaitAsync(poolCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (poolCt.IsCancellationRequested)
            {
                // Pool shutdown, not a fault -- still unmark the link (a
                // caller mid-CallAsync against it, or Status/Links read
                // after DisposeAsync returns, must not see a stale
                // Connected=true) before closing. No subscriber handler runs
                // after this returns.
                await MarkDisconnectedAsync(link).ConfigureAwait(false);
                await CloseSessionAsync(session).ConfigureAwait(false);
                await serving.ConfigureAwait(false);
                await link.ConsumersEnded().ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                // A replay write's own genuine failure -- rare, since
                // TrySendReplayAsync swallows everything but real poolCt
                // cancellation -- falls through to respawn.
            }

            await MarkDisconnectedAsync(link).ConfigureAwait(false);
            await CloseSessionAsync(session).ConfigureAwait(false);
            await serving.ConfigureAwait(false);
            await link.ConsumersEnded().ConfigureAwait(false);
            await DelayRespawnAsync(poolCt).ConfigureAwait(false);
        }
    }

    /// <summary>Flip a link to disconnected. Its in-flight calls fail as soon as its Session closes, rather than waiting out their own timeout against a link that's already known to be gone.</summary>
    private async Task MarkDisconnectedAsync(PooledLink link)
    {
        await _stateLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            link.Connected = false;
            link.Session = null;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>
    /// Replays a pre-taken snapshot of tracked subscriptions/advertisements
    /// onto `link`'s freshly-connected Session -- first connect and every
    /// later respawn go through this exact same path, so there is no
    /// separate "pre-handshake pending" concept to keep in sync. Runs
    /// OUTSIDE `_stateLock` (unlike the version this replaced): the
    /// snapshot is taken under the lock, at the same instant `link.Connected`
    /// is flipped to true, in RunLinkAsync itself -- see that method's own
    /// doc for why the actual wire writes must not run while holding the
    /// pool-wide lock, and for why replaying exactly this snapshot (rather
    /// than re-reading `_subs`/`_procs`/`_streamProcs` here) is what keeps
    /// "never both, never neither" true even though the writes now happen
    /// after the lock is released.
    /// </summary>
    private async Task ReplayAsync(
        PooledLink link,
        Session session,
        List<(string RealmHex, string Topic)> subsSnapshot,
        List<(string RealmHex, string Procedure)> procsSnapshot,
        List<(string RealmHex, string Procedure)> streamProcsSnapshot,
        CancellationToken ct)
    {
        foreach (var (realmHex, topic) in subsSnapshot)
        {
            var spec = new SubscribeSpec { Topic = topic, Realm = FromHex(realmHex), Subscriber = _identity.NodeId() };
            await SubscribeLinkAsync(link, spec, ct, session).ConfigureAwait(false);
        }
        foreach (var (realmHex, procedure) in procsSnapshot)
        {
            var spec = new AdvertiseSpec { Realm = FromHex(realmHex), Procedure = procedure, Advertiser = _identity.NodeId() };
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
        foreach (var (realmHex, procedure) in streamProcsSnapshot)
        {
            var spec = new AdvertiseSpec { Realm = FromHex(realmHex), Procedure = procedure, Advertiser = _identity.NodeId() };
            await TrySendReplayAsync(link, ct, (s, c) => s.AdvertiseAsync(spec, c)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A write's failure is swallowed, matching macula_client_replay.erl
    /// exactly for a REPLAY write: this link's own next respawn retries.
    /// Also used for a live (non-replay) wire-issue from Subscribe/
    /// Advertise/Unadvertise -- same swallow policy there too, since those
    /// are already fire-and-forget on the wire and the pool's own tracked
    /// state (which is what actually matters) was already updated before
    /// this runs.
    ///
    /// Bounded by <see cref="StationPoolOptions.WireWriteTimeout"/>, NOT
    /// just `ct`: called from ReplayOntoAsync while `_stateLock` is held,
    /// so an unbounded stall here would block every other pool operation,
    /// not just this one link. A timeout here is swallowed like any other
    /// write failure; only genuine cancellation via the ORIGINAL `ct`
    /// propagates.
    /// </summary>
    private async Task TrySendReplayAsync(PooledLink link, CancellationToken ct, Func<Session, CancellationToken, Task> send)
    {
        if (link.Session is not { } session)
        {
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.WireWriteTimeout);
        try
        {
            await send(session, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // swallowed -- see summary.
        }
    }

    /// <summary>
    /// Starts this link's copy of a pool subscription, and the task that hands
    /// its events to the pool's subscribers. Bounded by WireWriteTimeout and
    /// swallowed on failure like a replay write: the link's next respawn
    /// subscribes again.
    /// </summary>
    private async Task SubscribeLinkAsync(PooledLink link, SubscribeSpec spec, CancellationToken ct, Session? session = null)
    {
        if ((session ?? link.Session) is not { } target)
        {
            return;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.WireWriteTimeout);
        Subscription subscription;
        try
        {
            subscription = await target.SubscribeAsync(spec, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return;
        }
        link.AddConsumer(ConsumeSubscriptionAsync(subscription, () => target.SubscribeAsync(spec), evt => DeliverEventAsync(_subs, _dedup, spec, evt)));
    }

    /// <summary>
    /// Hands one link subscription's events to handle (the pool's dedup and
    /// fan-out) strictly in arrival order, each finished before the next
    /// starts, so one subscriber's handler is never re-entered for two events
    /// from the same link. When this copy falls behind, subscribeAgain makes a
    /// fresh one before the overflowed one is closed, so the station-side
    /// subscription never lapses. Ends with the link's Session.
    /// </summary>
    internal static async Task ConsumeSubscriptionAsync(Subscription subscription, Func<Task<Subscription>> subscribeAgain, Func<EventInfo, Task> handle)
    {
        try
        {
            while (true)
            {
                EventInfo evt;
                try
                {
                    evt = await subscription.RecvEventAsync(Timeout.InfiniteTimeSpan).ConfigureAwait(false);
                }
                catch (ConsumerOverflowException)
                {
                    var fresh = await subscribeAgain().ConfigureAwait(false);
                    await subscription.DisposeAsync().ConfigureAwait(false);
                    subscription = fresh;
                    continue;
                }
                await handle(evt).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The link's Session ended or was closed; its next incarnation
            // subscribes again.
        }
        finally
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves this link Session's inbound CALLs until the Session ends. Each
    /// handler runs off this loop -- see this class's own doc on why.
    /// </summary>
    private async Task ServeLinkCallsAsync(Session session, CancellationToken poolCt)
    {
        while (true)
        {
            CallInfo call;
            try
            {
                call = await session.NextInboundCallAsync(poolCt).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return; // the Session ended, or the pool is disposing
            }
            _ = Task.Run(() => HandleInboundCallAsync(session, call, poolCt), CancellationToken.None);
        }
    }

    private async Task DelayRespawnAsync(CancellationToken poolCt)
    {
        try
        {
            await Task.Delay(_options.RespawnDelay, poolCt).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // pool disposing -- the outer loop's own poolCt check exits it.
        }
    }

    private async Task CloseSessionAsync(Session session)
    {
        using var cts = new CancellationTokenSource(_options.CloseTimeout);
        try
        {
            await session.CloseAsync().AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort and bounded -- matches Session.CloseAsync's own
            // GOODBYE-send tolerance, just capped so a hung close can't
            // turn "flat backoff" into "backoff plus the QUIC idle
            // timeout." A dead identity must never dial its replacement
            // before this returns -- see this class's own doc on the
            // same-identity-double-connect hazard.
        }
    }

    // OrderBy(Ordinal): _links is a ConcurrentDictionary (needed for
    // StationDiscovery's post-construction adds -- see PooledLink.Ordinal's
    // own doc), and ConcurrentDictionary.Values enumerates in hash-bucket
    // order, NOT insertion order, unlike the plain Dictionary this used
    // to be. Sorting by Ordinal restores the caller's own Seed-list
    // order (bootstrap first, in the order given, discovered links
    // after, in discovery order) that FirstSuccess's whole contract
    // depends on.
    private List<PooledLink> ConnectedLinksSnapshot() => _links.Values.Where(l => l.Connected).OrderBy(l => l.Ordinal).ToList();

    /// <summary>
    /// Orders a connected-links snapshot per <see cref="_resolvedLinkSelection"/> --
    /// the single shared choke point CallAsync and PublishAsync both
    /// route through, so the two operations can never drift onto
    /// different selection policies by accident.
    /// </summary>
    private List<PooledLink> SelectLinks(List<PooledLink> connected) => SelectLinksCore(connected, _resolvedLinkSelection);

    /// <summary>
    /// The pure logic behind SelectLinks, extracted as a static,
    /// internal function so it's directly unit-testable without a live
    /// StationPool (whose only constructor path, Connect, immediately
    /// dials real sessions -- this repo has no fake-dialer test seam,
    /// see StationPoolLinkSelectionTests' own header doc). FirstSuccess
    /// passes the list through unchanged (today's original insertion-
    /// order behavior). Random shuffles a COPY (never the list the
    /// caller passed in) via a Fisher-Yates shuffle using
    /// <see cref="Random.Shared"/> (thread-safe since .NET 6, no
    /// separate locking needed here).
    /// </summary>
    internal static List<PooledLink> SelectLinksCore(List<PooledLink> connected, LinkSelection resolved)
    {
        if (resolved != LinkSelection.Random || connected.Count <= 1)
        {
            return connected;
        }
        var shuffled = new List<PooledLink>(connected);
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = System.Random.Shared.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }
        return shuffled;
    }

    /// <summary>
    /// The pure logic behind LinkSelectionAuto's pairing with
    /// StationDiscovery.Enabled, extracted so it's unit-testable
    /// without constructing a StationPool at all.
    /// </summary>
    internal static LinkSelection ResolveLinkSelection(LinkSelection configured, bool stationDiscoveryEnabled) =>
        configured == LinkSelection.Auto
            ? (stationDiscoveryEnabled ? LinkSelection.Random : LinkSelection.FirstSuccess)
            : configured;

    //====================================================================
    // Events and inbound calls on a link's Session
    //====================================================================

    /// <summary>
    /// Hands an event that a link's subscription to pattern received to that
    /// pattern's handlers, once however many links deliver it. The session
    /// reader already matched the event to the pattern by the station's rule,
    /// so the pattern is looked up exactly, wildcard or not, and an event
    /// matching both "orders/*" and "orders/placed" reaches each set of
    /// handlers once.
    /// </summary>
    internal static async Task DeliverEventAsync(IReadOnlyDictionary<(string RealmHex, string Topic), ConcurrentDictionary<Guid, PoolEventHandler>> subs, EventDedup dedup, SubscribeSpec pattern, EventInfo evt)
    {
        if (!dedup.CheckNew(evt.Realm, evt.Publisher, evt.Seq, evt.Topic, pattern.Topic))
        {
            return; // another link already delivered this fact for this pattern.
        }

        if (!subs.TryGetValue((ToHex(pattern.Realm), pattern.Topic), out var byId))
        {
            return;
        }

        foreach (var handler in byId.Values)
        {
            try
            {
                await handler(evt).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // one subscriber's failure must not affect delivery to
                // others, or this link's own pump.
            }
        }
    }

    /// <summary>
    /// Off the serving loop: the loop only ever takes calls and dispatches,
    /// it never awaits handler logic itself -- see this class's own doc on
    /// why (macula_link_inline_handler_deadlock). The Session queues only a
    /// CALL signed by the caller it names.
    /// </summary>
    private async Task HandleInboundCallAsync(Session session, CallInfo callInfo, CancellationToken ct)
    {
        // Read _procs exactly ONCE, so the policy check and the handler
        // dispatch that follows it always agree on the same registration --
        // two independent TryGetValue calls here would let a concurrent
        // AdvertiseAsync that TIGHTENS the policy (e.g. Open -> Required)
        // land in between them: the policy check would pass against the
        // stale Open entry while the handler that runs is already the new
        // one, serving an unauthenticated caller a procedure that had
        // already been re-gated. A single snapshot closes that window.
        var snapshot = _procs.TryGetValue((ToHex(callInfo.Realm), callInfo.Procedure), out var entry) ? entry : ((CallHandler Handler, Policy Policy)?)null;
        CallLookup lookup = (_, _) => snapshot?.Handler;
        PolicyLookup policyLookup = (_, _) => snapshot?.Policy ?? Policy.Open;

        var reply = await Session.BuildCallReplyAsync(session, callInfo, lookup, policyLookup, _identity).ConfigureAwait(false);
        try
        {
            await session.SendAsync(reply, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The link may already be dead -- RunLinkAsync notices its
            // Session ending separately and respawns.
        }
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);
}

/// <summary>
/// One seed's connection state inside a <see cref="StationPool"/>. Not
/// part of the public API -- app code interacts with the pool, never a
/// link directly (aside from <see cref="StationPool.PickConnectedSession"/>
/// handing back the underlying Session for dedicated-stream use).
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class PooledLink
{
    public Seed Seed { get; }

    /// <summary>
    /// Assignment order this link was created in -- bootstrap Seeds get
    /// 0..N-1 in the caller's own list order, a discovered link gets
    /// whatever the pool's shared counter is at when it's added (always
    /// higher than every bootstrap ordinal). The ONLY thing that
    /// determines FirstSuccess/take-first-N ordering everywhere it
    /// matters (ConnectedLinksSnapshot, PickConnectedSession, Links) --
    /// found in adversarial review 2026-09-05: _links itself became a
    /// ConcurrentDictionary (needed so StationDiscovery can add entries
    /// post-construction safely), and ConcurrentDictionary.Values
    /// enumerates in hash-bucket order, NOT insertion order -- unlike
    /// the plain Dictionary this class used before, which happened to
    /// preserve it. Without this field, a caller's own "primary, then
    /// fallback" Seed list would silently stop being honored the
    /// instant this diff shipped, and Links' own reported order would
    /// vary randomly across process restarts (per-process randomized
    /// string hashing) -- confirmed empirically, not assumed.
    /// </summary>
    public long Ordinal { get; }

    public Session? Session { get; set; }
    public bool Connected { get; set; }

    /// <summary>
    /// The peer's node id as of the last successful handshake -- unlike
    /// Connected/Session, deliberately NOT cleared when the link
    /// disconnects: a link mid-backoff/redial is still, as far as
    /// station-DISCOVERY's own dedupe-by-identity is concerned (see
    /// StationDiscovery.cs's HasLinkForNodeId), "the same station we
    /// already have a link to." Updated on every successful dial, not
    /// just the first -- a redial can legitimately prove a different
    /// node id (e.g. a DNS name repointed to a different station).
    /// </summary>
    public byte[]? LastKnownNodeId { get; set; }

    // The tasks handing this link's subscriptions to the pool's subscribers,
    // for the current Session. RunLinkAsync waits for them to end before a
    // respawn dials again, so an old incarnation's handler never runs
    // alongside a new one's for the same subscriber.
    private ConcurrentQueue<Task> _consumers = new();

    public PooledLink(Seed seed, long ordinal)
    {
        Seed = seed;
        Ordinal = ordinal;
    }

    /// <summary>Tracks a task handing one of this link's subscriptions to the pool's subscribers.</summary>
    public void AddConsumer(Task consumer) => _consumers.Enqueue(consumer);

    /// <summary>Completes once every subscription consumer tracked for this link so far has ended.</summary>
    public Task ConsumersEnded() => Task.WhenAll(Interlocked.Exchange(ref _consumers, new ConcurrentQueue<Task>()));

    /// <summary>Publishes on this link's Session. Sends on a Session take turns, so this needs no gate of its own.</summary>
    public Task PublishAsync(PublishSpec spec, CancellationToken ct = default) =>
        (Session ?? throw new InvalidOperationException("link is not connected")).PublishAsync(spec, ct);
}
