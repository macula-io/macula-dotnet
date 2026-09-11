using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using Macula.Cbor;
using Macula.Connection;
using Macula.Content;
using Macula.Frame;
using Macula.Identity;
using Macula.Streaming;

namespace Macula.Dht;

/// <summary>
/// Macula's direct-dial resolve-and-call: resolving a signed
/// procedure_advertisement DHT record and its serving station's own signed
/// station_endpoint, then dialing that station in one hop -- instead of
/// depending on ordinary advertise-gossip having propagated a route between
/// whichever two stations happen to be involved. Ported from
/// macula-io/macula's macula_direct_dial.erl via macula-go's own
/// directdial/directdial.go.
///
/// Trust model: every candidate procedure_advertisement must carry a valid
/// Ed25519 signature before its serving_station is trusted at all, and the
/// resolved station_endpoint must be signed by the station itself. The
/// actual QUIC dial trusts neither the TLS certificate (a production
/// station's TLS is terminated by an unrelated PKI) nor nothing -- trust is
/// enforced at the application layer, by checking the freshly dialed
/// session's own signature-verified HELLO identity against the exact
/// pubkey the signed DHT chain resolved.
///
/// Candidates: every advertisement (or content announcement) that verifies
/// is a candidate, tried in the order the DHT returned them. A candidate
/// whose endpoint record doesn't resolve, whose dial fails, or whose dialed
/// identity doesn't match is skipped for the next one, because nothing has
/// reached the provider yet. Once a CALL or STREAM_OPEN has gone out, its
/// result is the call's result and it is never sent again. When no
/// candidate qualifies, or every one failed before sending, the DHT is
/// queried again with a backoff of 100 ms doubling to 1 s; within one call
/// a station that already failed is dialed again only once its
/// advertisement or endpoint record has changed. The call's timeout bounds
/// all of it, and each candidate gets a share of what remains for its
/// endpoint lookup and dial. At the deadline, the most recent candidate
/// failure is thrown as it was raised; a later query that finds nothing, or
/// fails, never replaces it. When no candidate was ever tried, the reason
/// none qualified is thrown instead.
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public static class DirectDial
{
    // Matches macula_direct_dial.erl's ?RESOLVE_RETRY_MS -- a record just
    // published on the provider's station has not necessarily replicated to
    // the resolving station yet, so the first miss is not treated as
    // failure. The endpoint lookup retries at this cadence within a
    // candidate's share, and re-queries start at it.
    private static readonly TimeSpan ResolveRetryDelay = TimeSpan.FromMilliseconds(100);

    // Re-queries back off from ResolveRetryDelay, doubling up to this cap, so
    // a call waiting out a missing or refusing provider doesn't keep loading
    // the DHT.
    private static readonly TimeSpan MaxRequeryPause = TimeSpan.FromSeconds(1);

    // Every candidate gets at least this much of the remaining time for its
    // endpoint lookup and dial, or all of it when less than this remains.
    private static readonly TimeSpan MinCandidateShare = TimeSpan.FromSeconds(1);

    // The time budget ResolveAsync, ResolveWithCertChainAsync and
    // ResolveStationEndpointAsync get, since none takes a timeout of its
    // own. Matches macula-go's DefaultResolveTimeout.
    private static readonly TimeSpan DefaultResolveTimeout = TimeSpan.FromSeconds(10);

    public sealed class ProcedureNotAdvertisedException : Exception
    {
        public ProcedureNotAdvertisedException() : base("directdial: procedure has no direct-dial advertisement in the DHT") { }
    }

    public sealed class NoTrustedAdvertisementException : Exception
    {
        public NoTrustedAdvertisementException() : base("directdial: every candidate advertisement failed signature verification") { }
    }

    public sealed class StationEndpointNotFoundException : Exception
    {
        public StationEndpointNotFoundException() : base("directdial: resolved station published no reachable station_endpoint") { }
    }

    /// <summary>No candidate advertisement is cert-chain-authorized for the expected org -- at least one candidate's envelope signature verified, but none passed CertChainVerification.Verify.</summary>
    public sealed class NoAuthorizedAdvertisementException : Exception
    {
        public NoAuthorizedAdvertisementException(Exception? inner) : base("directdial: no candidate advertisement is cert-chain-authorized for the expected org", inner) { }
    }

    public sealed class TrustViolationException : Exception
    {
        public TrustViolationException(string message) : base(message) { }
    }

    public sealed record Resolved(byte[] Station, string Host, ushort Port);

    /// <summary>
    /// The two DHT lookups direct-dial resolution makes, as delegates, so
    /// the resolution logic runs unchanged against a fake DHT in tests.
    /// </summary>
    internal sealed record DhtLookups(
        Func<byte[], CancellationToken, Task<IReadOnlyList<Record>>> FindRecords,
        Func<byte[], CancellationToken, Task<Record>> FindRecord)
    {
        internal static DhtLookups Via(Session resolveVia) => new(
            (key, ct) => DhtClient.FindRecordsAsync(resolveVia, key, ct),
            (key, ct) => DhtClient.FindRecordAsync(resolveVia, key, ct));
    }

    /// <summary>Dials a resolved station and checks its HELLO identity, throwing if either fails. Nothing has reached the provider yet when this throws.</summary>
    internal delegate Task<TTarget> DialVerified<TTarget>(Resolved station, TimeSpan timeout, CancellationToken ct);

    /// <summary>Sends the request to a dialed station, and owns that connection from then on.</summary>
    internal delegate Task<T> RequestAt<TTarget, T>(TTarget target, TimeSpan remaining, CancellationToken ct);

    /// <summary>The realm CA and org an advertisement's cert chain must satisfy, on the *WithCertChain paths.</summary>
    internal sealed record CertChainCheck(byte[] RealmCaPem, string ExpectedOrg);

    /// <summary>
    /// One call's time budget, on a monotonic clock. It covers resolution,
    /// every endpoint lookup, every dial and the request itself.
    /// </summary>
    internal readonly struct CallDeadline
    {
        private readonly long _startedAt;
        private readonly TimeSpan _budget;

        private CallDeadline(TimeSpan budget)
        {
            _startedAt = Stopwatch.GetTimestamp();
            _budget = budget;
        }

        internal static CallDeadline Start(TimeSpan timeout)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
            return new CallDeadline(timeout);
        }

        // Task.Delay and CancelAfter work in whole milliseconds, so less than
        // one left counts as none; otherwise the last fraction of a
        // millisecond turns every pause into zero and the loop spins.
        internal TimeSpan Remaining
        {
            get
            {
                var left = _budget - Stopwatch.GetElapsedTime(_startedAt);
                return left >= TimeSpan.FromMilliseconds(1) ? left : TimeSpan.Zero;
            }
        }

        internal bool Passed => Remaining == TimeSpan.Zero;

        /// <summary>
        /// The next candidate's share of what remains, for its endpoint
        /// lookup and dial: an even split across the candidates not yet
        /// tried, but never less than MinCandidateShare unless less than
        /// that remains.
        /// </summary>
        internal CallDeadline ShareFor(int untried)
        {
            var remaining = Remaining;
            var even = remaining / untried;
            var floor = remaining < MinCandidateShare ? remaining : MinCandidateShare;
            return new CallDeadline(even > floor ? even : floor);
        }

        internal CancellationTokenSource LinkedTo(CancellationToken ct)
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Remaining);
            return cts;
        }
    }

    /// <summary>
    /// Finds procedure's currently-advertised serving station and its
    /// dialable host/port, retrying past DHT propagation lag for up to 10
    /// seconds. realm and procedure must match exactly what the provider
    /// passed to AdvertiseDirectAsync -- the discovery URI they derive must
    /// agree. resolveVia is used only to query the DHT; it does not need to
    /// be connected to the same station that will end up serving the call.
    /// The first candidate whose station endpoint resolves is returned.
    /// </summary>
    public static Task<Resolved> ResolveAsync(Session resolveVia, byte[] realm, string procedure, CancellationToken ct = default) =>
        ResolveCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, null, DefaultResolveTimeout, ct);

    internal static Task<Resolved> ResolveCoreAsync(DhtLookups dht, byte[] realm, string procedure, CertChainCheck? certChain, TimeSpan timeout, CancellationToken ct) =>
        ReachProcedureCoreAsync(dht, realm, procedure, certChain, KeepResolved, ReturnResolved, timeout, ct);

    // Resolution alone: the "dial" and the "request" hand the resolved
    // endpoint straight back.
    private static readonly DialVerified<Resolved> KeepResolved = (station, _, _) => Task.FromResult(station);
    private static readonly RequestAt<Resolved, Resolved> ReturnResolved = (station, _, _) => Task.FromResult(station);

    internal static Task<T> ReachProcedureCoreAsync<TTarget, T>(DhtLookups dht, byte[] realm, string procedure, CertChainCheck? certChain, DialVerified<TTarget> dial, RequestAt<TTarget, T> request, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = CallDeadline.Start(timeout);
        return FirstAnswerAsync(ProcedureCandidates(dht, realm, procedure, certChain), ThroughStationEndpoint(dht, dial, request, deadline), deadline, ct);
    }

    // One DHT query's candidates, in DHT order, and the error to report if
    // none of them qualifies.
    private sealed record Pass<TCandidate>(IReadOnlyList<TCandidate> Candidates, Exception Unresolved);

    // A qualified advertisement: its record (whose signer and version
    // identify the candidate) and the station serving it.
    private sealed record ProcedureCandidate(Record Advertisement, byte[] Station);

    // A qualified content announcement: its record and its parsed fields.
    private sealed record ContentCandidate(Record Announcement, ContentAnnouncement Provider);

    // A candidate that failed before sending, within one call: the version
    // of the record that made it a candidate, the endpoint record version it
    // failed on (null when none was found), and the error.
    private sealed record RememberedFailure(byte[] RecordVersion, byte[]? EndpointVersion, Exception Error);

    // What trying one candidate came to: an answer, or a failure after which
    // the next candidate may be tried. A failure after the request went out
    // is thrown instead.
    private readonly record struct Attempt<T>(T? Answer, Exception? Failure)
    {
        internal static Attempt<T> Answered(T answer) => new(answer, null);
        internal static Attempt<T> Failed(Exception failure) => new(default, failure);
    }

    // The loop every resolving shape shares: query, try the candidates in
    // DHT order until one answers, and query again after a capped backoff
    // until the deadline. Once a candidate has failed before sending, the
    // most recent candidate failure is what the call throws, as it was
    // raised: a later query that finds nothing, or fails, never replaces
    // it. Until then, the latest query's reason for finding no candidate is.
    private static async Task<T> FirstAnswerAsync<TCandidate, T>(
        Func<CallDeadline, CancellationToken, Task<Pass<TCandidate>>> query,
        Func<TCandidate, CallDeadline, Exception?, CancellationToken, Task<Attempt<T>>> attempt,
        CallDeadline deadline,
        CancellationToken ct)
    {
        Exception? candidateFailure = null;
        Exception? unresolved = null;
        var pause = ResolveRetryDelay;
        while (true)
        {
            var pass = await query(deadline, ct).ConfigureAwait(false);
            // A query the deadline cut short learned nothing, so it doesn't
            // replace a reason already seen.
            if (pass.Candidates.Count == 0 && !(deadline.Passed && unresolved is not null))
            {
                unresolved = pass.Unresolved;
            }
            for (var i = 0; i < pass.Candidates.Count && !deadline.Passed; i++)
            {
                var outcome = await attempt(pass.Candidates[i], deadline.ShareFor(pass.Candidates.Count - i), candidateFailure ?? unresolved, ct).ConfigureAwait(false);
                if (outcome.Failure is null)
                {
                    return outcome.Answer!;
                }
                candidateFailure = outcome.Failure;
            }
            if (deadline.Passed)
            {
                break;
            }
            await Task.Delay(pause < deadline.Remaining ? pause : deadline.Remaining, ct).ConfigureAwait(false);
            pause = pause * 2 < MaxRequeryPause ? pause * 2 : MaxRequeryPause;
            if (deadline.Passed)
            {
                break;
            }
        }
        ExceptionDispatchInfo.Throw(candidateFailure ?? unresolved ?? new TimeoutException("directdial: the timeout ran out before any candidate could be tried"));
        throw new UnreachableException();
    }

    private static Func<CallDeadline, CancellationToken, Task<Pass<ProcedureCandidate>>> ProcedureCandidates(DhtLookups dht, byte[] realm, string procedure, CertChainCheck? certChain)
    {
        var key = RecordFactory.ProcedureKey(RecordFactory.DiscoveryUri(realm, procedure));
        return async (deadline, ct) =>
        {
            IReadOnlyList<Record> recs;
            using (var within = deadline.LinkedTo(ct))
            {
                try
                {
                    recs = await dht.FindRecords(key, within.Token).ConfigureAwait(false);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // a failed or cut-off query counts as no records yet
                    recs = Array.Empty<Record>();
                }
            }
            if (recs.Count == 0)
            {
                return new Pass<ProcedureCandidate>(Array.Empty<ProcedureCandidate>(), new ProcedureNotAdvertisedException());
            }
            return certChain is null ? TrustedAdvertisements(recs) : AuthorizedAdvertisements(recs, certChain);
        };
    }

    // Every advertisement whose signature and expiry verify and whose
    // payload parses, in DHT order.
    private static Pass<ProcedureCandidate> TrustedAdvertisements(IReadOnlyList<Record> recs)
    {
        var candidates = new List<ProcedureCandidate>();
        foreach (var rec in recs)
        {
            if (RecordFactory.Verify(rec) is not null)
            {
                continue;
            }
            try
            {
                candidates.Add(new ProcedureCandidate(rec, RecordReading.ReadProcedureAdvertisement(rec).ServingStation));
            }
            catch (Exception)
            {
                // malformed payload -- not a candidate
            }
        }
        return new Pass<ProcedureCandidate>(candidates, new NoTrustedAdvertisementException());
    }

    /// <summary>
    /// ResolveAsync plus Slice 7c Direction B managed-realm authorization:
    /// only an advertisement whose embedded cert chain validates to
    /// realmCaPem and names expectedOrg is trusted. Opt-in -- ResolveAsync
    /// itself is unaffected and remains the right choice for unmanaged
    /// realms.
    /// </summary>
    public static Task<Resolved> ResolveWithCertChainAsync(Session resolveVia, byte[] realm, string procedure, byte[] realmCaPem, string expectedOrg, CancellationToken ct = default) =>
        ResolveCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, new CertChainCheck(realmCaPem, expectedOrg), DefaultResolveTimeout, ct);

    // TrustedAdvertisements plus the cert-chain check. When none qualifies,
    // the error is NoAuthorizedAdvertisementException carrying the most
    // recent cert-chain failure, or NoTrustedAdvertisementException if every
    // candidate failed the plain signature check instead, matching
    // ResolveAsync's own distinction.
    private static Pass<ProcedureCandidate> AuthorizedAdvertisements(IReadOnlyList<Record> recs, CertChainCheck certChain)
    {
        var candidates = new List<ProcedureCandidate>();
        Exception? lastError = null;
        foreach (var rec in recs)
        {
            try
            {
                CertChain.VerifyAdvertisementCertChain(certChain.RealmCaPem, rec, certChain.ExpectedOrg);
            }
            catch (CertChain.CertChainBadSignatureException)
            {
                continue;
            }
            catch (Exception e)
            {
                lastError = e;
                continue;
            }
            try
            {
                candidates.Add(new ProcedureCandidate(rec, RecordReading.ReadProcedureAdvertisement(rec).ServingStation));
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }
        Exception unresolved = lastError is null ? new NoTrustedAdvertisementException() : new NoAuthorizedAdvertisementException(lastError);
        return new Pass<ProcedureCandidate>(candidates, unresolved);
    }

    // Tries one advertised station: its endpoint lookup and dial within the
    // candidate's share, then the request with whatever remains of the
    // deadline. A failure before the request is remembered with the endpoint
    // version it failed on. On a later pass, an unchanged advertisement gets
    // a single endpoint lookup, and the station is dialed again only if that
    // lookup shows a different endpoint version; a lookup that got no answer
    // teaches nothing and keeps the remembered failure.
    private static Func<ProcedureCandidate, CallDeadline, Exception?, CancellationToken, Task<Attempt<T>>> ThroughStationEndpoint<TTarget, T>(DhtLookups dht, DialVerified<TTarget> dial, RequestAt<TTarget, T> request, CallDeadline deadline)
    {
        var failures = new Dictionary<string, RememberedFailure>();
        return async (candidate, share, _, ct) =>
        {
            var signer = Convert.ToHexString(candidate.Advertisement.Key);
            var remembered = failures.GetValueOrDefault(signer);
            var retrying = remembered is not null && remembered.RecordVersion.AsSpan().SequenceEqual(candidate.Advertisement.Version);
            var lookup = await LookupStationEndpointAsync(dht, candidate.Station, share, retryWithinBudget: !retrying, ct).ConfigureAwait(false);
            if (retrying && (!lookup.Answered || SameVersion(lookup.SeenVersion, remembered!.EndpointVersion)))
            {
                return Attempt<T>.Failed(remembered!.Error);
            }
            if (lookup.Failure is not null)
            {
                failures[signer] = new RememberedFailure(candidate.Advertisement.Version, lookup.SeenVersion, lookup.Failure);
                return Attempt<T>.Failed(lookup.Failure);
            }
            TTarget target;
            try
            {
                target = await dial(lookup.Target!, share.Remaining, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failures[signer] = new RememberedFailure(candidate.Advertisement.Version, lookup.SeenVersion, e);
                return Attempt<T>.Failed(e);
            }
            return Attempt<T>.Answered(await request(target, deadline.Remaining, ct).ConfigureAwait(false));
        };
    }

    private static bool SameVersion(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    /// <summary>CallAsync, resolved via ResolveWithCertChainAsync instead of ResolveAsync -- see both for the full contract. Opt-in managed-realm authorization; CallAsync itself is unaffected.</summary>
    public static Task<CallResponse> CallWithCertChainAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, byte[] realmCaPem, string expectedOrg, Value payload, TimeSpan timeout, CancellationToken ct = default) =>
        ReachProcedureCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, new CertChainCheck(realmCaPem, expectedOrg), DialerFor(identity),
            ClosingAfter<CallResponse>((target, remaining, c) => target.CallAsync(procedure, realm, payload, WireDeadlineMs(remaining), remaining, c)),
            timeout, ct);

    /// <summary>AdvertiseDirectAsync plus embedding a service-cert chain (leaf-first PEM: leaf ++ org CA) for Slice 7c Direction B authorization. Opt-in; AdvertiseDirectAsync itself is unaffected.</summary>
    public static async Task AdvertiseDirectWithCertChainAsync(Session session, KeyPair identity, byte[] realm, string procedure, TimeSpan ttl, byte[] certChainPem, CancellationToken ct = default)
    {
        var spec = new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = identity.NodeId() };
        await session.AdvertiseAsync(spec, ct).ConfigureAwait(false);

        var uri = RecordFactory.DiscoveryUri(realm, procedure);
        var rec = RecordFactory.NewProcedureAdvertisementWithCertChain(identity.NodeId(), uri, session.RemoteInfo.NodeId, ttl, certChainPem);
        rec = RecordFactory.Sign(rec, identity);
        await DhtClient.PutRecordAsync(session, rec, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves an arbitrary known station's dialable host/port from its own
    /// signed station_endpoint record -- the same lookup ResolveAsync
    /// performs internally after finding a procedure_advertisement, but
    /// exported for callers that already know WHICH station they want
    /// (content PUT-direct: content has no "procedure" to advertise).
    ///
    /// Retries past a resolved-but-stale record, not just an absent one, for
    /// up to 10 seconds -- the DHT can hand back a replica that hasn't been
    /// evicted yet even though the station's own current publish is live.
    /// </summary>
    public static Task<Resolved> ResolveStationEndpointAsync(Session resolveVia, byte[] station, CancellationToken ct = default) =>
        ResolveStationEndpointCoreAsync(DhtLookups.Via(resolveVia), station, DefaultResolveTimeout, ct);

    internal static async Task<Resolved> ResolveStationEndpointCoreAsync(DhtLookups dht, byte[] station, TimeSpan timeout, CancellationToken ct)
    {
        var lookup = await LookupStationEndpointAsync(dht, station, CallDeadline.Start(timeout), retryWithinBudget: true, ct).ConfigureAwait(false);
        if (lookup.Failure is not null)
        {
            ExceptionDispatchInfo.Throw(lookup.Failure);
        }
        return lookup.Target!;
    }

    // One station_endpoint lookup within budget: the resolved endpoint or
    // why it failed, the version of the last record the DHT returned, and
    // whether the DHT answered at all (a record or not_found) rather than
    // failing or being cut off.
    private sealed record EndpointLookup(Resolved? Target, Exception? Failure, byte[]? SeenVersion, bool Answered);

    // With retryWithinBudget, an absent or expired record is looked up again
    // at ResolveRetryDelay until the budget runs out.
    private static async Task<EndpointLookup> LookupStationEndpointAsync(DhtLookups dht, byte[] station, CallDeadline budget, bool retryWithinBudget, CancellationToken ct)
    {
        var key = RecordFactory.StationEndpointKey(station);
        byte[]? seen = null;
        var answered = false;
        while (true)
        {
            Record? rec = null;
            using (var within = budget.LinkedTo(ct))
            {
                try
                {
                    rec = await dht.FindRecord(key, within.Token).ConfigureAwait(false);
                    answered = true;
                }
                catch (DhtClient.NotFoundException)
                {
                    answered = true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // cut off by the budget: nothing learned
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    return new EndpointLookup(null, e, seen, answered);
                }
            }

            if (rec is not null)
            {
                seen = rec.Version;
                // The station_endpoint record for `station` must be SIGNED BY
                // `station` itself -- checking the signature and that the
                // signer is exactly `station`, not just any valid signature,
                // is what makes pinning the dial's expected identity
                // meaningful.
                if (!rec.Key.AsSpan().SequenceEqual(station))
                {
                    return new EndpointLookup(null, new TrustViolationException("directdial: station_endpoint signer mismatch"), seen, answered);
                }
                var verr = RecordFactory.Verify(rec);
                if (verr is null)
                {
                    return ReadEndpoint(station, rec);
                }
                if (verr != RecordFactory.VerifyError.Expired)
                {
                    return new EndpointLookup(null, new NoTrustedAdvertisementException(), seen, answered);
                }
            }
            if (!retryWithinBudget || budget.Passed)
            {
                return new EndpointLookup(null, new StationEndpointNotFoundException(), seen, answered);
            }
            await Task.Delay(ResolveRetryDelay < budget.Remaining ? ResolveRetryDelay : budget.Remaining, ct).ConfigureAwait(false);
        }
    }

    private static EndpointLookup ReadEndpoint(byte[] station, Record rec)
    {
        StationEndpoint ep;
        try
        {
            ep = RecordReading.ReadStationEndpoint(rec);
        }
        catch (Exception e)
        {
            return new EndpointLookup(null, e, rec.Version, true);
        }
        return ep.HostAdvertised.Count == 0
            ? new EndpointLookup(null, new StationEndpointNotFoundException(), rec.Version, true)
            : new EndpointLookup(new Resolved(station, ep.HostAdvertised[0], ep.QuicPort), null, rec.Version, true);
    }

    /// <summary>
    /// The shared second half of every direct-dial call shape: dial
    /// host:port and check the freshly connected session's own
    /// signature-verified HELLO identity against station.
    /// </summary>
    private static async Task<Session> DialAndVerifyAsync(string host, ushort port, byte[] station, KeyPair identity, TimeSpan timeout, CancellationToken ct)
    {
        var target = await Session.ConnectAsync(host, port, identity, Trust.Unsafe, timeout, ct).ConfigureAwait(false);
        if (!target.RemoteInfo.NodeId.AsSpan().SequenceEqual(station))
        {
            await target.CloseAsync().ConfigureAwait(false);
            throw new TrustViolationException(
                $"directdial: trust violation -- resolved station {Convert.ToHexStringLower(station)} but the dialed peer proved identity {Convert.ToHexStringLower(target.RemoteInfo.NodeId)}");
        }
        return target;
    }

    // Dials within the candidate's share. A dial the share cuts off fails
    // with TimeoutException, the same as a handshake that runs out of time.
    private static DialVerified<Session> DialerFor(KeyPair identity) =>
        async (station, timeout, ct) =>
        {
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(timeout);
            try
            {
                return await DialAndVerifyAsync(station.Host, station.Port, station.Station, identity, timeout, within.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"directdial: dialing {station.Host}:{station.Port} did not finish within {timeout}");
            }
        };

    // A request that closes its dialed session once it finishes, whatever
    // the outcome -- every shape except a stream, which hands the session to
    // the caller.
    private static RequestAt<Session, T> ClosingAfter<T>(RequestAt<Session, T> request) =>
        async (target, remaining, ct) =>
        {
            try
            {
                return await request(target, remaining, ct).ConfigureAwait(false);
            }
            finally
            {
                await target.CloseAsync().ConfigureAwait(false);
            }
        };

    private static long WireDeadlineMs(TimeSpan remaining) =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)remaining.TotalMilliseconds;

    /// <summary>
    /// Resolves procedure's provider via direct-dial (through resolveVia,
    /// which is used only to query the DHT) and calls it there, in one hop,
    /// in a SEPARATE connection from resolveVia. The provider must have
    /// advertised via AdvertiseDirectAsync -- a plain AdvertiseAsync
    /// publishes no discoverable record and the call will throw
    /// ProcedureNotAdvertisedException.
    ///
    /// timeout bounds the whole call: finding the provider, each candidate's
    /// endpoint lookup and dial, and the CALL itself. See the type doc's
    /// "Candidates" for how providers are tried in turn.
    ///
    /// The dial itself uses Trust.Unsafe (no TLS verification) because
    /// trust is enforced at the application layer instead -- see the type
    /// doc's "Trust model".
    /// </summary>
    public static Task<CallResponse> CallAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, Value payload, TimeSpan timeout, CancellationToken ct = default) =>
        ReachProcedureCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, null, DialerFor(identity),
            ClosingAfter<CallResponse>((target, remaining, c) => target.CallAsync(procedure, realm, payload, WireDeadlineMs(remaining), remaining, c)),
            timeout, ct);

    /// <summary>
    /// CallAsync, presenting ucanToken to a provider gated with
    /// <see cref="Policy.Required"/>. Every hecate-om capability is
    /// advertised via AdvertiseDirectAsync, so this is the only way this
    /// SDK can reach a UCAN-gated capability at all -- CallAsync itself
    /// has no token parameter, and Session.CallWithUcanAsync is the
    /// plain, non-direct path, which cannot resolve a direct-dial-only
    /// advertisement to begin with.
    /// </summary>
    public static Task<CallResponse> CallWithUcanAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, Value payload, TimeSpan timeout, byte[] ucanToken, CancellationToken ct = default) =>
        ReachProcedureCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, null, DialerFor(identity),
            ClosingAfter<CallResponse>((target, remaining, c) => target.CallWithUcanAsync(procedure, realm, payload, WireDeadlineMs(remaining), remaining, ucanToken, c)),
            timeout, ct);

    /// <summary>
    /// Publishes a signed procedure_advertisement naming session's own
    /// currently-connected station as procedure's server, discoverable by
    /// any caller's ResolveAsync/CallAsync. Mirrors
    /// macula_response:advertise_direct/6,7: it calls plain AdvertiseAsync
    /// FIRST and only then publishes the DHT record -- both, not either.
    /// Without the plain advertise, a caller that resolves this station via
    /// the DHT record and dials it directly reaches a station with no
    /// ordinary ADVERTISE registration to route the CALL to, so
    /// ServeOneCallAsync never sees it (a real bug found and fixed live in
    /// this SDK's Go/Rust siblings this same session -- ported here
    /// correctly from the start).
    ///
    /// Registers no handler of its own and does not keep anything alive
    /// across calls. A station's registration for a procedure does not
    /// survive the connection that sent it being replaced, so a long-lived
    /// server needs to call this again on its own schedule --
    /// see <see cref="KeepAdvertisedDirectAsync"/>.
    ///
    /// Both steps run on the ONE session passed in, so this must not be
    /// called on a session another task is serving with
    /// <see cref="Session.ServeOneCallAsync"/> at the same time: the
    /// put_record CALL's RESULT frame is consumed by that serve loop and
    /// the put times out (seen live 2026-09-03 in macula-cli's Go daemon,
    /// which did exactly that). A provider that is already serving should
    /// advertise on its serving session and publish the record
    /// (<see cref="RecordFactory.NewProcedureAdvertisement"/> +
    /// <see cref="RecordFactory.Sign"/> + <see cref="DhtClient.PutRecordAsync"/>)
    /// on a second session -- the same "second Session" rule
    /// <see cref="Session.ServeOneCallAsync"/>'s own doc gives.
    /// </summary>
    public static async Task AdvertiseDirectAsync(Session session, KeyPair identity, byte[] realm, string procedure, TimeSpan ttl, CancellationToken ct = default)
    {
        var spec = new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = identity.NodeId() };
        await session.AdvertiseAsync(spec, ct).ConfigureAwait(false);

        var uri = RecordFactory.DiscoveryUri(realm, procedure);
        var rec = RecordFactory.NewProcedureAdvertisement(identity.NodeId(), uri, session.RemoteInfo.NodeId, ttl);
        rec = RecordFactory.Sign(rec, identity);
        await DhtClient.PutRecordAsync(session, rec, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls AdvertiseDirectAsync immediately, then again every interval,
    /// until ct is cancelled. This is the "call this again on its own
    /// schedule" loop AdvertiseDirectAsync's own doc says a long-lived
    /// server needs. interval should leave real margin before ttl expires
    /// -- production practice in hecate-om's own capability re-advertise
    /// loop uses a 4x margin: a 30s republish interval against a 120s
    /// record TTL.
    ///
    /// A failed tick (network blip, connection genuinely dead, etc.) is
    /// reported via onError (null is fine -- the error is simply dropped)
    /// but does NOT stop the loop; it tries again at the next interval
    /// regardless. This loop cannot detect or repair a dead Session on its
    /// own -- if session's underlying connection has actually gone down,
    /// every tick will keep failing the same way until ct is cancelled.
    ///
    /// Same session rule as <see cref="AdvertiseDirectAsync"/>: run this
    /// loop on a session nothing else is serving on, never alongside a
    /// <see cref="Session.ServeOneCallAsync"/> loop on the same session.
    /// </summary>
    public static async Task KeepAdvertisedDirectAsync(Session session, KeyPair identity, byte[] realm, string procedure, TimeSpan ttl, TimeSpan interval, Action<Exception>? onError, CancellationToken ct)
    {
        async Task TickAsync()
        {
            try
            {
                await AdvertiseDirectAsync(session, identity, realm, procedure, ttl, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (onError is not null)
            {
                onError(e);
            }
        }

        await TickAsync().ConfigureAwait(false);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await TickAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    /// <summary>
    /// Resolves procedure's provider via direct-dial and opens a stream
    /// there, in one hop, in a SEPARATE connection from resolveVia -- the
    /// streaming-RPC counterpart to CallAsync. The provider must have
    /// advertised via AdvertiseDirectAsync: streaming's provider side
    /// (macula_streamer.erl) shares the identical procedure_advertisement
    /// mechanism RPC uses -- confirmed against the Erlang reference, no
    /// separate stream-shaped advertise exists or is needed.
    ///
    /// timeout bounds finding the provider, each candidate's endpoint lookup
    /// and dial, and opening the stream; deadlineMs is the stream's own
    /// deadline, sent to the provider. A failure once the station is dialed
    /// is never retried elsewhere, because STREAM_OPEN may already be out.
    ///
    /// The caller owns the returned Session (and must close it once the
    /// stream and any other work on it is done) alongside the StreamHandle
    /// itself, since -- unlike CallAsync, which owns its dial for exactly
    /// one request/reply -- a stream outlives the single call that opens it.
    /// </summary>
    public static Task<(Session Session, StreamHandle Stream)> OpenStreamDirectAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, StreamMode mode, Value args, long deadlineMs, TimeSpan timeout, CancellationToken ct = default) =>
        ReachProcedureCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, null, DialerFor(identity), OpenStream(identity, realm, procedure, mode, args, deadlineMs), timeout, ct);

    /// <summary>OpenStreamDirectAsync, resolved via ResolveWithCertChainAsync instead of ResolveAsync -- see both for the full contract. Opt-in managed-realm authorization; OpenStreamDirectAsync itself is unaffected.</summary>
    public static Task<(Session Session, StreamHandle Stream)> OpenStreamDirectWithCertChainAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, byte[] realmCaPem, string expectedOrg, StreamMode mode, Value args, long deadlineMs, TimeSpan timeout, CancellationToken ct = default) =>
        ReachProcedureCoreAsync(DhtLookups.Via(resolveVia), realm, procedure, new CertChainCheck(realmCaPem, expectedOrg), DialerFor(identity), OpenStream(identity, realm, procedure, mode, args, deadlineMs), timeout, ct);

    // Opens the stream on the dialed session and hands both to the caller;
    // closes the session only if the open itself fails.
    private static RequestAt<Session, (Session Session, StreamHandle Stream)> OpenStream(KeyPair identity, byte[] realm, string procedure, StreamMode mode, Value args, long deadlineMs) =>
        async (target, _, ct) =>
        {
            try
            {
                var handle = await StreamHandle.OpenAsync(target, procedure, realm, mode, args, deadlineMs, identity, ct).ConfigureAwait(false);
                return (target, handle);
            }
            catch (Exception)
            {
                await target.CloseAsync().ConfigureAwait(false);
                throw;
            }
        };

    /// <summary>
    /// Stores data at a KNOWN station directly, in one hop, instead of
    /// going through whatever station resolveVia happens to be connected
    /// to. Mirrors macula_feeder:start_link_direct/5,6, which -- unlike
    /// procedure/stream direct-dial -- takes the target station's pubkey
    /// directly rather than resolving one via a procedure_advertisement:
    /// content has no "procedure" to advertise.
    ///
    /// timeout bounds the station's endpoint lookup and the dial; the upload
    /// itself runs under ct alone.
    ///
    /// Caveat found live in this SDK's Go sibling: if resolveVia happens to
    /// already be connected to station, this call's own internal dial
    /// reuses identity against the SAME station resolveVia is on -- this
    /// fleet enforces one connection per identity and kicks whichever
    /// connects second, so resolveVia's own connection can be closed out
    /// from under the caller. Use a different identity for resolveVia than
    /// for identity if the caller needs resolveVia to keep working
    /// afterward against that same station.
    /// </summary>
    public static Task<byte[]> PutDirectAsync(Session resolveVia, KeyPair identity, byte[] station, byte[] data, string name, TimeSpan timeout, CancellationToken ct = default) =>
        ReachStationCoreAsync(DhtLookups.Via(resolveVia), station, DialerFor(identity),
            ClosingAfter<byte[]>((target, _, c) => ContentTransfer.PutAsync(target, data, name, identity, c)),
            timeout, ct);

    internal static async Task<T> ReachStationCoreAsync<TTarget, T>(DhtLookups dht, byte[] station, DialVerified<TTarget> dial, RequestAt<TTarget, T> request, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = CallDeadline.Start(timeout);
        var lookup = await LookupStationEndpointAsync(dht, station, deadline, retryWithinBudget: true, ct).ConfigureAwait(false);
        if (lookup.Failure is not null)
        {
            ExceptionDispatchInfo.Throw(lookup.Failure);
        }
        var target = await dial(lookup.Target!, deadline.Remaining, ct).ConfigureAwait(false);
        return await request(target, deadline.Remaining, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches and verifies the content addressed by mcid from whichever
    /// station a signed content_announcement names as its host, dialing
    /// that station in one hop instead of relaying through resolveVia's own
    /// station. Mirrors macula_direct_dial:get_content/3.
    ///
    /// timeout bounds the whole fetch: finding providers, each dial and each
    /// transfer. A provider whose dial or transfer fails, including content
    /// that doesn't verify against mcid, is skipped for the next one. When
    /// the timeout cuts a transfer off, the TimeoutException carries the
    /// previous failure as its InnerException.
    ///
    /// Architectural note this type's other direct-dial functions don't
    /// need: a content_announcement's endpoint is the FINAL dial target
    /// directly -- unlike procedure_advertisement, there is no
    /// station-relay indirection, so the announcer must genuinely BE
    /// independently dialable there. A plain outbound-only leaf (everything
    /// this SDK's own identity/session model supports) cannot legitimately
    /// publish one of these about itself -- only something with its own
    /// listening identity (macula-station, or a dedicated content-serving
    /// relay) can. This SDK therefore does not expose a client-facing
    /// "AnnounceContentDirect": RecordFactory.NewContentAnnouncement stays a
    /// low-level primitive for that kind of infrastructure-tier code, not
    /// ordinary leaf use (matching the Go/Rust siblings' identical, already
    /// live-verified, choice). GetDirectAsync itself has no such
    /// limitation -- resolving and fetching FROM an already-announced
    /// provider is a perfectly ordinary leaf operation.
    /// </summary>
    public static Task<byte[]> GetDirectAsync(Session resolveVia, KeyPair identity, byte[] mcid, TimeSpan timeout, CancellationToken ct = default) =>
        FetchContentCoreAsync(DhtLookups.Via(resolveVia), mcid, DialerFor(identity),
            ClosingAfter<byte[]>((target, _, c) => ContentTransfer.GetAsync(target, mcid, identity, c)),
            timeout, ct);

    internal static Task<T> FetchContentCoreAsync<TTarget, T>(DhtLookups dht, byte[] mcid, DialVerified<TTarget> dial, RequestAt<TTarget, T> fetch, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = CallDeadline.Start(timeout);
        return FirstAnswerAsync(ContentProviders(dht, mcid), FromProvider(dial, fetch, deadline), deadline, ct);
    }

    /// <summary>mcid has no live, verifiable content_announcement in the DHT.</summary>
    public sealed class ContentNotAnnouncedException : Exception
    {
        public ContentNotAnnouncedException() : base("directdial: content has no verifiable announcement in the DHT") { }
    }

    private static Func<CallDeadline, CancellationToken, Task<Pass<ContentCandidate>>> ContentProviders(DhtLookups dht, byte[] mcid)
    {
        var key = RecordFactory.ContentKey(mcid);
        return async (deadline, ct) =>
        {
            using var within = deadline.LinkedTo(ct);
            IReadOnlyList<Record> recs;
            try
            {
                recs = await dht.FindRecords(key, within.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                recs = Array.Empty<Record>();
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // the lookup's own error is the one to report if nothing better turns up
                return new Pass<ContentCandidate>(Array.Empty<ContentCandidate>(), e);
            }
            return TrustedContentProviders(recs);
        };
    }

    // Mirrors macula.erl's decode_provider/1: the record's OWN signature
    // must verify, AND the payload's claimed announcer_node must equal the
    // record's own envelope key -- a record merely stored under the right
    // key but self-signed by a different identity would otherwise still be
    // trusted.
    private static Pass<ContentCandidate> TrustedContentProviders(IReadOnlyList<Record> recs)
    {
        var candidates = new List<ContentCandidate>();
        foreach (var rec in recs)
        {
            if (RecordFactory.Verify(rec) is not null)
            {
                continue;
            }
            ContentAnnouncement adv;
            try
            {
                adv = RecordReading.ReadContentAnnouncement(rec);
            }
            catch (Exception)
            {
                continue;
            }
            if (!adv.AnnouncerNode.AsSpan().SequenceEqual(rec.Key))
            {
                continue;
            }
            candidates.Add(new ContentCandidate(rec, adv));
        }
        return new Pass<ContentCandidate>(candidates, new ContentNotAnnouncedException());
    }

    // Tries one content provider: its dial within the candidate's share,
    // then the transfer with whatever remains of the deadline. Any failure
    // moves on to the next provider, since a fetch is verified against its
    // MCID and safe to repeat elsewhere; a provider that failed is skipped on
    // later passes unless its announcement changed.
    private static Func<ContentCandidate, CallDeadline, Exception?, CancellationToken, Task<Attempt<T>>> FromProvider<TTarget, T>(DialVerified<TTarget> dial, RequestAt<TTarget, T> fetch, CallDeadline deadline)
    {
        var failures = new Dictionary<string, RememberedFailure>();
        return async (candidate, share, lastFailure, ct) =>
        {
            var announcer = Convert.ToHexString(candidate.Announcement.Key);
            if (failures.TryGetValue(announcer, out var remembered) && remembered.RecordVersion.AsSpan().SequenceEqual(candidate.Announcement.Version))
            {
                return Attempt<T>.Failed(remembered.Error);
            }
            try
            {
                var (host, port) = ParseSeedUrl(candidate.Provider.Endpoint);
                var target = await dial(new Resolved(candidate.Provider.AnnouncerNode, host, port), share.Remaining, ct).ConfigureAwait(false);
                using var transfer = deadline.LinkedTo(ct);
                try
                {
                    return Attempt<T>.Answered(await fetch(target, deadline.Remaining, transfer.Token).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("directdial: the timeout ran out during the content transfer", lastFailure);
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                failures[announcer] = new RememberedFailure(candidate.Announcement.Version, null, e);
                return Attempt<T>.Failed(e);
            }
        };
    }

    // A content_announcement's endpoint is a dialable seed URL (e.g.
    // "https://host:4433"), distinct from station_endpoint's already-split
    // host_advertised/quic_port fields.
    private static (string Host, ushort Port) ParseSeedUrl(string seed)
    {
        if (Uri.TryCreate(seed, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            if (uri.Port <= 0)
            {
                throw new FormatException($"endpoint has no port: {seed}");
            }
            return (uri.Host, (ushort)uri.Port);
        }
        // No scheme/authority at all -- try it as a bare host:port instead
        // of failing outright.
        var idx = seed.LastIndexOf(':');
        if (idx <= 0 || idx == seed.Length - 1)
        {
            throw new FormatException($"not a URL or host:port: {seed}");
        }
        var host = seed[..idx];
        if (!ushort.TryParse(seed[(idx + 1)..], out var port))
        {
            throw new FormatException($"invalid port in: {seed}");
        }
        return (host, port);
    }
}
