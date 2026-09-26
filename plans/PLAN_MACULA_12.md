# PLAN_MACULA_12.md

**Status:** Built (0.5.0)
**Created:** 2026-09-26

## End goal

> This exists so a .NET shop can join the macula 12 mesh with `dotnet add package Macula`.

## Decision

macula-dotnet moves onto the macula 12 wire as a binding over libmacula
(macula-go's `cabi`), not as a native port (Raf, 2026-09-26).

Why not native: macula 12 stations accept only hybrid post-quantum key
exchange, and MsQuic's OpenSSL backend offers `secp256r1:x25519` and nothing
else (`SSL_set1_groups_list` in `src/platform/tls_openssl.c`, per connection,
unchanged on MsQuic main at 2026-09-25). No .NET API chooses the groups on
any platform. A native port would also have had to rebuild ML-DSA identity,
the 12 frames and CBOR in C#: a fifth implementation of the wire.

## Shape

- `src/Macula/Native/`: the P/Invoke declarations of `macula.h`, SafeHandles
  for every freed handle, blocking calls on the thread pool with a libmacula
  cancel token wired to the `CancellationToken`, and one pump thread per
  long-lived inbox (subscription, served procedure, stream) into a bounded
  channel.
- One file per capability: `NodeKey`, `Pool`, `Served`, `Subscription`,
  `MeshStream`, `Content`, `Records`, `Errors`, `Ids`, `Payload`.
- `libmacula.version` pins the macula-go release; `scripts/fetch_libmacula.sh`
  verifies every file by `SHA256SUMS` and attestation.

## Done

- [x] Delete the 0.4 stack (native QUIC, Ed25519, CBOR, frames, pool, UCAN, examples)
- [x] The binding, over macula-go v0.15.0 (ABI 1)
- [x] Tests against in-process stations (macula-go's teststation), CI on four runners
- [x] One live fleet check with throwaway keys: connect, DHT, pubsub, mcl-echo, serve and stream in the own namespace
- [x] C# examples; README with the MsQuic finding
- [x] Content example on the fleet (every station on 0.6.7)
- [ ] F# examples on the new API
- [x] The cabi fixes Neptunus measured, and Windows key files (macula-go v0.15.0)
