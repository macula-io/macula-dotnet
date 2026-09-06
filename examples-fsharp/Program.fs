module Program

open System.Threading.Tasks

[<EntryPoint>]
let main argv =
    let name = if argv.Length > 0 then argv.[0] else ""
    // Each module's `run` is a `task { ... }` with no explicit `return` --
    // F#'s task builder infers that as `Task<unit>`, not the bare `Task`
    // a C# `async Task` method with no return value would produce.
    let example : (unit -> Task<unit>) option =
        match name with
        | "00" | "quickstart" -> Some Quickstart.run
        | "01" | "handshake" -> Some Handshake.run
        | "02" | "call" -> Some Call.run
        | "03" | "pubsub" -> Some PublishSubscribe.run
        | "04" | "content" -> Some Content.run
        | "05" | "stream" -> Some StreamOpenCaller.run
        | "06" | "rpc-provider" -> Some RpcProvider.run
        | "07" | "stream-provider" -> Some StreamProvider.run
        | "08" | "errors" -> Some ErrorHandling.run
        | "09" | "long-running-provider" -> Some LongRunningProvider.run
        | "10" | "direct-dial" -> Some DirectDialExample.run
        | "11" | "ucan" -> Some UcanExample.run
        | _ -> None

    match example with
    | None ->
        eprintfn "Usage: dotnet run --project examples-fsharp -- <example>"
        eprintfn "  00 | quickstart             advertise + call its own trivial echo procedure"
        eprintfn "  01 | handshake              identity + connect + close"
        eprintfn "  02 | call                   unary RPC caller"
        eprintfn "  03 | pubsub                 publish/subscribe/event"
        eprintfn "  04 | content                content transfer (put/get)"
        eprintfn "  05 | stream                 streaming RPC, caller role"
        eprintfn "  06 | rpc-provider           unary RPC, provider role (two sessions)"
        eprintfn "  07 | stream-provider        streaming RPC, provider role (two sessions)"
        eprintfn "  08 | errors                 every error shape this SDK produces"
        eprintfn "  09 | long-running-provider  a provider serving many calls, not just one"
        eprintfn "  10 | direct-dial            resolve+dial a provider via its DHT advertisement"
        eprintfn "  11 | ucan                   mint a token, gate a served procedure by policy"
        1
    | Some run ->
        (run ()).GetAwaiter().GetResult()
        0
