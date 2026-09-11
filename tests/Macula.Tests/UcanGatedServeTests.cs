using System.Runtime.Versioning;
using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Tests;

/// <summary>
/// Live proof that UCAN policy gating works over the real wire: an
/// unauthorized call is refused with BOLT#4 Unauthorized BEFORE any handler
/// code runs, and an authorized call reaches it and gets a real reply.
/// Same fleet-flakiness caveat as <see cref="LiveStationTests"/>. Separate
/// identities per role throughout.
/// </summary>
[Trait("Category", "Live")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public class UcanGatedServeTests
{
    private const string StationHost = "station-de-frankfurt.macula.io";
    private const int StationPort = 4433;

    [Fact]
    public async Task Gated_serve_refuses_unauthorized_and_answers_authorized()
    {
        var providerId = KeyPair.GenerateWithDefaultPuzzle();
        var requiredIssuerId = KeyPair.GenerateWithDefaultPuzzle();
        var authorizedCallerId = KeyPair.GenerateWithDefaultPuzzle();
        var unauthorizedCallerId = KeyPair.GenerateWithDefaultPuzzle();
        var procedure = $"macula_dotnet_sdk.ucan_gated_test.{Guid.NewGuid():N}";
        var realm = new byte[32];

        await using var provider = await Session.ConnectAsync(StationHost, StationPort, providerId, Connection.Trust.UseWebPki);
        await provider.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerId.NodeId() });

        var policy = Policy.Required(requiredIssuerId.PublicBytes());
        var handlerInvoked = false;

        var serveUnauthorized = Task.Run(async () =>
        {
            CallLookup lookup = (_, proc) => proc != procedure ? null : _ =>
            {
                handlerInvoked = true;
                return Task.FromResult(Value.Null);
            };
            PolicyLookup policyLookup = (_, proc) => proc == procedure ? policy : Policy.Open;
            await provider.ServeOneCallGatedAsync(lookup, policyLookup, TimeSpan.FromSeconds(15));
        });

        await using (var caller = await Session.ConnectAsync(StationHost, StationPort, unauthorizedCallerId, Connection.Trust.UseWebPki))
        {
            var response = await caller.CallAsync(procedure, realm, Value.Null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, TimeSpan.FromSeconds(10));
            var error = Assert.IsType<CallResponse.Error>(response);
            Assert.Equal("unauthorized", error.Name);
        }
        await serveUnauthorized;
        Assert.False(handlerInvoked, "the handler must never run for a call the policy rejects");

        // Second round: a fresh serve, this time with a valid token.
        var serveAuthorized = Task.Run(async () =>
        {
            CallLookup lookup = (_, proc) => proc != procedure ? null : payload =>
            {
                handlerInvoked = true;
                return Task.FromResult(payload);
            };
            PolicyLookup policyLookup = (_, proc) => proc == procedure ? policy : Policy.Open;
            await provider.ServeOneCallGatedAsync(lookup, policyLookup, TimeSpan.FromSeconds(15));
        });

        var token = UcanToken.Create("did:macula:test-issuer", Convert.ToHexStringLower(authorizedCallerId.NodeId()), Array.Empty<UcanToken.Capability>(), requiredIssuerId);
        await using (var caller = await Session.ConnectAsync(StationHost, StationPort, authorizedCallerId, Connection.Trust.UseWebPki))
        {
            var response = await caller.CallWithUcanAsync(procedure, realm, Value.Text("42"), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000, TimeSpan.FromSeconds(10), token);
            var result = Assert.IsType<CallResponse.Result>(response);
            Assert.Equal("42", result.Payload.AsText());
        }
        await serveAuthorized;
        Assert.True(handlerInvoked, "the handler must run for a call the policy accepts");
    }
}
