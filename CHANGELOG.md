# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
