using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Macula.Content;
using Macula.Dht;
using Macula.Frame;
using Macula.Identity;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.X509;
using static Macula.Tests.CertChainFixtures;
using DhtRecord = Macula.Dht.Record;

namespace Macula.Tests;

/// <summary>
/// Direct-dial candidate selection with no network: a fake DHT answers with
/// real signed records, and fake dials and requests remember which stations
/// were reached. The cases and names match macula-go's
/// directdial/candidates_test.go, so every SDK in the family is held to the
/// same behaviour.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class DirectDialCandidatesTests
{
    private static readonly byte[] Realm = new byte[32];
    private const string Procedure = "macula_dotnet_sdk.candidates_test.echo";
    private const string Org = "acme-corp";

    // Roomy leaves time for every candidate; Short is the deadline of the
    // timeout-bound cases, and ShortBound how long those may take to return.
    private static readonly TimeSpan Roomy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ShortBound = TimeSpan.FromSeconds(1);

    private static readonly byte[] ProcedureKey = RecordFactory.ProcedureKey(RecordFactory.DiscoveryUri(Realm, Procedure));
    private static readonly byte[] Content = "the content"u8.ToArray();

    [Fact]
    public async Task Call_tries_the_next_advertisement_when_a_station_has_no_endpoint()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a), Advertisement(b)]);
        dht.PublishEndpoint(b.Station, StationEndpoint(b.Station, b.Host));
        var stations = new FakeStations();

        var response = await CallAsync(dht, stations, Roomy);

        Assert.Equal("reply from b.test", ReplyText(response));
        Assert.Equal(new[] { "b.test" }, stations.Reached);
    }

    [Fact]
    public async Task Call_retries_when_no_advertisement_qualifies()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a, expiresInMs: -1_000)], [Advertisement(b)]);
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));
        dht.PublishEndpoint(b.Station, StationEndpoint(b.Station, b.Host));
        var stations = new FakeStations();

        var response = await CallAsync(dht, stations, Roomy);

        Assert.Equal("reply from b.test", ReplyText(response));
        Assert.Equal(new[] { "b.test" }, stations.Reached);
    }

    [Fact]
    public async Task Call_tries_the_next_station_when_a_dial_fails()
    {
        var (dht, a, _) = TwoProvidersWithEndpoints();
        var stations = new FakeStations();
        stations.Refuse(a.Host);

        var response = await CallAsync(dht, stations, Roomy);

        Assert.Equal("reply from b.test", ReplyText(response));
        Assert.Equal(new[] { "a.test", "b.test" }, stations.Reached);
    }

    // A guard, green before and after the fix: once the CALL has gone out,
    // its failure is the call's result.
    [Fact]
    public async Task Call_never_sends_the_request_twice()
    {
        var (dht, _, _) = TwoProvidersWithEndpoints();
        var stations = new FakeStations();
        var resetAfterSending = new DirectDial.RequestAt<string, CallResponse>((host, _, _) =>
            Task.FromException<CallResponse>(new IOException($"{host} reset the stream after the CALL went out")));

        var e = await Assert.ThrowsAsync<IOException>(() => CallAsync(dht, stations, Roomy, resetAfterSending));

        Assert.Contains("a.test", e.Message);
        Assert.Equal(new[] { "a.test" }, stations.Reached);
    }

    [Fact]
    public async Task Call_timeout_bounds_resolution()
    {
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DirectDial.ProcedureNotAdvertisedException>(() => CallAsync(new FakeDht(), new FakeStations(), Short));

        AssertReturnedWithin(ShortBound, clock);
    }

    [Fact]
    public async Task Call_timeout_bounds_the_endpoint_lookup()
    {
        var a = new Provider("a.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a)]);
        var stations = new FakeStations();
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DirectDial.StationEndpointNotFoundException>(() => CallAsync(dht, stations, Short));

        AssertReturnedWithin(ShortBound, clock);
        Assert.Empty(stations.Reached);
    }

    [Fact]
    public async Task Open_stream_direct_tries_the_next_station_when_a_dial_fails()
    {
        var (dht, a, _) = TwoProvidersWithEndpoints();
        var stations = new FakeStations();
        stations.Refuse(a.Host);

        var stream = await OpenStreamAsync(dht, stations, new DirectDial.RequestAt<string, string>((host, _, _) => Task.FromResult($"stream at {host}")));

        Assert.Equal("stream at b.test", stream);
        Assert.Equal(new[] { "a.test", "b.test" }, stations.Reached);
    }

    // A guard, green before and after the fix: STREAM_OPEN may already be out
    // once the station is dialed, so a failure there is never retried.
    [Fact]
    public async Task Open_stream_direct_never_opens_the_stream_twice()
    {
        var (dht, _, _) = TwoProvidersWithEndpoints();
        var stations = new FakeStations();

        var e = await Assert.ThrowsAsync<IOException>(() => OpenStreamAsync(dht, stations, new DirectDial.RequestAt<string, string>((host, _, _) =>
            Task.FromException<string>(new IOException($"{host} failed after STREAM_OPEN may have gone out")))));

        Assert.Contains("a.test", e.Message);
        Assert.Equal(new[] { "a.test" }, stations.Reached);
    }

    [Fact]
    public async Task Get_direct_retries_when_no_provider_qualifies()
    {
        var p = new Provider("p.test");
        var mcid = NewMcid();
        var dht = new FakeDht();
        dht.Answer(RecordFactory.ContentKey(mcid), [], [Announcement(p, mcid)]);
        var stations = new FakeStations();

        var content = await GetAsync(dht, stations, mcid, Roomy);

        Assert.Equal(Content, content);
        Assert.Equal(new[] { "p.test" }, stations.Reached);
    }

    [Fact]
    public async Task Get_direct_tries_the_next_provider_after_a_failed_fetch()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var mcid = NewMcid();
        var dht = new FakeDht();
        dht.Answer(RecordFactory.ContentKey(mcid), [Announcement(a, mcid), Announcement(b, mcid)]);
        var stations = new FakeStations();

        var content = await GetAsync(dht, stations, mcid, Roomy, new DirectDial.RequestAt<string, byte[]>((host, _, _) =>
            host == a.Host ? Task.FromException<byte[]>(HashMismatch()) : Task.FromResult(Content)));

        Assert.Equal(Content, content);
        Assert.Equal(new[] { "a.test", "b.test" }, stations.Reached);
    }

    [Fact]
    public async Task Get_direct_timeout_bounds_resolution()
    {
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DirectDial.ContentNotAnnouncedException>(() => GetAsync(new FakeDht(), new FakeStations(), NewMcid(), Short));

        AssertReturnedWithin(ShortBound, clock);
    }

    [Fact]
    public async Task Put_direct_timeout_bounds_the_endpoint_lookup()
    {
        var station = KeyPair.Generate();
        var stations = new FakeStations();
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DirectDial.StationEndpointNotFoundException>(() => DirectDial.ReachStationCoreAsync(
            new FakeDht().Lookups, station.NodeId(), stations.Dial, new DirectDial.RequestAt<string, byte[]>((_, _, _) => Task.FromResult(NewMcid())), Short, CancellationToken.None));

        AssertReturnedWithin(ShortBound, clock);
        Assert.Empty(stations.Reached);
    }

    [Fact]
    public async Task Call_with_cert_chain_tries_the_next_advertisement_when_a_station_has_no_endpoint()
    {
        var ca = TestCa();
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [AuthorizedAdvertisement(a, ca, Org), AuthorizedAdvertisement(b, ca, Org)]);
        dht.PublishEndpoint(b.Station, StationEndpoint(b.Station, b.Host));
        var stations = new FakeStations();

        var response = await CallWithCertChainAsync(dht, stations, ca.Pem, Roomy);

        Assert.Equal("reply from b.test", ReplyText(response));
        Assert.Equal(new[] { "b.test" }, stations.Reached);
    }

    [Fact]
    public async Task Call_with_cert_chain_reports_the_authorization_failure_at_its_deadline()
    {
        var ca = TestCa();
        var a = new Provider("a.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [AuthorizedAdvertisement(a, ca, "other-org")]);
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));
        var stations = new FakeStations();
        var clock = Stopwatch.StartNew();

        var e = await Assert.ThrowsAsync<DirectDial.NoAuthorizedAdvertisementException>(() => CallWithCertChainAsync(dht, stations, ca.Pem, Short));

        AssertReturnedWithin(ShortBound, clock);
        Assert.IsType<CertChain.CertChainOrgMismatchException>(e.InnerException);
        Assert.Empty(stations.Reached);
    }

    [Fact]
    public async Task Call_skips_a_station_whose_endpoint_is_signed_by_another_key()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a), Advertisement(b)]);
        dht.PublishEndpoint(a.Station, StationEndpoint(KeyPair.Generate(), a.Host));
        dht.PublishEndpoint(b.Station, StationEndpoint(b.Station, b.Host));
        var stations = new FakeStations();

        var response = await CallAsync(dht, stations, Roomy);

        Assert.Equal("reply from b.test", ReplyText(response));
        Assert.Equal(new[] { "b.test" }, stations.Reached);
    }

    [Fact]
    public async Task Call_dials_a_refusing_station_once_per_endpoint_version()
    {
        var a = new Provider("a.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a)]);
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));
        var stations = new FakeStations();
        stations.Refuse(a.Host);

        var call = CallAsync(dht, stations, Roomy);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "a.test" }, stations.Reached);

        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));
        await Assert.ThrowsAsync<IOException>(() => call);

        Assert.Equal(new[] { "a.test", "a.test" }, stations.Reached);
    }

    [Fact]
    public async Task Call_tries_an_advertisement_that_appears_on_a_later_pass()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var adA = Advertisement(a);
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [adA], [adA, Advertisement(b)]);
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));
        dht.PublishEndpoint(b.Station, StationEndpoint(b.Station, b.Host));
        var stations = new FakeStations();
        stations.Refuse(a.Host);

        var response = await CallAsync(dht, stations, Roomy);

        Assert.Equal("reply from b.test", ReplyText(response));
        Assert.Equal(new[] { "a.test", "b.test" }, stations.Reached);
    }

    [Fact]
    public async Task Resolution_backs_off_between_passes()
    {
        var dht = new FakeDht();

        await Assert.ThrowsAsync<DirectDial.ProcedureNotAdvertisedException>(() => CallAsync(dht, new FakeStations(), Roomy));

        var asked = dht.AskedAt(ProcedureKey);
        Assert.True(asked.Count is >= 5 and <= 8, $"expected 5 to 8 lookups, got {asked.Count} at ms {string.Join(", ", asked)}");
    }

    [Fact]
    public async Task Get_direct_fetches_from_a_failing_provider_once_per_announcement()
    {
        var p = new Provider("p.test");
        var mcid = NewMcid();
        var dht = new FakeDht();
        dht.Answer(RecordFactory.ContentKey(mcid), [Announcement(p, mcid)]);
        var fetches = 0;
        var failVerification = new DirectDial.RequestAt<string, byte[]>((_, _, _) =>
        {
            Interlocked.Increment(ref fetches);
            return Task.FromException<byte[]>(HashMismatch());
        });

        await Assert.ThrowsAsync<ContentTransfer.ContentTransferException>(() => GetAsync(dht, new FakeStations(), mcid, TimeSpan.FromSeconds(2), failVerification));

        Assert.Equal(1, fetches);
    }

    [Fact]
    public async Task Call_picks_up_an_endpoint_record_that_changes_mid_deadline()
    {
        var a = new Provider("a.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a)]);
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, "a-old.test"));
        var stations = new FakeStations();
        stations.Refuse("a-old.test");

        var call = CallAsync(dht, stations, Roomy);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));

        Assert.Equal("reply from a.test", ReplyText(await call));
        Assert.Equal(new[] { "a-old.test", "a.test" }, stations.Reached);
    }

    [Fact]
    public async Task Resolve_timeout_bounds_resolution()
    {
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DirectDial.ProcedureNotAdvertisedException>(() =>
            DirectDial.ResolveCoreAsync(new FakeDht().Lookups, Realm, Procedure, null, Short, CancellationToken.None));

        AssertReturnedWithin(ShortBound, clock);
    }

    [Fact]
    public async Task Resolve_station_endpoint_timeout_bounds_the_lookup()
    {
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DirectDial.StationEndpointNotFoundException>(() =>
            DirectDial.ResolveStationEndpointCoreAsync(new FakeDht().Lookups, KeyPair.Generate().NodeId(), Short, CancellationToken.None));

        AssertReturnedWithin(ShortBound, clock);
    }

    [Fact]
    public void A_candidate_share_splits_what_remains_evenly_with_a_one_second_floor()
    {
        var slack = TimeSpan.FromMilliseconds(100);
        var roomy = DirectDial.CallDeadline.Start(TimeSpan.FromSeconds(3));
        var tight = DirectDial.CallDeadline.Start(TimeSpan.FromMilliseconds(300));

        Assert.InRange(roomy.ShareFor(2).Remaining, TimeSpan.FromSeconds(1.5) - slack, TimeSpan.FromSeconds(1.5));
        Assert.InRange(roomy.ShareFor(10).Remaining, TimeSpan.FromSeconds(1) - slack, TimeSpan.FromSeconds(1));
        Assert.InRange(tight.ShareFor(3).Remaining, TimeSpan.FromMilliseconds(300) - slack, TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public async Task A_timeout_that_is_not_positive_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CallAsync(new FakeDht(), new FakeStations(), TimeSpan.Zero));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CallAsync(new FakeDht(), new FakeStations(), Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Get_direct_timeout_during_a_transfer_carries_the_last_failure()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var mcid = NewMcid();
        var dht = new FakeDht();
        dht.Answer(RecordFactory.ContentKey(mcid), [Announcement(a, mcid), Announcement(b, mcid)]);
        var failThenHang = new DirectDial.RequestAt<string, byte[]>(async (host, _, ct) =>
        {
            if (host == a.Host)
            {
                throw HashMismatch();
            }
            await Task.Delay(Timeout.Infinite, ct);
            return Content;
        });

        var e = await Assert.ThrowsAsync<TimeoutException>(() => GetAsync(dht, new FakeStations(), mcid, TimeSpan.FromSeconds(1), failThenHang));

        Assert.IsType<ContentTransfer.ContentTransferException>(e.InnerException);
    }

    private static Task<CallResponse> CallAsync(FakeDht dht, FakeStations stations, TimeSpan timeout, DirectDial.RequestAt<string, CallResponse>? request = null) =>
        DirectDial.ReachProcedureCoreAsync(dht.Lookups, Realm, Procedure, null, stations.Dial, request ?? new DirectDial.RequestAt<string, CallResponse>(Reply), timeout, CancellationToken.None);

    private static Task<CallResponse> CallWithCertChainAsync(FakeDht dht, FakeStations stations, byte[] realmCaPem, TimeSpan timeout) =>
        DirectDial.ReachProcedureCoreAsync(dht.Lookups, Realm, Procedure, new DirectDial.CertChainCheck(realmCaPem, Org), stations.Dial, new DirectDial.RequestAt<string, CallResponse>(Reply), timeout, CancellationToken.None);

    private static Task<string> OpenStreamAsync(FakeDht dht, FakeStations stations, DirectDial.RequestAt<string, string> open) =>
        DirectDial.ReachProcedureCoreAsync(dht.Lookups, Realm, Procedure, null, stations.Dial, open, Roomy, CancellationToken.None);

    private static Task<byte[]> GetAsync(FakeDht dht, FakeStations stations, byte[] mcid, TimeSpan timeout, DirectDial.RequestAt<string, byte[]>? fetch = null) =>
        DirectDial.FetchContentCoreAsync(dht.Lookups, mcid, stations.Dial, fetch ?? new DirectDial.RequestAt<string, byte[]>((_, _, _) => Task.FromResult(Content)), timeout, CancellationToken.None);

    private static Task<CallResponse> Reply(string host, TimeSpan remaining, CancellationToken ct) =>
        Task.FromResult<CallResponse>(new CallResponse.Result(Value.Text($"reply from {host}"), new byte[32]));

    private static string ReplyText(CallResponse response) => Assert.IsType<CallResponse.Result>(response).Payload.AsText();

    private static ContentTransfer.ContentTransferException HashMismatch() =>
        new(ContentTransfer.RemoteReason.HashMismatch, "fetched content does not hash to its MCID");

    private static void AssertReturnedWithin(TimeSpan bound, Stopwatch clock) =>
        Assert.True(clock.Elapsed < bound, $"expected to return within {bound}, took {clock.Elapsed}");

    private static (FakeDht Dht, Provider A, Provider B) TwoProvidersWithEndpoints()
    {
        var a = new Provider("a.test");
        var b = new Provider("b.test");
        var dht = new FakeDht();
        dht.Answer(ProcedureKey, [Advertisement(a), Advertisement(b)]);
        dht.PublishEndpoint(a.Station, StationEndpoint(a.Station, a.Host));
        dht.PublishEndpoint(b.Station, StationEndpoint(b.Station, b.Host));
        return (dht, a, b);
    }

    /// <summary>A provider: its own advertisement signer (the DHT keeps one record per signer) and the station serving it at Host.</summary>
    private sealed class Provider(string host)
    {
        public string Host { get; } = host;
        public KeyPair Advertiser { get; } = KeyPair.Generate();
        public KeyPair Station { get; } = KeyPair.Generate();
    }

    private static DhtRecord Advertisement(Provider p, long expiresInMs = 120_000)
    {
        var rec = RecordFactory.NewProcedureAdvertisement(p.Advertiser.NodeId(), RecordFactory.DiscoveryUri(Realm, Procedure), p.Station.NodeId(), TimeSpan.FromMinutes(2));
        return RecordFactory.Sign(new DhtRecord
        {
            Type = rec.Type,
            Key = rec.Key,
            Version = rec.Version,
            CreatedAt = rec.CreatedAt,
            ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiresInMs,
            Payload = rec.Payload,
        }, p.Advertiser);
    }

    private static DhtRecord AuthorizedAdvertisement(Provider p, (byte[] Pem, X509Certificate Cert, AsymmetricKeyParameter Priv) ca, string org)
    {
        var leaf = TestLeaf(ca.Cert, ca.Priv, p.Advertiser.PublicBytes(), org, DateTime.UtcNow.AddHours(1));
        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(p.Advertiser.NodeId(), RecordFactory.DiscoveryUri(Realm, Procedure), p.Station.NodeId(), TimeSpan.FromMinutes(2), PemBundle(leaf));
        return RecordFactory.Sign(rec, p.Advertiser);
    }

    /// <summary>A station_endpoint advertising host, signed by signer -- normally the station itself. Each call is a new record version.</summary>
    private static DhtRecord StationEndpoint(KeyPair signer, string host)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return RecordFactory.Sign(new DhtRecord
        {
            Type = RecordTypes.StationEndpoint,
            Key = signer.NodeId(),
            Version = Envelope.FreshFrameId(),
            CreatedAt = now,
            ExpiresAt = now + 600_000,
            Payload = Value.Map(new List<KeyValuePair<Value, Value>>
            {
                new(Value.Text("quic_port"), Value.UInt(4433)),
                new(Value.Text("host_advertised"), Value.List(new[] { Value.Bytes(Encoding.UTF8.GetBytes(host)) })),
            }),
        }, signer);
    }

    private static DhtRecord Announcement(Provider p, byte[] mcid) =>
        RecordFactory.Sign(RecordFactory.NewContentAnnouncement(p.Station.NodeId(), mcid, $"https://{p.Host}:4433", TimeSpan.FromMinutes(2)), p.Station);

    private static byte[] NewMcid()
    {
        var mcid = new byte[34];
        RandomNumberGenerator.Fill(mcid);
        return mcid;
    }

    /// <summary>
    /// A DHT that answers FindRecords on a key with successive replies,
    /// repeating the last, and FindRecord with the station_endpoint published
    /// under that key (not_found otherwise). A test may publish while a call
    /// is running.
    /// </summary>
    private sealed class FakeDht
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, List<IReadOnlyList<DhtRecord>>> _replies = new();
        private readonly Dictionary<string, List<long>> _asked = new();
        private readonly Dictionary<string, DhtRecord> _endpoints = new();

        public DirectDial.DhtLookups Lookups => new(FindRecordsAsync, FindRecordAsync);

        public void Answer(byte[] key, IReadOnlyList<DhtRecord> first, params IReadOnlyList<DhtRecord>[] later)
        {
            lock (_gate)
            {
                _replies[Convert.ToHexString(key)] = [first, .. later];
            }
        }

        public void PublishEndpoint(KeyPair station, DhtRecord endpoint)
        {
            lock (_gate)
            {
                _endpoints[Convert.ToHexString(RecordFactory.StationEndpointKey(station.NodeId()))] = endpoint;
            }
        }

        /// <summary>When FindRecords was asked for key, in milliseconds since this DHT was created.</summary>
        public IReadOnlyList<long> AskedAt(byte[] key)
        {
            lock (_gate)
            {
                return _asked.TryGetValue(Convert.ToHexString(key), out var times) ? times.ToList() : [];
            }
        }

        private Task<IReadOnlyList<DhtRecord>> FindRecordsAsync(byte[] key, CancellationToken ct)
        {
            lock (_gate)
            {
                var hex = Convert.ToHexString(key);
                if (!_asked.TryGetValue(hex, out var times))
                {
                    times = _asked[hex] = [];
                }
                times.Add(_clock.ElapsedMilliseconds);
                IReadOnlyList<DhtRecord> reply = _replies.TryGetValue(hex, out var replies)
                    ? replies[Math.Min(times.Count - 1, replies.Count - 1)]
                    : Array.Empty<DhtRecord>();
                return Task.FromResult(reply);
            }
        }

        private Task<DhtRecord> FindRecordAsync(byte[] key, CancellationToken ct)
        {
            lock (_gate)
            {
                return _endpoints.TryGetValue(Convert.ToHexString(key), out var rec)
                    ? Task.FromResult(rec)
                    : Task.FromException<DhtRecord>(new DhtClient.NotFoundException());
            }
        }
    }

    /// <summary>
    /// Fake dials: remembers every host it is asked to reach, in order, and
    /// refuses the hosts marked as refusing before anything is sent.
    /// </summary>
    private sealed class FakeStations
    {
        private readonly object _gate = new();
        private readonly List<string> _reached = new();
        private readonly HashSet<string> _refusing = new();

        public IReadOnlyList<string> Reached
        {
            get
            {
                lock (_gate)
                {
                    return _reached.ToList();
                }
            }
        }

        public DirectDial.DialVerified<string> Dial => (station, _, _) =>
        {
            lock (_gate)
            {
                _reached.Add(station.Host);
                return _refusing.Contains(station.Host)
                    ? Task.FromException<string>(new IOException($"{station.Host} refused the connection"))
                    : Task.FromResult(station.Host);
            }
        };

        public void Refuse(string host)
        {
            lock (_gate)
            {
                _refusing.Add(host);
            }
        }
    }
}
