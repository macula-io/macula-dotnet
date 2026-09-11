module PublishSubscribe

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity

/// Subscribe to a topic, publish to it, and receive the resulting EVENT --
/// a subscriber does receive its own publish, delivered_via "direct".
///
/// The subscription gets only the events that match its realm and topic.
/// Anything else the station sends on the same session, such as its own
/// periodic advertise broadcasts for built-in `_content.*` procedures, is
/// routed elsewhere or counted in Session.UnroutedFrameCounts, so one
/// RecvEventAsync is enough.
let run () =
    task {
        let identity = KeyPair.GenerateWithDefaultPuzzle()
        let! session = Session.ConnectAsync(Station.Host, Station.Port, identity, Trust.UseWebPki)

        // A random realm, not the all-zero sentinel: this is a shared
        // public demo station, and realm=zero is exactly the sentinel
        // macula's own content-transfer procedures use, so it sees
        // meaningfully more unrelated traffic than a random realm does.
        let realm = Array.zeroCreate<byte> 32
        Random.Shared.NextBytes realm
        let topic = sprintf "macula_csharp_sdk.examples_fsharp.%s" (Guid.NewGuid().ToString "N")

        let! subscription = session.SubscribeAsync(SubscribeSpec(Topic = topic, Realm = realm, Subscriber = identity.NodeId()))
        printfn "subscribed to %s" topic

        do!
            session.PublishAsync(
                PublishSpec(
                    Topic = topic,
                    Realm = realm,
                    Publisher = identity.NodeId(),
                    Seq = 1UL,
                    Payload = Value.Text "hello mesh",
                    PublishedAtMs = uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))
        printfn "published"

        let! evt = subscription.RecvEventAsync(TimeSpan.FromSeconds 10.0)
        printfn "received EVENT: topic=%s payload=%s delivered_via=%s" evt.Topic (evt.Payload.AsText()) evt.DeliveredVia

        do! subscription.DisposeAsync()
        do! session.CloseAsync()
    }
