# macula-dotnet

[![CI](https://img.shields.io/github/actions/workflow/status/macula-io/macula-dotnet/ci.yml?branch=main&label=CI)](https://github.com/macula-io/macula-dotnet/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Macula.svg)](https://www.nuget.org/packages/Macula)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](#license)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com)
[![GitHub Sponsors](https://img.shields.io/badge/GitHub%20Sponsors-support-ea4aaa.svg?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/rgfaber)

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/macula-dotnet-full-dark.svg">
    <img src="assets/macula-dotnet-full-light.svg" alt="Macula" width="320">
  </picture>
</p>

<p align="center">
  <strong>The Macula mesh from .NET, on the macula 12 wire</strong><br>
  written in C#, usable from any .NET language
</p>

---

> **Status, 2026-09-29:** on the **macula 12** wire: TLS 1.3 with a hybrid
> post-quantum key exchange, ML-DSA-87 identities (as the ML-DSA-87 +
> RSA-PSS-4096 composite in `pq_hybrid`, the fleet's profile), and signed
> calls, replies and publications. Since 0.7.0, handshake v5 (the session
> bound to its TLS channel) and macula 13's end-to-end sealing with the
> caller's seal report, checked live against macula 13.2.2 both ways. Every feature is tested on each push
> against in-process macula 12 stations on Linux, macOS and Windows. Checked
> on the public fleet with throwaway keys: connecting, the DHT,
> publish/subscribe, calling `mcl-echo` in io.macula, serving calls and a
> stream in a node's own namespace, and serving a procedure gated on a
> post-quantum UCAN. Device request proofs (realm join) and ownership proofs
> are held to their verifiers' vectors, and checked through the realm's and
> mcl_om's own verifiers.

## What it is

A .NET client for the Macula mesh: node keys, a pool of links to stations,
calls and streams by direct dial, serving (in a node's own namespace or
under an org), publish/subscribe, node-served content, and the DHT. It is a
thin, idiomatic C# layer over **libmacula**, macula-go behind a C ABI
(macula-go's [`cabi`](https://github.com/macula-io/macula-go/tree/master/cabi)).
The package carries libmacula for every supported platform, so
`dotnet add package Macula` is all it takes.

## Why a binding, not a native port

macula-dotnet 0.4 was a native C# implementation on `System.Net.Quic`. It
cannot reach a macula 12 station, and no .NET release can today:

- macula 12 stations accept only hybrid post-quantum key exchange
  (SecP384r1MLKEM1024, SecP256r1MLKEM768) and refuse a classical-only
  client.
- On Linux and macOS, MsQuic's OpenSSL backend fixes the groups every
  connection offers in code: `SSL_set1_groups_list(TlsContext->Ssl,
  "secp256r1:x25519")` in `src/platform/tls_openssl.c`, still so on MsQuic's
  main as of 2026-09-25. It is set per connection, so no OpenSSL
  configuration can change it.
- MsQuic's `QUIC_TLS_GROUP` enum lists the ML-KEM groups, but only to
  *report* the negotiated one; there is no setting to choose them, and
  `QuicConnectionOptions` has none either. On Windows, .NET gives no control
  over the groups at all.
- The NuGet `Unofficial.MsQuic` package 0.4 shipped embeds OpenSSL 1.1.1,
  which has no ML-KEM.

So instead of a fifth implementation of QUIC, TLS 1.3 with a hybrid
post-quantum key exchange, deterministic CBOR and signed frames, .NET uses
macula-go's, the same choice [macula-php](https://github.com/macula-io/macula-php),
[macula-ts](https://github.com/macula-io/macula-ts) and macula-py make.
There is one implementation of the wire to keep correct, and the C ABI is
the same for every binding.

## Install

```sh
dotnet add package Macula
```

| Runtime | Needs |
|---|---|
| `linux-x64`, `linux-arm64` | glibc 2.28 or later |
| `osx-x64`, `osx-arm64` | macOS 13.0 or later |
| `win-x64` | Windows x64 |

.NET 10. Each libmacula in the package is the file of a macula-go release,
checked against its `SHA256SUMS` and its GitHub build provenance attestation
before it was packed (`scripts/fetch_libmacula.sh`); `libmacula.version`
names the release.

## Quick start

```csharp
using System.Text.Json.Nodes;
using Macula;

// A node key: ML-DSA-87 + RSA-PSS-4096 (pq_hybrid), its node id solving the
// admission puzzle. Saved readable by its owner only, reused next time.
using var key = await NodeKey.LoadOrCreateAsync("node.key");

// Stations are pinned by the node id they must prove, never by name alone.
var seeds = new[]
{
    new Seed("station-de-frankfurt.macula.io", 4433,
        MeshId.Parse("00cd0008ec2e72b6572b7bf6fc8b048d7fe83993faf1fc544370f2bc1eb71f85")),
};
await using var pool = await Pool.ConnectAsync(key, seeds, new PoolOptions
{
    RealmTrust = new Dictionary<MeshId, byte[]> { [realm] = realmKey },
});

// Serve a procedure in this node's own namespace: only this node can.
var greet = pool.OwnProcedure("greet");
await using var served = pool.Serve(realm, greet, (request, cancellationToken) =>
    ValueTask.FromResult<JsonNode?>(new JsonObject { ["hello"] = request.Caller.ToString() }));

// Call a service under an org, by direct dial to a provider the realm trusts.
var reply = await pool.CallAsync(realm, "mcl-echo/echo", new JsonObject { ["message"] = "hi" });

// Publish and subscribe.
await using var subscription = pool.Subscribe(realm, "news");
pool.Publish(realm, "news", new JsonObject { ["headline"] = "macula 12" });
await foreach (var e in subscription.ReadAllAsync(cancellationToken))
{
    Console.WriteLine($"{e.Publisher}: {e.Payload}");
}
```

## The API

| Type | What it does |
|---|---|
| `NodeKey` | `GenerateAsync`, `Load`, `LoadOrCreateAsync`, `Save`, `NodeId`, `PublicKey`, `Sign`, `Verify`; `CreateUcan`, `DeviceRequestProof`, `OwnershipProof` (below) |
| `Pool` | `ConnectAsync` (`PoolOptions.KemAdvertise`, below), `NodeId`, `OwnProcedure`, `Status`, `EventsAsync`, `DisposeAsync` |
| calls | `Pool.CallAsync` (`CallOptions.Ucan` presents a UCAN, `CallOptions.Confidential` seals it), `Pool.CallReportAsync`, `Pool.ProvidersAsync` |
| serving | `Pool.Serve` (a handler per call), `Pool.ServeStream` (a handler per session), each a `Served` to dispose, each optionally gated on an `AuthPolicy` and sealed (`ServedConfidential`) |
| streams | `Pool.OpenStreamAsync` (its `ucan` presents one, its `confidential` seals it), and `MeshStream`: `Send`, `CloseSend`, `Reply`, `Abort`, `Close`, `ReadAllAsync`, `Report` |
| UCANs | `Ucan.ProofId`, `Ucan.KeyId`, `Capability`, `UcanOptions`, `UcanPresentation`, `UcanRequired`, `RealmMemberRequired` |
| proofs | `DeviceRequestProofs.Message`, `OwnershipProofs.Message`: the exact bytes each signs |
| publish/subscribe | `Pool.Publish`, `Pool.Subscribe`, and `Subscription.ReadAllAsync`, `Dropped` |
| content | `Pool.ShareContentAsync`, `UnshareContentAsync`, `GetContentAsync` |
| the DHT | `Pool.FindRecordAsync`, `FindRecordsAsync`, `FindRecordsByTypeAsync`, `PutRecordAsync` |

Every call that waits takes a `CancellationToken`, and cancelling it ends the
wait in libmacula itself, not just the `await`. Failures are .NET's own
where one fits (`OperationCanceledException`, `TimeoutException`,
`ArgumentException`, `ObjectDisposedException`), and otherwise a
`MaculaException` with its `Kind`: `ProviderErrorException` (with the
provider's `Code` and `Detail`), `RelayErrorException`, `NotSharedException`,
`ContentUnavailableException` (with each sharer's failure),
`ConfidentialityException` (with its `Reason`, `Named` and `Found`).

### Payloads

A payload is a `System.Text.Json.Nodes.JsonNode`, mapped to and from the
mesh's CBOR:

- **No booleans.** The mesh's CBOR has none: send 0 and 1. A boolean is
  refused with an `ArgumentException` before anything is sent.
- **Integers are exact over the int64 range**, the wire's; read them with
  `GetValue<long>()`. One outside int64 is refused.
- **Bytes** are `Payload.Bytes(span)` going out and `Payload.TryGetBytes(node,
  out bytes)` coming back: the object `{"$bytes": "<base64>"}` either way.
- Map keys come back in the wire's deterministic order.

### Serving

A served procedure's handler gets the `Request` (the verified caller, realm,
procedure, payload, deadline, and whether it came sealed) and a
`CancellationToken` that ends at the call's deadline. Its result is the reply;
an exception it throws answers the caller with a `handler_error` whose detail
is the exception's message. Calls run concurrently, each on its own task. A
streaming handler's stream is closed when the handler returns, and aborted
with `handler_error` when it throws.

A procedure in a node's own namespace (`~<node id>/<name>`,
`Pool.OwnProcedure`) needs no org or realm to vouch for it. One under an org
(`<org>/<name>`) needs the org's delegation to this node in the realm, and
the realm's key in `PoolOptions.RealmTrust`.

### UCANs

A procedure served with an `AuthPolicy` answers only callers presenting a
UCAN (macula 12's post-quantum capability token) the policy accepts. The
provider checks each call and stream open before the handler sees it, as
macula does, and answers the rest `unauthorized` (or `malformed_frame` for a
proof no token in the chain names):

```csharp
await using var served = provider.Serve(realm, procedure, handler, new UcanRequired(root.NodeId));

var token = root.CreateUcan(caller.NodeId, [new Capability("mri:org:io.macula/acme", "invoke")],
    DateTimeOffset.UtcNow.AddHours(1));
await caller.CallAsync(realm, procedure, payload, new CallOptions { Ucan = new UcanPresentation(token) });

// Delegated: alice hands the caller one procedure, naming her grant as its parent.
var sub = alice.CreateUcan(caller.NodeId, [new Capability("mri:proc:io.macula/acme/count_v1", "invoke")],
    DateTimeOffset.UtcNow.AddMinutes(10), new UcanOptions { Parent = Ucan.ProofId(toAlice) });
await caller.CallAsync(realm, procedure, payload, new CallOptions { Ucan = new UcanPresentation(sub, [toAlice]) });
```

A token is minted for the node that will present it. `RealmMemberRequired(keyId, can)`
gates on a realm key instead, named by `Ucan.KeyId(realmPublicKey, profile)`.
macula's `test/vectors/UCAN_V1.md` is the contract.

### Sealing

macula 13 seals a call's or a stream's payload end to end to the provider's
KEM key (E2E seal scheme 1): stations route what they cannot read. It is off
until a provider opts in, and a caller seals whenever it can:

```csharp
// The provider names its KEM key in its advertisements.
await using var provider = await Pool.ConnectAsync(key, seeds, new PoolOptions { RealmTrust = trust, KemAdvertise = true });
await using var served = provider.Serve(realm, procedure, handler, confidential: ServedConfidential.Required);

// The caller seals to it: Preferred (the default) whenever the provider's advertisement names a key,
// Required never calls one that names none.
var (result, report) = await caller.CallReportAsync(realm, procedure, payload,
    new CallOptions { Confidential = Confidential.Required });
// report.Sealed, report.Provider, report.SealKeyId (the key, 16 hex)
```

- `KemAdvertise` is off by default. Enable it only once every station runs
  macula 12.11 or later and every caller can seal (macula 13, macula-go 0.18,
  Macula .NET 0.7 or later).
- A served procedure is `ServedConfidential.Preferred` by default: it names
  the key when the pool advertises one, and still takes a clear call while its
  last keyless advertisement could be served. `Required` refuses every clear
  call (`sealed_required`) and needs `KemAdvertise`; `Off` serves in the clear.
  `Request.Sealed` says whether a call came sealed; its payload is the opened
  plaintext either way.
- A caller has no `Off`: only an advertisement naming no key is called in the
  clear, and a sealed call never falls back to the clear. What could not be
  kept confidential throws a `ConfidentialityException`, its `Reason` one of
  `no_kem_key`, `key_mismatch`, `reply_not_opened`, `clear_answer_to_sealed`,
  `kem_advertise_disabled`.
- The seal report states that sealing ran on the exchange behind a result,
  nothing more. A stream's, `MeshStream.Report()`, settles on the provider's
  first chunk or reply; before that it throws a `MaculaException` of kind
  `NotSettled`, and on a served stream of kind `NotACaller`.

What stays visible: a request's UCAN and proofs, sizes, timing and routing.
Content (`ShareContentAsync`) is public by design and travels in the clear.
macula-go's `cabi/CONTRACT.md` ("Confidentiality", "The seal report") is the
contract.

### Proofs for a realm and for a service

`NodeKey.DeviceRequestProof` signs a device's request to a realm (realm
proof v2): a join session's body (the body text exactly as sent, or a
`JsonObject` under the HTTP rule) or a membership UCAN request over the mesh
(`DeviceRequestRule.Mesh`). `NodeKey.OwnershipProof` returns a payload with
the `asserted_by` block that authorises its fields to a service such as
mcl_om; send it as the payload. Neither signs a `"caller"`: the caller is the
verified signer, so a request or payload carrying one is refused. Signing
runs on the calling thread.

### Content

`ShareContentAsync` keeps the bytes in this node, serves them on its own
`~<node id>/content_v1` and announces them, for as long as the pool is open;
stations keep no content. `GetContentAsync` fetches an MCID from the nodes
that share it and checks every block against it, so no sharer is trusted.

## Examples

`examples/` has one short, complete program per feature. They connect fresh
nodes (throwaway keys) to the public fleet and the io.macula realm, or to
the stations `MACULA_STATIONS` names (`host:port@<node id hex>`, comma
separated) and the realm in `MACULA_REALM` and `MACULA_REALM_KEY`:

```sh
dotnet run --project examples -- quickstart   # a pool and its links
dotnet run --project examples -- call         # mcl-echo in io.macula, by direct dial
dotnet run --project examples -- serve        # serve in the own namespace, call it
dotnet run --project examples -- gated        # serve gated on a UCAN, call it with and without one
dotnet run --project examples -- pubsub       # publish, hear it back
dotnet run --project examples -- stream       # a server stream
dotnet run --project examples -- content      # share, fetch verified
dotnet run --project examples -- dht          # every station's endpoint record
dotnet run --project examples -- errors       # what each failure looks like
```

`content` needs every station a node links to to admit own-namespace
advertisements (macula-station 0.6.4 or later).

The F# examples of 0.4 were retired with its API; F# uses this one as it is.

## Building and testing

```sh
scripts/fetch_libmacula.sh                 # libmacula of libmacula.version, verified
# or, for an unreleased macula-go revision, for this machine only:
scripts/build_libmacula.sh <macula-go checkout>

dotnet test                                # runs macula-go's teststation harness
MACULA_GO_DIR=<macula-go checkout> dotnet test   # the harness from a checkout
```

The tests run against in-process macula 12 stations (macula-go's
`teststation/cmd/teststation`, which needs Go), in both profiles for UCANs.
CI runs them on Linux, macOS and Windows.

`scripts/interop/ownership_proof.sh` and `scripts/interop/device_request.sh`
check proofs this package signs against the verifiers themselves: mcl_om's
(in macula's pinned CI image), after the payload has crossed a station as a
provider receives it, and the realm's. `scripts/interop/v5.sh` and
`scripts/interop/sealed.sh` run handshake v5 against a macula station, and
sealed calls and streams with their seal reports both ways against a macula
node. See `scripts/interop/README.md`.

## License

Licensed under the Apache License, Version 2.0: see [LICENSE](LICENSE).

The .NET emblem in this README's header logo is Microsoft's official
[.NET logo](https://github.com/dotnet/brand/blob/main/logo/dotnet-logo.svg)
(from the `dotnet/brand` repository), licensed
[CC0 1.0 Universal](https://github.com/dotnet/brand/blob/main/LICENSE), a
public-domain dedication, credited here anyway. It is used (in
`assets/macula-dotnet-full-{dark,light}.svg`) only to identify the platform
this SDK targets, as the sibling [macula-go](https://github.com/macula-io/macula-go)
and [macula-rust](https://github.com/macula-io/macula-rust) badges use the Go
gopher and the Rust gear, and [macula-php](https://github.com/macula-io/macula-php)
the PHP logo. It is not an endorsement by Microsoft.
