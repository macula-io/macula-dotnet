using Macula.Connection;
using Macula.Frame;
using Macula.Identity;
using Macula.Ucan;

namespace Macula.Examples;

/// <summary>
/// UCAN: mint a token, gate a served procedure behind a required issuer
/// via <see cref="Policy.Required"/>, then show both paths -- a call with
/// no token is refused (BOLT#4 Unauthorized) before the handler ever
/// runs, a call with a valid token reaches it. The gate is checked by
/// <see cref="Session.ServeOneCallGatedAsync"/>; plain
/// <see cref="Session.ServeOneCallAsync"/> is a zero-behavior-change
/// delegation to it with an always-open policy, so ordinary procedures
/// are unaffected by UCAN existing at all.
/// </summary>
public static class UcanExample
{
    public static async Task RunAsync()
    {
        var providerIdentity = KeyPair.GenerateWithDefaultPuzzle();
        var callerIdentity = KeyPair.GenerateWithDefaultPuzzle();

        var issuer = $"did:macula:{Convert.ToHexStringLower(callerIdentity.NodeId())}";
        // A gated provider accepts a token only from the caller it names as
        // audience: that caller's node id as lowercase hex, no did: prefix.
        var token = UcanToken.Create(
            issuer,
            Convert.ToHexStringLower(callerIdentity.NodeId()),
            new[] { new UcanToken.Capability("mri:procedure:examples/ucan_gated", "call") },
            callerIdentity);
        Console.WriteLine($"minted token, issuer={issuer}");

        await using var providerSession = await Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki);
        await using var callerSession = await Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki);

        var realm = new byte[32];
        var procedure = $"macula_dotnet_sdk.examples.ucan_gated.{Guid.NewGuid():N}";

        await providerSession.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId() });
        Console.WriteLine($"provider advertised {procedure}, requiring issuer={issuer}");
        await Task.Delay(500);

        CallLookup lookup = (_, proc) => proc == procedure ? (payload => Task.FromResult(payload)) : null;
        PolicyLookup policy = (_, proc) => proc == procedure ? Policy.Required(callerIdentity.NodeId()) : Policy.Open;

        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20_000;

        // First call: no token. Refused by policy before the handler ever runs.
        var unauthorizedServeTask = providerSession.ServeOneCallGatedAsync(lookup, policy, TimeSpan.FromSeconds(15));
        var unauthorizedResponse = await callerSession.CallAsync(procedure, realm, Value.Null, deadlineMs, TimeSpan.FromSeconds(10));
        await unauthorizedServeTask;
        Console.WriteLine($"call without a token: {unauthorizedResponse}");

        // Second call: valid token. Reaches the handler for real.
        var authorizedServeTask = providerSession.ServeOneCallGatedAsync(lookup, policy, TimeSpan.FromSeconds(15));
        var authorizedResponse = await callerSession.CallWithUcanAsync(procedure, realm, Value.Text("hello, gated procedure"), deadlineMs, TimeSpan.FromSeconds(10), token);
        await authorizedServeTask;

        Console.WriteLine(authorizedResponse is CallResponse.Result r
            ? $"call with a valid token got real RESULT: {r.Payload}"
            : $"call with a valid token got ERROR: {authorizedResponse}");
    }
}
