using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>
/// Connects to a real macula-station, advertises a trivial echo
/// procedure, and calls it. Two <see cref="Session"/>s, two identities
/// (a provider and a caller) -- a station kicks a connection the instant
/// a second one arrives under the same identity.
/// </summary>
public static class Quickstart
{
    public static async Task RunAsync()
    {
        var providerIdentity = KeyPair.GenerateWithDefaultPuzzle();
        var callerIdentity = KeyPair.GenerateWithDefaultPuzzle();

        await using var providerSession = await Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki);
        await using var callerSession = await Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki);

        var realm = new byte[32];
        // Unique per run -- reusing a fixed procedure name across rapid
        // repeated runs can hit stale DHT routing state from the prior
        // run's now-dead advertiser.
        var procedure = $"macula_dotnet.quickstart_echo.{Guid.NewGuid():N}";

        await providerSession.AdvertiseAsync(new AdvertiseSpec { Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId() });
        await Task.Delay(500); // ADVERTISE is fire-and-forget; give it a moment to land

        CallLookup lookup = (_, proc) => proc == procedure ? (payload => Task.FromResult(payload)) : null;
        var serveTask = providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds(15));

        var deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000;
        var callTask = callerSession.CallAsync(procedure, realm, Value.Text("hello"), deadlineMs, TimeSpan.FromSeconds(10));

        await Task.WhenAll(serveTask, callTask);

        var response = callTask.Result;
        Console.WriteLine(response is CallResponse.Result r
            ? $"call response: {r.Payload}"
            : $"call response: {response}");
    }
}
