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
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public static class DirectDial
{
    // Matches macula_direct_dial.erl's ?RESOLVE_RETRIES/?RESOLVE_RETRY_MS --
    // a record just published on the provider's station has not
    // necessarily replicated to the resolving station yet, so the first
    // miss is not treated as failure.
    private const int ResolveRetries = 50;
    private static readonly TimeSpan ResolveRetryDelay = TimeSpan.FromMilliseconds(100);

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
    /// Finds procedure's currently-advertised serving station and its
    /// dialable host/port, retrying past DHT propagation lag. realm and
    /// procedure must match exactly what the provider passed to
    /// AdvertiseDirectAsync -- the discovery URI they derive must agree.
    /// resolveVia is used only to query the DHT; it does not need to be
    /// connected to the same station that will end up serving the call.
    /// </summary>
    public static async Task<Resolved> ResolveAsync(Session resolveVia, byte[] realm, string procedure, CancellationToken ct = default)
    {
        var uri = RecordFactory.DiscoveryUri(realm, procedure);
        var key = RecordFactory.ProcedureKey(uri);

        var recs = await FindWithRetryAsync(resolveVia, key, ct).ConfigureAwait(false);
        if (recs.Count == 0)
        {
            throw new ProcedureNotAdvertisedException();
        }

        var adv = FirstTrustedAdvertisement(recs) ?? throw new NoTrustedAdvertisementException();
        return await ResolveStationEndpointAsync(resolveVia, adv.ServingStation, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Record>> FindWithRetryAsync(Session resolveVia, byte[] key, CancellationToken ct)
    {
        for (var attempt = 0; attempt < ResolveRetries; attempt++)
        {
            IReadOnlyList<Record> recs;
            try
            {
                recs = await DhtClient.FindRecordsAsync(resolveVia, key, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                recs = Array.Empty<Record>();
            }
            if (recs.Count > 0)
            {
                return recs;
            }
            await Task.Delay(ResolveRetryDelay, ct).ConfigureAwait(false);
        }
        return Array.Empty<Record>();
    }

    private static ProcedureAdvertisement? FirstTrustedAdvertisement(IReadOnlyList<Record> recs)
    {
        foreach (var rec in recs)
        {
            if (RecordFactory.Verify(rec) is not null)
            {
                continue;
            }
            try
            {
                return RecordReading.ReadProcedureAdvertisement(rec);
            }
            catch (Exception)
            {
                // malformed payload -- try the next candidate
            }
        }
        return null;
    }

    /// <summary>
    /// ResolveAsync plus Slice 7c Direction B managed-realm authorization:
    /// only an advertisement whose embedded cert chain validates to
    /// realmCaPem and names expectedOrg is trusted. Opt-in -- ResolveAsync
    /// itself is unaffected and remains the right choice for unmanaged
    /// realms.
    /// </summary>
    public static async Task<Resolved> ResolveWithCertChainAsync(Session resolveVia, byte[] realm, string procedure, byte[] realmCaPem, string expectedOrg, CancellationToken ct = default)
    {
        var uri = RecordFactory.DiscoveryUri(realm, procedure);
        var key = RecordFactory.ProcedureKey(uri);

        var recs = await FindWithRetryAsync(resolveVia, key, ct).ConfigureAwait(false);
        if (recs.Count == 0)
        {
            throw new ProcedureNotAdvertisedException();
        }

        var (adv, lastError) = FirstAuthorizedAdvertisement(recs, realmCaPem, expectedOrg);
        if (adv is null)
        {
            if (lastError is not null)
            {
                throw new NoAuthorizedAdvertisementException(lastError);
            }
            throw new NoTrustedAdvertisementException();
        }
        return await ResolveStationEndpointAsync(resolveVia, adv.ServingStation, ct).ConfigureAwait(false);
    }

    // FirstTrustedAdvertisement plus the cert-chain check. lastError is the
    // most recent CertChain.VerifyAdvertisementCertChain failure seen (null
    // if every candidate failed the plain signature check instead, in
    // which case the caller should report NoTrustedAdvertisementException,
    // matching ResolveAsync's own distinction).
    private static (ProcedureAdvertisement? Advertisement, Exception? LastError) FirstAuthorizedAdvertisement(IReadOnlyList<Record> recs, byte[] realmCaPem, string expectedOrg)
    {
        Exception? lastError = null;
        foreach (var rec in recs)
        {
            try
            {
                CertChain.VerifyAdvertisementCertChain(realmCaPem, rec, expectedOrg);
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
                return (RecordReading.ReadProcedureAdvertisement(rec), null);
            }
            catch (Exception e)
            {
                lastError = e;
            }
        }
        return (null, lastError);
    }

    /// <summary>CallAsync, resolved via ResolveWithCertChainAsync instead of ResolveAsync -- see both for the full contract. Opt-in managed-realm authorization; CallAsync itself is unaffected.</summary>
    public static async Task<CallResponse> CallWithCertChainAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, byte[] realmCaPem, string expectedOrg, Value payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var resolved = await ResolveWithCertChainAsync(resolveVia, realm, procedure, realmCaPem, expectedOrg, ct).ConfigureAwait(false);
        var target = await DialAndVerifyAsync(resolved.Host, resolved.Port, resolved.Station, identity, timeout, ct).ConfigureAwait(false);
        try
        {
            return await target.CallAsync(procedure, realm, payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            await target.CloseAsync().ConfigureAwait(false);
        }
    }

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
    /// Retries past a resolved-but-stale record, not just an absent one --
    /// the DHT can hand back a replica that hasn't been evicted yet even
    /// though the station's own current publish is live.
    /// </summary>
    public static async Task<Resolved> ResolveStationEndpointAsync(Session resolveVia, byte[] station, CancellationToken ct = default)
    {
        var key = RecordFactory.StationEndpointKey(station);
        for (var attempt = 0; attempt < ResolveRetries; attempt++)
        {
            Record rec;
            try
            {
                rec = await DhtClient.FindRecordAsync(resolveVia, key, ct).ConfigureAwait(false);
            }
            catch (DhtClient.NotFoundException)
            {
                await Task.Delay(ResolveRetryDelay, ct).ConfigureAwait(false);
                continue;
            }

            // The station_endpoint record for `station` must be SIGNED BY
            // `station` itself -- checking the signature and that the
            // signer is exactly `station`, not just any valid signature, is
            // what makes pinning the dial's expected identity meaningful.
            if (!rec.Key.AsSpan().SequenceEqual(station))
            {
                throw new TrustViolationException("directdial: station_endpoint signer mismatch");
            }
            var verr = RecordFactory.Verify(rec);
            if (verr == RecordFactory.VerifyError.Expired)
            {
                await Task.Delay(ResolveRetryDelay, ct).ConfigureAwait(false);
                continue;
            }
            if (verr is not null)
            {
                throw new NoTrustedAdvertisementException();
            }
            var ep = RecordReading.ReadStationEndpoint(rec);
            if (ep.HostAdvertised.Count == 0)
            {
                throw new StationEndpointNotFoundException();
            }
            return new Resolved(station, ep.HostAdvertised[0], ep.QuicPort);
        }
        throw new StationEndpointNotFoundException();
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

    /// <summary>
    /// Resolves procedure's provider via direct-dial (through resolveVia,
    /// which is used only to query the DHT) and calls it there, in one hop,
    /// in a SEPARATE connection from resolveVia. The provider must have
    /// advertised via AdvertiseDirectAsync -- a plain AdvertiseAsync
    /// publishes no discoverable record and ResolveAsync will throw
    /// ProcedureNotAdvertisedException.
    ///
    /// The dial itself uses Trust.Unsafe (no TLS verification) because
    /// trust is enforced at the application layer instead -- see the type
    /// doc's "Trust model".
    /// </summary>
    public static async Task<CallResponse> CallAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, Value payload, TimeSpan timeout, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(resolveVia, realm, procedure, ct).ConfigureAwait(false);
        var target = await DialAndVerifyAsync(resolved.Host, resolved.Port, resolved.Station, identity, timeout, ct).ConfigureAwait(false);
        try
        {
            return await target.CallAsync(procedure, realm, payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds, timeout, ct).ConfigureAwait(false);
        }
        finally
        {
            await target.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// CallAsync, presenting ucanToken to a provider gated with
    /// <see cref="Policy.Required"/>. Every hecate-om capability is
    /// advertised via AdvertiseDirectAsync, so this is the only way this
    /// SDK can reach a UCAN-gated capability at all -- CallAsync itself
    /// has no token parameter, and Session.CallWithUcanAsync is the
    /// plain, non-direct path, which cannot resolve a direct-dial-only
    /// advertisement to begin with.
    /// </summary>
    public static async Task<CallResponse> CallWithUcanAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, Value payload, TimeSpan timeout, byte[] ucanToken, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(resolveVia, realm, procedure, ct).ConfigureAwait(false);
        var target = await DialAndVerifyAsync(resolved.Host, resolved.Port, resolved.Station, identity, timeout, ct).ConfigureAwait(false);
        try
        {
            return await target.CallWithUcanAsync(procedure, realm, payload, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)timeout.TotalMilliseconds, timeout, ucanToken, ct).ConfigureAwait(false);
        }
        finally
        {
            await target.CloseAsync().ConfigureAwait(false);
        }
    }

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
    /// The caller owns the returned Session (and must close it once the
    /// stream and any other work on it is done) alongside the StreamHandle
    /// itself, since -- unlike CallAsync, which owns its dial for exactly
    /// one request/reply -- a stream outlives the single call that opens it.
    /// </summary>
    public static async Task<(Session Session, StreamHandle Stream)> OpenStreamDirectAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, StreamMode mode, Value args, long deadlineMs, TimeSpan timeout, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(resolveVia, realm, procedure, ct).ConfigureAwait(false);
        var target = await DialAndVerifyAsync(resolved.Host, resolved.Port, resolved.Station, identity, timeout, ct).ConfigureAwait(false);
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
    }

    /// <summary>OpenStreamDirectAsync, resolved via ResolveWithCertChainAsync instead of ResolveAsync -- see both for the full contract. Opt-in managed-realm authorization; OpenStreamDirectAsync itself is unaffected.</summary>
    public static async Task<(Session Session, StreamHandle Stream)> OpenStreamDirectWithCertChainAsync(Session resolveVia, KeyPair identity, byte[] realm, string procedure, byte[] realmCaPem, string expectedOrg, StreamMode mode, Value args, long deadlineMs, TimeSpan timeout, CancellationToken ct = default)
    {
        var resolved = await ResolveWithCertChainAsync(resolveVia, realm, procedure, realmCaPem, expectedOrg, ct).ConfigureAwait(false);
        var target = await DialAndVerifyAsync(resolved.Host, resolved.Port, resolved.Station, identity, timeout, ct).ConfigureAwait(false);
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
    }

    /// <summary>
    /// Stores data at a KNOWN station directly, in one hop, instead of
    /// going through whatever station resolveVia happens to be connected
    /// to. Mirrors macula_feeder:start_link_direct/5,6, which -- unlike
    /// procedure/stream direct-dial -- takes the target station's pubkey
    /// directly rather than resolving one via a procedure_advertisement:
    /// content has no "procedure" to advertise.
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
    public static async Task<byte[]> PutDirectAsync(Session resolveVia, KeyPair identity, byte[] station, byte[] data, string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var resolved = await ResolveStationEndpointAsync(resolveVia, station, ct).ConfigureAwait(false);
        var target = await DialAndVerifyAsync(resolved.Host, resolved.Port, resolved.Station, identity, timeout, ct).ConfigureAwait(false);
        try
        {
            return await ContentTransfer.PutAsync(target, data, name, identity, ct).ConfigureAwait(false);
        }
        finally
        {
            await target.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches and verifies the content addressed by mcid from whichever
    /// station a signed content_announcement names as its host, dialing
    /// that station in one hop instead of relaying through resolveVia's own
    /// station. Mirrors macula_direct_dial:get_content/3.
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
    public static async Task<byte[]> GetDirectAsync(Session resolveVia, KeyPair identity, byte[] mcid, TimeSpan timeout, CancellationToken ct = default)
    {
        var recs = await DhtClient.FindRecordsAsync(resolveVia, RecordFactory.ContentKey(mcid), ct).ConfigureAwait(false);
        var adv = FirstTrustedContentProvider(recs) ?? throw new ContentNotAnnouncedException();
        var (host, port) = ParseSeedUrl(adv.Endpoint);
        var target = await DialAndVerifyAsync(host, port, adv.AnnouncerNode, identity, timeout, ct).ConfigureAwait(false);
        try
        {
            return await ContentTransfer.GetAsync(target, mcid, identity, ct).ConfigureAwait(false);
        }
        finally
        {
            await target.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>mcid has no live, verifiable content_announcement in the DHT.</summary>
    public sealed class ContentNotAnnouncedException : Exception
    {
        public ContentNotAnnouncedException() : base("directdial: content has no verifiable announcement in the DHT") { }
    }

    // Mirrors macula.erl's decode_provider/1: the record's OWN signature
    // must verify, AND the payload's claimed announcer_node must equal the
    // record's own envelope key -- a record merely stored under the right
    // key but self-signed by a different identity would otherwise still be
    // trusted.
    private static ContentAnnouncement? FirstTrustedContentProvider(IReadOnlyList<Record> recs)
    {
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
            return adv;
        }
        return null;
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
