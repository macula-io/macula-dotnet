# macula-dotnet

[![CI](https://img.shields.io/github/actions/workflow/status/macula-io/macula-dotnet/ci.yml?branch=main&label=CI)](https://github.com/macula-io/macula-dotnet/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](#license)
[![.NET](https://img.shields.io/badge/.NET-10.0%2B-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![no FFI](https://img.shields.io/badge/FFI-none-success.svg)](#why-native-not-a-binding)
[![GitHub Sponsors](https://img.shields.io/badge/GitHub%20Sponsors-support-ea4aaa.svg?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/rgfaber)

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/macula-dotnet-full-dark.svg">
    <img src="assets/macula-dotnet-full-light.svg" alt="Macula" width="320">
  </picture>
</p>

<p align="center">
  <strong>Native .NET client for the Macula SDK wire protocol</strong><br>
  written in C#, usable from any .NET language
</p>

---

> **Status, 2026-08-30:** the FULL wire protocol is built and
> **live-verified against the production station fleet**
> (`station-de-frankfurt.macula.io`) — handshake, unary RPC (both caller
> AND provider), PubSub, content transfer, and streaming RPC, every
> primitive in both caller and provider roles where the protocol has
> one. On top of that: **direct-dial** (resolve a service via the mesh
> DHT and dial it in one hop, plain and cert-chain-authorized, extended
> to streaming and content transfer too), **UCAN** capability tokens
> (mint/verify/introspect, policy-gated serving), a **supervised PubSub
> pair**, **periodic re-advertise**, and **RPC telemetry auto-facts** —
> all live-verified the same way. The frame layer is cross-checked
> byte-for-byte — including the Ed25519 signature itself — against
> [`macula-rust`](https://github.com/macula-io/macula-rust)'s own
> fixed reference vectors: 15 golden frames, all matching. See
> [Status](#status) for the full picture.

## What is this?

A native .NET implementation, written in C#, of the client half of Macula's wire protocol —
the same protocol [`macula-io/macula`](https://github.com/macula-io/macula)
(the Erlang/OTP SDK) speaks, and the same protocol
[`macula-go`](https://github.com/macula-io/macula-go) and
[`macula-rust`](https://github.com/macula-io/macula-rust) already
port. Macula is a federated mesh for sovereign, end-to-end-encrypted
application networks; a **station** is the relay/DHT node, and this
library is what a **leaf** — anything that isn't itself a station — uses
to join it.

## Why native, not a binding

Unlike [`macula-php`](https://github.com/macula-io/macula-php)
(a thin FFI binding over the Go SDK's compiled C ABI, the right call for
PHP), this is a full independent port: its own deterministic CBOR codec,
its own Ed25519/S/Kademlia identity layer, its own frame construction —
built the same way the Go and Rust ports were, not wrapped around either
of them. Two things make that practical for C# where it wasn't for PHP:
.NET has shipped a real QUIC client (`System.Net.Quic`) since .NET 7, and
`System.Formats.Cbor` already covers general CBOR mechanics (though not
this protocol's specific canonicalization rules — see
[The CBOR codec is hand-rolled on purpose](#the-cbor-codec-is-hand-rolled-on-purpose)).
No cgo, no native `.so`/`.dll` shipped by this repo, no P/Invoke into
anything macula-specific — `dotnet add package Macula` is the
whole install (once published to nuget.org — see [Status](#status)).

The one native dependency that's unavoidable regardless of approach is
QUIC itself: `System.Net.Quic` needs `libmsquic`, which Microsoft doesn't
publish for Linux on NuGet (only via the `packages.microsoft.com` apt/dnf
repo). This library depends on the community
[`Unofficial.MsQuic`](https://www.nuget.org/packages/Unofficial.MsQuic)
package instead, which ships `libmsquic` for linux-x64/linux-arm64/win-x64
as ordinary NuGet runtime assets — so `dotnet add package` really is
still the whole install, no system package manager required.

## Why a fourth implementation matters

Four independent implementations (Erlang reference, Rust, Go, now C#)
producing bit-identical wire bytes for the same input is a much stronger
correctness claim than any one of them alone. This repo's own
`FrameGoldenVectorTests` builds the exact same 15 signed frames
`macula-rust`'s own differential-vector tests build — same identity,
same fixed `frame_id`/`sent_at_ms`/`call_id`/`stream_id` — and asserts
the Ed25519 signature, not just the frame shape, matches byte for byte
against vectors originally captured from a real `rebar3 shell` against
`macula-io/macula` itself. If this port's canonical CBOR encoder or
signing domain diverged from the other three anywhere, this would fail;
it doesn't.

## Features

| Primitive | Caller | Provider | Notes |
|---|---|---|---|
| Handshake (CONNECT/HELLO) | ✅ | — | Ed25519 identity, S/Kademlia puzzle-hardened; HELLO signature verified; live-verified |
| Deterministic CBOR codec | ✅ | — | Hand-rolled — see [Codec](#the-cbor-codec-is-hand-rolled-on-purpose) |
| Unary RPC (CALL/RESULT/ERROR) | ✅ | ✅ | `Session.ServeOneCallAsync`, BOLT#4 error mapping, live-verified |
| PubSub (PUBLISH/SUBSCRIBE/EVENT) | ✅ | ✅ | A subscriber gets its own publish, verified live |
| Content transfer (single-block + chunked) | ✅ | — | Content-addressed, BLAKE3/SHA-256, Merkle-verified |
| Streaming RPC (STREAM_OPEN/DATA/END/REPLY) | ✅ | ✅ | Both roles live-verified against the real fleet |
| RPC advertise/unadvertise | ✅ | — | |
| Pubkey-pinned trust | ✅ | — | `Trust.Pin(nodeId)` — Ed25519 SPKI match, no CA chain needed |
| Direct-dial (RPC) | ✅ | ✅ | `DirectDial.ResolveAsync`/`CallAsync`/`AdvertiseDirectAsync` — resolve+dial via the mesh DHT, no advertise-gossip propagation needed |
| Direct-dial, cert-chain-authorized | ✅ | ✅ | `...WithCertChainAsync` variants — opt-in org/realm authorization on top of plain direct-dial |
| Direct-dial (streaming, content) | ✅ | — | `DirectDial.OpenStreamDirectAsync`/`PutDirectAsync`/`GetDirectAsync` |
| Periodic re-advertise | — | ✅ | `DirectDial.KeepAdvertisedDirectAsync` — keeps a station-side registration fresh for a long-lived provider |
| Supervised PubSub pair | ✅ | ✅ | `SupervisedPubSub.RunPublisherAsync`/`RunSubscriberAsync` — callback-driven, auto-publishes `pubsub.publish_started_v1`/`publish_completed_v1` |
| UCAN (mint/verify/introspect) | ✅ | ✅ | `UcanToken.Create`/`Verify`/`Decode` and friends — no library exists for the exact spec version macula uses, hand-rolled to match the reference exactly |
| UCAN-gated serving | — | ✅ | `Session.ServeOneCallGatedAsync` + `Policy.Required`/`Open` — a caller with no/invalid token is refused before the handler ever runs |
| RPC telemetry auto-facts | ✅ | ✅ | `rpc.sent_v1`/`rpc.completed_v1` (caller), `rpc.received_v1`/`rpc.replied_v1` (provider) — always-on, fire-and-forget, matching the reference exactly |

## Structure

```
src/Macula/
  Value.cs                 The wire protocol's closed value model
  Cbor/CborCodec.cs        Deterministic CBOR encode/decode
  Identity/                Ed25519 keypair, S/Kademlia puzzle
  Bolt4/                   17-entry BOLT#4 error taxonomy
  Frame/                   Envelope, signing, wire codec, every frame type
  Connection/               QUIC transport, handshake, Session (RPC/PubSub/serve)
  Content/                  Manifest (chunking/Merkle), put/get
  Streaming/                StreamHandle -- caller and provider roles
  Dht/                      DirectDial (resolve/call/advertise via the mesh DHT) + CertChain
  Ucan/                     UcanToken (mint/verify/introspect) + Policy (gated serving)
examples/                  A quickstart, one example per primitive, plus error handling, a long-running provider, direct-dial, and UCAN (C#)
examples-fsharp/           The same examples in F#
tests/Macula.Tests/         Offline unit tests + live station tests
```

## Quick start

Also lives as a runnable example — `dotnet run --project examples --
quickstart`. Advertises and calls its own trivial echo procedure (two
identities, a provider and a caller, since a station kicks a connection
the instant a second one arrives under the same identity) rather than
depending on any particular procedure already being advertised on the
fleet:

```csharp
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
```

## Examples

Every example exists in both `examples/` (C#) and `examples-fsharp/` (F#)
with matching numbers and behavior — same station calls, same output shape.

| # | Run (C#) | Run (F#) | What it shows |
|---|---|---|---|
| 00 | `dotnet run --project examples -- 00` | `dotnet run --project examples-fsharp -- 00` | Quickstart: advertise + call its own trivial echo procedure |
| 01 | `dotnet run --project examples -- 01` | `dotnet run --project examples-fsharp -- 01` | Identity + connect + close |
| 02 | `dotnet run --project examples -- 02` | `dotnet run --project examples-fsharp -- 02` | Unary RPC caller (CALL/RESULT/ERROR) |
| 03 | `dotnet run --project examples -- 03` | `dotnet run --project examples-fsharp -- 03` | PubSub (subscribe, publish, receive the EVENT) |
| 04 | `dotnet run --project examples -- 04` | `dotnet run --project examples-fsharp -- 04` | Content transfer, single-block and chunked |
| 05 | `dotnet run --project examples -- 05` | `dotnet run --project examples-fsharp -- 05` | Streaming RPC, caller role |
| 06 | `dotnet run --project examples -- 06` | `dotnet run --project examples-fsharp -- 06` | Unary RPC, provider role (two `Session`s, one process) |
| 07 | `dotnet run --project examples -- 07` | `dotnet run --project examples-fsharp -- 07` | Streaming RPC, provider role (two `Session`s, one process) |
| 08 | `dotnet run --project examples -- 08` | `dotnet run --project examples-fsharp -- 08` | Every error shape this SDK produces, handled |
| 09 | `dotnet run --project examples -- 09` | `dotnet run --project examples-fsharp -- 09` | A provider serving many calls over its lifetime, not just one |
| 10 | `dotnet run --project examples -- 10` | `dotnet run --project examples-fsharp -- 10` | Direct-dial: advertise via the mesh DHT, resolve, dial, call — no advertise-gossip propagation needed |
| 11 | `dotnet run --project examples -- 11` | `dotnet run --project examples-fsharp -- 11` | UCAN: mint a token, gate a served procedure by policy, show both the rejected and accepted paths |

Examples 06, 07, and 09 run more than one role in **one process, multiple
`Session`s** — there's no cgo/fork hazard here the way there was in
[`macula-php`](https://github.com/macula-io/macula-php)'s
FFI-over-Go binding, so a provider and a caller can just be concurrent
tasks in the same async function.

## Using this from F#

Nothing about this library is C#-specific — it's a plain .NET assembly,
and `examples-fsharp/` is live-verified against the real station exactly
like its C# counterpart. Two things are worth knowing if you haven't
mixed F# with a C#-authored async API before:

- **Prefer F#'s `task { }` computation expression over `async { } |>
  Async.AwaitTask`.** Several methods here (`Session.CloseAsync`,
  `StreamHandle.AbortAsync`) return `ValueTask`, and `Async.AwaitTask`
  has no overload for that — it only accepts `Task`/`Task<'T>`, so you'd
  need an extra `.AsTask()` call on every such site. `task { }` awaits
  `ValueTask`/`ValueTask<'T>` directly, no conversion needed, confirmed
  directly rather than assumed (see `examples-fsharp/`, which uses `task { }`
  throughout).
- **`required`-property spec types** (`CallSpec`, `PublishSpec`,
  `SubscribeSpec`, `AdvertiseSpec`, and friends) construct the same way
  any C# object with settable properties does from F#: parens with named
  assignments, e.g. `SubscribeSpec(Topic = topic, Realm = realm, Subscriber
  = identity.NodeId())` — not C#'s `{ }` object-initializer braces.

## Testing

```bash
dotnet test --filter "Category!=Live"   # offline: pure logic + 15 golden byte-exact frame vectors
dotnet test --filter "Category=Live"    # dials the real production fleet -- see below
```

The offline suite (run in CI on every push) never touches the network:
CBOR codec edge cases, Ed25519/puzzle vectors captured from a real
`rebar3 shell`, and all 15 golden frame vectors. The live suite dials
`station-de-frankfurt.macula.io` — throwaway dev infrastructure with no
uptime guarantee, so it's excluded from CI and only run manually.

**Running the live suite locally on a distro without `libmsquic`'s
required OpenSSL version:** `Unofficial.MsQuic`'s Linux build links
against OpenSSL 1.1, which some distros (Arch among them) no longer ship.
If `dotnet test --filter Category=Live` fails with `Unable to load MsQuic
library version '2'`, check `ldd` on the restored
`runtimes/linux-x64/native/libmsquic.so` for `libcrypto.so.1.1 => not
found` — if so, either install an OpenSSL 1.1 compatibility package, or
substitute any locally-built `libmsquic.so` compiled against your
system's actual OpenSSL (msquic itself has no other requirements).

## The CBOR codec is hand-rolled on purpose

Every frame's Ed25519 signature is computed over the *canonical* CBOR
bytes of the frame, and macula's canonicalization rules diverge from
RFC 8949's own recommendations in one deliberate way: floats always
encode as full binary64 (`0xFB` + 8 bytes), never the shortest
round-tripping width the RFC prefers. `System.Formats.Cbor` (the BCL's
own CBOR writer) implements the RFC's canonical mode, not this one — so
using it here would silently produce non-verifying signatures against a
real station. `Cbor/CborCodec.cs` is a direct, from-scratch
transcription of the same ~200-line deterministic algorithm the Erlang
reference (`macula_cbor_nif`'s `deterministic.rs`) and the Rust/Go ports
already use: minimal-length integers, map keys sorted by their own
*encoded* bytes (not their logical value), `procedure`/`topic`/`detail`
as raw byte strings even though most other string-ish fields are text,
and floats always full-width.

## Real findings from building this against the live fleet

Two things worth knowing if you're integrating against the real station,
neither obvious from the wire-protocol spec alone:

- **`System.Net.Quic` accepts zero inbound streams by default.** Unlike
  `quinn` (Rust's QUIC crate), a `QuicConnection` that doesn't set
  `MaxInboundBidirectionalStreams`/`MaxInboundUnidirectionalStreams`
  throws `InvalidOperationException` the moment anything calls
  `AcceptInboundStreamAsync` — including the station routing an inbound
  STREAM_OPEN to an advertised procedure. `Session.ConnectAsync` sets
  both to 100 unconditionally, since the decision to advertise happens
  after the connection already exists.
- **The station periodically sends unprompted `advertise` frames for its
  own built-in `_content.*` procedures over every connected client's
  control stream** — observed directly while testing the PubSub example,
  not documented anywhere in the wire-protocol spec. `Session.RecvEventAsync`
  errors on the first non-EVENT frame rather than silently skipping it
  (matching the Go/Rust/PHP SDKs — a caller waiting specifically for a
  pubsub delivery has no reason to expect anything else), so a real
  caller sharing a control stream between PubSub and anything else should
  loop past frames it doesn't recognize rather than call it exactly once.
  See `examples/03_PublishSubscribe.cs`'s own `RecvEventSkippingOtherTrafficAsync`.

## Status

Every application primitive the wire protocol defines is built, in both
roles where the protocol has one, and live-verified against
`station-de-frankfurt.macula.io`:

- Transport + handshake (CONNECT/HELLO, Ed25519 identity, S/Kademlia
  puzzle, WebPki and pubkey-pinned trust)
- Unary RPC, caller and provider (`Session.CallAsync` /
  `Session.ServeOneCallAsync`)
- PubSub, caller and provider (a subscriber does receive its own publish)
- Content transfer, single-block and chunked (sequential v1, matching
  the Go/Rust SDKs — multi-lane parallelism is a throughput optimization
  addable later with zero wire change)
- Streaming RPC, caller and provider (`StreamHandle.OpenAsync` /
  `StreamHandle.AcceptAsync`)
- Direct-dial: resolve a service via a signed DHT record and dial it in
  one hop, plain and cert-chain-authorized, for RPC, streaming, and
  content transfer (`DirectDial`) — see [Direct-dial and the mesh
  DHT](#direct-dial-and-the-mesh-dht) for what "DHT" means here
- UCAN capability tokens: mint/verify/introspect, plus policy-gated
  serving that refuses an unauthorized caller before a handler ever runs
  (`UcanToken`, `Policy`, `Session.ServeOneCallGatedAsync`)
- A supervised PubSub pair (`SupervisedPubSub`) and periodic re-advertise
  (`DirectDial.KeepAdvertisedDirectAsync`) for long-lived providers
- RPC telemetry auto-facts, always-on, matching the reference exactly

Not built, matching every sibling SDK's own documented v1 scope: real DHT
peer participation (Kademlia routing tables, replication) — this library
only ever asks whichever station it's already connected to look something
up in or publish to the DHT via ordinary RPC (`_dht.put_record`/
`_dht.find_record`), the same way `macula-go`/`macula-rust` do.
HyParView/Plumtree gossip primitives are station-to-station overlay
concerns, explicitly out of scope for a leaf client by design, not an
unfinished gap.

## Direct-dial and the mesh DHT

"Direct-dial" doesn't mean this library is a DHT participant — the actual
Kademlia routing table, replication, and station-to-station gossip stay
entirely inside `macula-station` (the relay). What `DirectDial` adds is
much narrower: two RPC calls (`_dht.put_record`/`_dht.find_record`) to
whichever station this session is already connected to, plus the
signature verification to trust what comes back. The problem it solves:
ordinary `Session.AdvertiseAsync`/`CallAsync` only work if the caller's
station and the provider's station have already exchanged routing gossip
— best-effort, and on a fleet with more than a couple of stations, often
incomplete. The DHT is the one directory every station already
participates in regardless of gossip state, so resolving a
`procedure_advertisement` record there and dialing the named station
directly works even when ordinary routing hasn't (yet) propagated a
route. `DirectDial.AdvertiseDirectAsync` publishes both the DHT record
and an ordinary `Session.AdvertiseAsync` registration — a station still
needs *something* registered to route an inbound CALL to once dialed;
direct-dial only changes how the caller *finds* the station, not whether
a handler is waiting once it gets there.

When several providers advertise the same procedure, `DirectDial` tries
them in the order the DHT returns them. A provider that can't be reached
before the request goes out is skipped for the next one, and a request
that has been sent is never sent again. The `timeout` given to
`CallAsync` and its siblings bounds the whole call, finding the provider
included, and must be positive. `ResolveAsync` and
`ResolveStationEndpointAsync`, which take no timeout, give up after 10
seconds.

**NuGet publish:** not live yet. `.github/workflows/release.yml` publishes
via [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
(OIDC, no stored API key) on every `v*` tag push — set up and ready, just
not fired yet. Until then, consume this library via a project reference,
a local `dotnet pack`, or a `ProjectReference` to this repo directly.

## License

Licensed under the Apache License, Version 2.0 — see [LICENSE](LICENSE).

The .NET emblem in this README's header logo is Microsoft's official
[.NET logo](https://github.com/dotnet/brand/blob/main/logo/dotnet-logo.svg)
(from the `dotnet/brand` repository), licensed
[CC0 1.0 Universal](https://github.com/dotnet/brand/blob/main/LICENSE) —
a public-domain dedication, no attribution legally required, credited
here anyway as a matter of course. Used here (as part of
`assets/macula-dotnet-full-{dark,light}.svg`) purely to identify the
platform this SDK targets, the same way the sibling
[macula-go](https://github.com/macula-io/macula-go) and
[macula-rust](https://github.com/macula-io/macula-rust) badges
use the Go gopher and Rust gear marks, and
[macula-php](https://github.com/macula-io/macula-php) uses the
official PHP logo — not an endorsement by Microsoft.
