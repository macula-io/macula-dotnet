module Quickstart

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity

/// Connects to a real macula-station, advertises a trivial echo
/// procedure, and calls it. Two Sessions, two identities (a provider and
/// a caller) -- a station kicks a connection the instant a second one
/// arrives under the same identity.
let run () =
    task {
        let providerIdentity = KeyPair.GenerateWithDefaultPuzzle()
        let callerIdentity = KeyPair.GenerateWithDefaultPuzzle()

        let! providerSession = Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki)
        let! callerSession = Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        // Unique per run -- reusing a fixed procedure name across rapid
        // repeated runs can hit stale DHT routing state from the prior
        // run's now-dead advertiser.
        let procedure = sprintf "macula_dotnet.quickstart_echo.%s" (Guid.NewGuid().ToString "N")

        do! providerSession.AdvertiseAsync(AdvertiseSpec(Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId()))
        do! Threading.Tasks.Task.Delay 500 // ADVERTISE is fire-and-forget; give it a moment to land

        let lookup =
            CallLookup(fun _realm proc ->
                if proc = procedure then CallHandler(fun payload -> task { return payload }) else null)

        let serveTask = providerSession.ServeOneCallAsync(lookup, TimeSpan.FromSeconds 15.0)

        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 10_000L
        let! response = callerSession.CallAsync(procedure, realm, Value.Text "hello", deadlineMs, TimeSpan.FromSeconds 10.0)

        do! serveTask

        match response with
        | :? CallResponse.Result as r -> printfn "call response: %A" r.Payload
        | other -> printfn "call response: %A" other
    }
