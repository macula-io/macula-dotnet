# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.9.0-rc.2] - 2026-10-14

Wire 1.0, the second release candidate on NuGet, carrying sealed groups.

### Added

- **Sealed groups** (macula 13.2, `DESIGN_E2E_SEALED_PUBSUB.md`, macula-go
  v0.27.0-rc.3): a .NET app publishes and subscribes under a realm group, and
  every station between it and a subscriber routes what it cannot read.
  `Pool.PublishGroupAsync` seals a publication under the group's current
  epoch; `Pool.SubscribeGroupAsync` joins the group and opens its events. Each
  joins first, pulling the group's epoch keys from the org's distributor over
  a sealed call with the org's grant — a UCAN with `can` `group_keys`, which
  the org mints for a member — so each takes a timeout and a
  `CancellationToken` like a call. `Group` names the prefix, the grant and,
  for a `~<node id>` group, the distributor; the grant is a bearer credential
  and stays out of what a `Group` prints.
- `MeshEvent.Sealed` and `MeshEvent.SealKeyId` name the epoch an event was
  opened under, and `MeshEvent.Unopened` says why one could not be opened —
  `not_a_member`, `membership_unknown`, `unknown_epoch`, `epoch_expired`,
  `no_distributor`, `no_group` or `tag_invalid`, with a null payload. A plain
  `Subscribe` under a group prefix hears every such event as `no_group`. A
  join stays held once it succeeded, so a clear `Publish` under the prefix is
  refused `group_held` from then on.
- `GroupException` (`ErrorKind.Group`) carries macula's reason, and the prefix
  a `group_held` refusal names.
- `scripts/interop/groups.sh`: sealed groups across macula 15.0.0-rc.2 and
  this package, both ways, through macula's own Erlang distributor — an Erlang
  member and a .NET member each open the other's sealed event under one epoch,
  and a .NET node the distributor's removed set names is refused
  `not_a_member`.

### Changed

- **The packaged libmacula is macula-go v0.27.0-rc.3** (was v0.27.0-rc.1),
  which carries the sealed group calls. `LibraryFloor` moves with it:
  v0.27.0-rc.1 speaks wire 1.0 and passes the wire check, but has no group
  call at all, so a library from before the floor is refused by name as it
  loads rather than linking one that cannot seal a group's publication
  (macula-go#34).

## [0.9.0-rc.1] - 2026-10-13

Wire 1.0, a release candidate on NuGet. NuGet has no dist-tag: the pre-release
is opt-in by version, so a plain `dotnet add package Macula` still resolves
0.8.0, on the macula 12 wire, until wire 1.0 ships to the fleet.

### Added

- **The load path proves which wire libmacula links** (macula-go#18, #30). A
  library that speaks another wire major is refused as it loads, naming the
  release it was built from (`macula_library_version()`) beside the wire it
  speaks (`macula_wire_version()`) and the one this package speaks; every 1.x
  peer shares the frozen core, as `cabi/CONTRACT.md` "Version" has it, so a 1.1
  library is spoken with rather than refused. It replaces the old floor, which
  asked only whether `macula_stream_report` was exported: a v0.26.0 library
  exports it and passes that check, and reaches no wire 1.0 station.
  `LibraryFloor` is therefore v0.27.0-rc.1, and `scripts/fetch_libmacula.sh`
  checks the pinned release's `macula.h` `MACULA_WIRE_VERSION` against the
  binding's, as it already checks the ABI.

### Changed

- **The packaged libmacula is macula-go v0.27.0-rc.1** (was v0.26.0), which
  speaks wire 1.0 (macula 15) and nothing older (macula-go#30). Wire 1.0 is a
  new station contract: frames carry version 5 and the wire 1.0 envelope (an
  `ext` extension map, an unknown critical extension refused and an unknown
  non-critical one read past), an unknown frame type no longer costs a link, an
  unknown enum value never closes one, the handshake is version 5 alone (the
  client proof is V3, over SHA-384 of the CONNECT tbs; the station's session
  proof is V2), records carry `schema` and a lifetime class, and the DHT
  procedures are the versioned `_dht.*` ones. A station before wire 1.0 does
  not accept it, and it does not accept one. The in-process test stations are
  wire 1.0 too, and the whole suite passes on them, in both profiles.
- The README names the candidate and how to install it. The package
  description names no wire, as the other bindings' shared metadata does: it is
  packed into each version and cannot be revised afterwards, so a wire named
  there outlives the release that speaks it. Nothing else in the API changed: this package's
  surface is the same on either wire, and 0.8.0 remains the release for the
  macula 12 fleet.

## [0.8.0] - 2026-10-09

On libmacula from macula-go v0.26.0 (was v0.23.0).

### Security

- **A UCAN whose issuer did:key is over 4,400 characters is refused at once**
  (macula#87, macula-go#27). Base58 decodes in time quadratic in its length,
  ahead of the signature check; on v0.23.0 a gated procedure decoding a
  300,000-character issuer outlasted a 5 s call. It is now refused
  `unauthorized` before the handler runs. The library also refuses, at every
  link of a chain, an `exp` more than ten years past now (macula#68, v0.25.0),
  and `NodeKey.CreateUcan` will not mint one (v0.24.0).
- `LibraryFloor` is v0.26.0: an older library would silently lack the bound.

### Fixed

- **A process that loads libmacula no longer crashes under garbage-collection
  load on Linux** (GHSA-49xj-cmc6-whqc, #7). libmacula is Go, and Go's runtime,
  started inside a process that is not Go, adds `SA_ONSTACK` to the signal
  handlers it does not own, among them the runtime's own thread-suspension
  signal (`SIGRTMIN`). That handler then ran on a thread's small alternate
  signal stack, and under collection load a suspended thread could resume with
  corrupted registers: the process died with a segmentation fault in whatever
  managed code was running. The binding now keeps the runtime's registration
  of that one signal as the runtime made it, around libmacula's load; every
  other signal keeps Go's `SA_ONSTACK`. A residual remains: the runtime
  signals a thread it saw running managed code, and if that thread enters a
  call into libmacula in the microseconds between, the handler runs on a
  small Go stack and could overrun it. Linux only; macOS is unmeasured.

### Tests

- The UCAN vectors are macula v14.5.0's `ucan_v1.json` (was 0e2724cc), with
  `did_key_length`, held to 4,400. A gated procedure refuses a token with a
  300,000-character issuer at once, in both profiles; that test, run with the
  rest of the suite, is what exposed the crash above.

## [0.7.1] - 2026-10-06

On libmacula from macula-go v0.23.0 (was v0.20.0).

### Changed

- Every connection to a station now negotiates SecP384r1MLKEM1024, the hybrid group that meets both CNSA 2.0
  (ML-KEM-1024 with P-384) and BSI TR-02102 (hybrid only). On v0.20.0 it landed on SecP256r1MLKEM768 with every
  station: Go's crypto/tls ignores the order of its group preferences and offered SecP256r1MLKEM768 first, and a
  station takes the client's first group. A station that accepts only SecP256r1MLKEM768 now fails in the TLS
  handshake. Every macula 12 station accepts SecP384r1MLKEM1024
  ([macula-go#20](https://github.com/macula-io/macula-go/issues/20), closes #8).
- Also in libmacula since v0.20.0: a station endpoint read from the DHT carries the release its station says it runs
  (macula-go v0.21.0), and the C ABI can verify a signed object (`macula_signed_object_verify`, v0.22.0). This
  package does not expose either yet.
- The library floor this package names is now macula-go v0.23.0 (was v0.20.0), since an older libmacula lands on
  SecP256r1MLKEM768. The floor is named in the error for a library that lacks a function; the check cannot tell
  v0.20.0 to v0.22.0 from v0.23.0, as no function was added.

## [0.7.0] - 2026-09-29

On libmacula from macula-go v0.20.0 (was v0.18.2): macula 13's end-to-end sealing, the caller's seal report, and
handshake v5.

### Added

- **Sealed calls and streams** (macula 13's E2E seal scheme 1, macula-go v0.18.0). `PoolOptions.KemAdvertise` names
  the node's KEM key in the advertisements of what it serves confidentially; it is off by default. `Pool.Serve` and
  `Pool.ServeStream` take `confidential:` (`ServedConfidential.Preferred`, the default, `Required` or `Off`), and
  `Request.Sealed` says whether a call came sealed. `CallOptions.Confidential` and `OpenStreamAsync`'s `confidential:`
  take `Confidential.Preferred` (the default: sealed whenever the provider's advertisement names a key) or `Required`
  (never calls one that names none). What could not be kept confidential throws a `ConfidentialityException`
  (`Reason`, `Named`, `Found`).
- **The seal report** (macula's DESIGN_E2E_SEAL_REPORT, macula-go v0.19.0): `Pool.CallReportAsync` returns
  `Reported(Result, Report)`, and `MeshStream.Report()` a caller stream's; a `SealReport` has `Sealed`, `Provider` and
  `SealKeyId`. A stream's throws a `MaculaException` of kind `NotSettled` before it settles and `NotACaller` on a
  served stream.
- `scripts/interop/v5.sh` and `scripts/interop/sealed.sh`, with `scripts/interop/Peer`: handshake v5 against a macula
  station, and sealed calls and streams with their seal reports both ways against a macula node.

### Changed

- **Handshake v5** (macula-go v0.20.0): a link binds its session to its TLS channel instead of a composite signature
  on every control frame in pq_hybrid; libmacula dials v4 once when a station refuses v5. It comes with the library;
  nothing in the .NET API changes.
- The library floor is macula-go v0.20.0. Calls, serving and opens go through libmacula's `*_opts` functions, whose
  one options set carries a UCAN, its proofs and the confidentiality.
- Recompile against 0.7.0: `OpenStreamAsync` gains `confidential` before `cancellationToken`, `Serve` and
  `ServeStream` gain a trailing `confidential`, and `Request` gains `Sealed` as its sixth member. A call that names
  its optional arguments compiles unchanged; one that passes `cancellationToken` to `OpenStreamAsync` by position, or
  constructs or deconstructs a `Request`, needs editing.

### Fixed

- **A streaming handler that returns now closes its stream**; it used to be aborted when the stream was freed, so the
  caller saw a stream error instead of the stream's end, unlike a Python or TypeScript provider.

## [0.6.1] - 2026-09-29

On libmacula from macula-go v0.18.2 (was v0.17.0), for two fixes a .NET caller gets from the library.

### Fixed

- **A call enters a provider's handler at most once** (macula-go#8, fixed in v0.18.1). A call used to move to the
  next provider after any failure but a provider's answer, a timeout included, and a provider slower than one
  candidate's share of the deadline was called again elsewhere: **a handler that is not idempotent could run
  twice**. Now the call moves on only when a provider's station cannot be reached, before anything is sent; once the
  call has gone out, its outcome is returned as it is. A reply that is lost ends in a timeout, and the handler ran
  once or not at all.
- **No call goes out with a provider deadline past its caller's** (macula-go#12, v0.18.2).

### Changed

- The library floor is macula-go v0.18.2: an older libmacula lacks these fixes. Nothing in the .NET API changes:
  sealed calls and streams, and the seal report, come in the release on macula-go v0.19.0.

## [0.6.0] - 2026-09-27

On libmacula from macula-go v0.17.0 (was v0.15.0).

### Added

- UCANs (macula 12, D7): `NodeKey.CreateUcan` mints a token for the node that
  will present it; `CallOptions.Ucan` and `OpenStreamAsync`'s `ucan` present
  one and its chain's proofs (`UcanPresentation`); `Pool.Serve` and
  `Pool.ServeStream` take an `AuthPolicy` (`UcanRequired`,
  `RealmMemberRequired`), and the provider refuses what it does not accept
  with `unauthorized` (or `malformed_frame` for a proof no token names).
  `Ucan.ProofId` and `Ucan.KeyId`, held to macula's UCAN vectors.
- `NodeKey.DeviceRequestProof` (realm proof v2, macula-realm#29) and
  `DeviceRequestProofs.Message`, held to the realm's vector; a proof made here
  is accepted by the realm's own verifier (`scripts/interop/device_request.sh`).
- `NodeKey.OwnershipProof` (v2, mcl-om#7) and `OwnershipProofs.Message`, held
  to mcl_om's vector; a payload signed here, delivered through a station, is
  accepted by mcl_om's own verifier and refused changed or replayed
  (`scripts/interop/ownership_proof.sh`).
- `Ucan.KeyId` refuses a key whose length is not its profile's.
- The `gated` example, run against the public fleet.

### Breaking

- `OpenStreamAsync`: `lifetime` is now `deadline` (how far ahead the open's
  signed deadline lies, which bounds the provider's admission of the open, not
  the stream's life, as the contract says; the value is passed as before), and
  a new `ucan` parameter precedes `cancellationToken`. A 0.5.0 caller that
  passed the token positionally, or named `lifetime:`, no longer compiles.

### Changed
- Loading a libmacula older than macula-go v0.17.0 fails at once, naming the
  version it needs, instead of at the first call it lacks.
- A handler's request payload never holds a "caller" its sender wrote: who
  called is `Request.Caller`, the verified signer.
- A device request or ownership-proven payload carrying a "caller" is refused
  (`ArgumentException`).

## [0.5.0] - 2026-09-26

### Breaking

- **On the macula 12 wire, over libmacula.** Macula is now a C# layer over
  libmacula, macula-go v0.15.0 behind its C ABI (cabi, ABI 1), shipped in the
  package for linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64. The 0.4
  native stack is gone: its QUIC transport, Ed25519 identity, CBOR codec,
  frames, station pool, UCANs and examples. No .NET QUIC stack can reach a
  macula 12 station (MsQuic fixes its key exchange groups to secp256r1 and
  x25519; see the README), and macula 12 needs everything else rebuilt anyway.
- The API is new: `NodeKey`, `Pool`, `Served`, `Subscription`, `MeshStream`,
  `MeshId`, `Mcid`, `Payload`, and the `MaculaException` family.

### Added

- ML-DSA-87 node keys (`Profile.PqPure`) and the ML-DSA-87 + RSA-PSS-4096
  composite (`Profile.PqHybrid`, the default), generated with the admission
  puzzle solved, saved readable by their owner only.
- Calls and streams by direct dial; serving in a node's own namespace
  (`Pool.OwnProcedure`) or under an org; publish/subscribe; node-served
  content (`ShareContentAsync`, `GetContentAsync`, every block checked
  against its MCID); the DHT.
- A `CancellationToken` on every call that waits, which ends the wait in
  libmacula itself.
- Each libmacula in the package is the macula-go release's file, checked
  against its `SHA256SUMS` and its build provenance attestation
  (`scripts/fetch_libmacula.sh`), and `scripts/check_package.sh` refuses a
  package that lacks one.
- Tests against in-process macula 12 stations on Linux (x64, arm64), macOS
  and Windows, and eight C# examples.
