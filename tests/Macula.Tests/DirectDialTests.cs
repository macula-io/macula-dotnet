using System.Linq;
using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Dht;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Tests;

/// <summary>
/// Direct-dial live tests. Same fleet-flakiness caveat as
/// <see cref="LiveStationTests"/>. Uses SEPARATE identities per role
/// (provider vs caller) throughout -- this fleet enforces one connection
/// per identity and kicks whichever connects second, a real self-inflicted
/// bug already found and fixed in this SDK's Go/Rust siblings this same
/// session.
///
/// <see cref="Direct_dial_serve_round_trip_gets_a_real_result"/> depends on
/// the resolved station's own station_endpoint record being fresh (5min
/// TTL) -- an already-documented external macula-station republish-cadence
/// gap affecting every SDK in this family, not a defect here. Verified for
/// real against 2 of 5 fleet stations tried in one sitting (Falkenstein,
/// Stockholm both clean passes with a real RESULT payload; Frankfurt,
/// Milan, Helsinki all hit StationEndpointNotFoundException at that
/// moment) -- an occasional failure here is expected fleet noise, not a
/// regression.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class DirectDialTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    /// <summary>
    /// The real bar this session settled on for direct-dial correctness:
    /// an actual RESULT payload, not just "reached the call stage" -- the
    /// weaker check that hid a real AdvertiseDirect bug in this SDK's Go
    /// sibling (it published only the DHT record, never the plain
    /// ADVERTISE, so a resolved+dialed station had nothing to route the
    /// CALL to).
    /// </summary>
    [Fact]
    public async Task Direct_dial_serve_round_trip_gets_a_real_result()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var callerId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.direct_dial_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        await DirectDial.AdvertiseDirectAsync(provider, providerId, realm, procedure, TimeSpan.FromHours(1));

        var served = Task.Run(async () =>
        {
            Value received = Value.Null;
            CallLookup lookup = (_, proc) => proc != procedure
                ? null
                : payload =>
                {
                    received = payload;
                    return Task.FromResult(payload);
                };
            await provider.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(20));
            return received;
        });

        await using var resolver = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki);
        var response = await DirectDial.CallAsync(resolver, callerId, realm, procedure, Value.Text("hello direct-dial"), TimeSpan.FromSeconds(15));

        var result = Assert.IsType<CallResponse.Result>(response);
        Assert.Equal("hello direct-dial", result.Payload.AsText());

        var handlerSaw = await served;
        Assert.Equal("hello direct-dial", handlerSaw.AsText());
    }

    /// <summary>
    /// A direct call to a provider on the resolver's own station runs on the
    /// resolver's session instead of dialing a second connection under the
    /// same identity, which the station would answer by closing the resolver.
    /// Reuse needs no station_endpoint lookup, so stale endpoint records on
    /// the fleet don't affect this one.
    /// </summary>
    [Fact]
    public async Task Resolver_session_still_connected_after_a_direct_call()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var callerId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.direct_dial_reuse_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        await DirectDial.AdvertiseDirectAsync(provider, providerId, realm, procedure, TimeSpan.FromHours(1));
        CallLookup lookup = (_, proc) => proc != procedure ? null : payload => Task.FromResult(payload);
        var served = Task.Run(() => provider.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(20)));

        await using var resolver = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki);
        // Reuse needs the resolver on the provider's own station.
        Assert.True(resolver.RemoteInfo.NodeId.AsSpan().SequenceEqual(provider.RemoteInfo.NodeId),
            $"the resolver reached station {Convert.ToHexStringLower(resolver.RemoteInfo.NodeId)} and the provider {Convert.ToHexStringLower(provider.RemoteInfo.NodeId)}");
        var response = await DirectDial.CallAsync(resolver, callerId, realm, procedure, Value.Text("hello again"), TimeSpan.FromSeconds(15));
        Assert.Equal("hello again", Assert.IsType<CallResponse.Result>(response).Payload.AsText());
        await served;

        // A second connection under callerId would have made the station close
        // the resolver by now.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(resolver.Ended.IsCompleted);
        await DirectDial.ResolveAsync(resolver, realm, procedure);
    }

    [Fact]
    public async Task Resolve_reports_not_advertised_for_an_unadvertised_procedure()
    {
        var callerId = KeyPair.GenerateWithDefaultPuzzle();
        await using var resolver = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki);

        await Assert.ThrowsAsync<DirectDial.ProcedureNotAdvertisedException>(() =>
            DirectDial.ResolveAsync(resolver, new byte[32], $"macula_dotnet_sdk.never_advertised.{Guid.NewGuid():N}"));
    }

    /// <summary>
    /// Verifies re-publish directly via the DHT record's own CreatedAt,
    /// matching this session's Go sibling's own test design for the same
    /// property, rather than by serving a call on the provider session while
    /// the KeepAdvertisedDirectAsync loop runs. Both can share one session;
    /// the record's CreatedAt is the direct evidence that a re-publish
    /// happened.
    /// </summary>
    [Fact]
    public async Task Keep_advertised_direct_republishes_and_stops_on_cancellation()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var verifierId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.keep_advertised_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        // A SEPARATE session/identity for verification reads -- checking
        // the DHT record on the SAME session the re-advertise loop is
        // ticking on would hit the identical concurrent-read conflict this
        // test's own doc comment describes.
        await using var verifier = await Session.ConnectAsync(StationHost, StationPort, verifierId, Connection.Trust.UseWebPki);

        using var cts = new CancellationTokenSource();
        var loopTask = DirectDial.KeepAdvertisedDirectAsync(provider, providerId, realm, procedure, TimeSpan.FromHours(1), TimeSpan.FromSeconds(1), null, cts.Token);

        var uri = RecordFactory.DiscoveryUri(realm, procedure);
        var key = RecordFactory.ProcedureKey(uri);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var afterTick1 = (await DhtClient.FindRecordsAsync(verifier, key)).Single();

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var afterTick2 = (await DhtClient.FindRecordsAsync(verifier, key)).Single();

        Assert.True(afterTick2.CreatedAt > afterTick1.CreatedAt, "expected a genuine re-publish, not a silent no-op");

        cts.Cancel();
        try
        {
            await loopTask;
        }
        catch (OperationCanceledException)
        {
            // expected
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var afterCancel = (await DhtClient.FindRecordsAsync(verifier, key)).Single();
        Assert.Equal(afterTick2.CreatedAt, afterCancel.CreatedAt);
    }
}
