using Macula.Connection;
using Macula.Frame;
using Macula.Identity;

namespace Macula.Examples;

/// <summary>
/// Subscribe to a topic, publish to it, and receive the resulting EVENT --
/// a subscriber does receive its own publish, delivered_via "direct".
///
/// The subscription gets only the events that match its realm and topic.
/// Anything else the station sends on the same session, such as its own
/// periodic advertise broadcasts for built-in `_content.*` procedures, is
/// routed elsewhere or counted in <see cref="Session.UnroutedFrameCounts"/>,
/// so one RecvEventAsync is enough.
/// </summary>
public static class PublishSubscribe
{
    public static async Task RunAsync()
    {
        var identity = KeyPair.GenerateWithDefaultPuzzle();
        await using var session = await Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki);

        // A random realm, not the all-zero sentinel: this is a shared
        // public demo station, and realm=zero is exactly the sentinel
        // macula's own content-transfer procedures use, so it sees
        // meaningfully more unrelated traffic than a random realm does.
        var realm = new byte[32];
        Random.Shared.NextBytes(realm);
        var topic = $"macula_csharp_sdk.examples.{Guid.NewGuid():N}";

        await using var subscription = await session.SubscribeAsync(new SubscribeSpec { Topic = topic, Realm = realm, Subscriber = identity.NodeId() });
        Console.WriteLine($"subscribed to {topic}");

        await session.PublishAsync(new PublishSpec
        {
            Topic = topic,
            Realm = realm,
            Publisher = identity.NodeId(),
            Seq = 1,
            Payload = Value.Text("hello mesh"),
            PublishedAtMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        Console.WriteLine("published");

        var evt = await subscription.RecvEventAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"received EVENT: topic={evt.Topic} payload={evt.Payload.AsText()} delivered_via={evt.DeliveredVia}");
    }
}
