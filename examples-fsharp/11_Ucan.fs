module UcanExample

open System
open Macula
open Macula.Connection
open Macula.Frame
open Macula.Identity
open Macula.Ucan

/// UCAN: mint a token, gate a served procedure behind a required issuer
/// via Policy.Required, then show both paths -- a call with no token is
/// refused (BOLT#4 Unauthorized) before the handler ever runs, a call
/// with a valid token reaches it. The gate is checked by
/// Session.ServeOneCallGatedAsync; plain Session.ServeOneCallAsync is a
/// zero-behavior-change delegation to it with an always-open policy, so
/// ordinary procedures are unaffected by UCAN existing at all.
let run () =
    task {
        let providerIdentity = KeyPair.GenerateWithDefaultPuzzle()
        let callerIdentity = KeyPair.GenerateWithDefaultPuzzle()

        let issuer = sprintf "did:macula:%s" (Convert.ToHexStringLower(callerIdentity.NodeId()))
        // A gated provider accepts a token only from the caller it names as
        // audience: that caller's node id as lowercase hex, no did: prefix.
        let token =
            UcanToken.Create(
                issuer,
                Convert.ToHexStringLower(callerIdentity.NodeId()),
                [| UcanToken.Capability("mri:procedure:examples_fsharp/ucan_gated", "call") |],
                callerIdentity)
        printfn "minted token, issuer=%s" issuer

        let! providerSession = Session.ConnectAsync(Station.Host, Station.Port, providerIdentity, Trust.UseWebPki)
        let! callerSession = Session.ConnectAsync(Station.Host, Station.Port, callerIdentity, Trust.UseWebPki)

        let realm = Array.zeroCreate<byte> 32
        let procedure = sprintf "macula_dotnet_sdk.examples_fsharp.ucan_gated.%s" (Guid.NewGuid().ToString "N")

        do! providerSession.AdvertiseAsync(AdvertiseSpec(Realm = realm, Procedure = procedure, Advertiser = providerIdentity.NodeId()))
        printfn "provider advertised %s, requiring issuer=%s" procedure issuer
        do! Threading.Tasks.Task.Delay 500

        let lookup =
            CallLookup(fun _realm proc ->
                if proc = procedure then CallHandler(fun payload -> Threading.Tasks.Task.FromResult payload) else null)
        let policy =
            PolicyLookup(fun _realm proc ->
                if proc = procedure then Policy.Required(callerIdentity.NodeId()) else Policy.Open)

        let deadlineMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 20_000L

        // First call: no token. Refused by policy before the handler ever runs.
        let unauthorizedServeTask = providerSession.ServeOneCallGatedAsync(lookup, policy, TimeSpan.FromSeconds 15.0)
        let! unauthorizedResponse = callerSession.CallAsync(procedure, realm, Value.Null, deadlineMs, TimeSpan.FromSeconds 10.0)
        do! unauthorizedServeTask
        printfn "call without a token: %A" unauthorizedResponse

        // Second call: valid token. Reaches the handler for real.
        let authorizedServeTask = providerSession.ServeOneCallGatedAsync(lookup, policy, TimeSpan.FromSeconds 15.0)
        let! authorizedResponse = callerSession.CallWithUcanAsync(procedure, realm, Value.Text "hello, gated procedure", deadlineMs, TimeSpan.FromSeconds 10.0, token)
        do! authorizedServeTask

        match authorizedResponse with
        | :? CallResponse.Result as r -> printfn "call with a valid token got real RESULT: %A" r.Payload
        | other -> printfn "call with a valid token got ERROR: %A" other

        do! providerSession.CloseAsync()
        do! callerSession.CloseAsync()
    }
