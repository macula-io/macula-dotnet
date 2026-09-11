using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Dht;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Tests;

/// <summary>
/// Proves DirectDial.CallWithUcanAsync actually reaches a UCAN-gated
/// procedure that plain DirectDial.CallAsync cannot -- the gap this
/// function closes (PLAN_CLOSE_SERVICE_AUTH_GAPS.md Phase 0,
/// macula-io/macula-architecture): every hecate-om capability is
/// advertised via AdvertiseDirectAsync, and until this function existed,
/// nothing in this SDK could attach a token to a direct-dial call at all --
/// a `ucan_required` capability was reachable in name only.
///
/// Same fleet-flakiness caveat as <see cref="DirectDialTests"/> and
/// <see cref="UcanGatedServeTests"/>. Separate identities per role
/// throughout.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class DirectDialUcanTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Direct_dial_ucan_gated_refuses_unauthorized_and_answers_authorized()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var callerId = KeyPair.GenerateWithDefaultPuzzle();
        var issuerId = KeyPair.GenerateWithDefaultPuzzle();
        var wrongIssuerId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.direct_dial_ucan_gated_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        await DirectDial.AdvertiseDirectAsync(provider, providerId, realm, procedure, TimeSpan.FromHours(1));

        var policy = Policy.Required(issuerId.PublicBytes());
        CallLookup lookup = (_, proc) => proc != procedure
            ? null
            : payload => Task.FromResult(payload);
        PolicyLookup policyLookup = (_, proc) => proc == procedure ? policy : Policy.Open;

        // 1. Unauthorized: plain CallAsync cannot even attach a token.
        var serve1 = Task.Run(() => provider.ServeOneCallGatedAsync(lookup, policyLookup, TimeSpan.FromSeconds(15)));
        await using (var resolver1 = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki))
        {
            var response = await DirectDial.CallAsync(resolver1, callerId, realm, procedure, Value.Text("no token"), TimeSpan.FromSeconds(12));
            var error = Assert.IsType<CallResponse.Error>(response);
            Assert.Equal("unauthorized", error.Name);
        }
        await serve1;

        // 2. Wrong issuer: CallWithUcanAsync exists and attaches a token,
        // but the token's issuer doesn't match what the procedure requires.
        var serve2 = Task.Run(() => provider.ServeOneCallGatedAsync(lookup, policyLookup, TimeSpan.FromSeconds(15)));
        var callerAudience = Convert.ToHexStringLower(callerId.NodeId());
        var wrongIssuerToken = UcanToken.Create("did:macula:wrong-issuer", callerAudience, Array.Empty<UcanToken.Capability>(), wrongIssuerId);
        await using (var resolver2 = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki))
        {
            var response = await DirectDial.CallWithUcanAsync(resolver2, callerId, realm, procedure, Value.Text("wrong issuer"), TimeSpan.FromSeconds(12), wrongIssuerToken);
            var error = Assert.IsType<CallResponse.Error>(response);
            Assert.Equal("unauthorized", error.Name);
        }
        await serve2;

        // 3. Authorized: the actual fix under test. The token names this
        // caller as its audience, as a gated provider requires.
        var serve3 = Task.Run(() => provider.ServeOneCallGatedAsync(lookup, policyLookup, TimeSpan.FromSeconds(15)));
        var validToken = UcanToken.Create("did:macula:test-issuer", callerAudience, Array.Empty<UcanToken.Capability>(), issuerId);
        await using (var resolver3 = await Session.ConnectAsync(StationHost, StationPort, callerId, Connection.Trust.UseWebPki))
        {
            var response = await DirectDial.CallWithUcanAsync(resolver3, callerId, realm, procedure, Value.Text("hello gated direct-dial"), TimeSpan.FromSeconds(12), validToken);
            var result = Assert.IsType<CallResponse.Result>(response);
            Assert.Equal("hello gated direct-dial", result.Payload.AsText());
        }
        await serve3;
    }
}
