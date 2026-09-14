# Changelog

All notable changes to `Macula`, this repository's NuGet package, are
documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package is
pre-1.0, so a minor version may carry a small breaking change; each one is
called out below. Releases before 0.4.1 predate this file; see the git tags.

## [0.4.2] - Unreleased

### Fixed

- **CBOR nested more than 128 levels deep is refused.** `CborCodec.Decode`
  throws `CborDecodeException` for a list or map value more than 128 levels
  below the top-level value, the same limit as macula, macula-go and
  macula-rust.
- **Every length and count in CBOR input is checked against the bytes after
  it.** A byte or text length, list count or map count that the rest of the
  input can't hold throws `CborDecodeException` before anything is sized by
  it. A length beyond the range of an int used to throw `OverflowException`
  or `OutOfMemoryException` instead. Lists and maps make room for at most
  1024 items before their items decode.
- **One decode produces at most 1,048,576 values.** Every decoded value
  counts, map keys and map values included, and the value past the limit
  throws `CborDecodeException`. The limit and the counting rule are
  macula-go's.
- **Duplicate map keys merge exactly when their canonical encodings are
  equal**, as in macula and macula-go: a nested map sent in another order, or
  a number sent with a longer head than it needs, no longer leaves a second
  entry. Each key is identified once, while it decodes.
- **A frame length prefix with its top bit set** makes `WireCodec.Decode`
  throw `FrameTooLargeException`, not `ArgumentOutOfRangeException`.
- **`ContentTransfer.GetAsync` checks a fetched manifest before using it.**
  The manifest must describe the requested MCID (its name, size, chunk size,
  chunk count, hash algorithm and root hash recompute to it), its chunks must
  be cut the way `ManifestBuilder.Create` cuts content, and its chunk hashes
  must make its root hash, all before any chunk is fetched. Each chunk must
  be the size its entry says, and the content is put together from the
  chunks that arrived instead of being sized from the manifest up front.
- **`ManifestBuilder.FromWire` reads a manifest the way macula does.** A
  missing `hash_algorithm` is blake3 and any other algorithm is refused, every
  number is checked against its field's range before it is converted, the
  name must be UTF-8, and the chunks must describe the content whole. These
  refusals use the new `FromWireError.InvalidValue`.
- **`ManifestBuilder.Verify` refuses a chunk size that is not positive**, with
  the new `VerifyError.InvalidManifest`, instead of cutting the data by it.

### Changed

- **`ManifestBuilder.Create` refuses `Algorithm.Sha256` and a chunk size that
  is not positive**, with `ArgumentException` and
  `ArgumentOutOfRangeException`. A sha256 manifest could never be fetched,
  because every chunk is fetched by its blake3 hash. `Algorithm.Sha256` and
  `CreateOptions.HashAlgorithm` stay in 0.4.x so code that names them still
  compiles, and are removed in 0.5.0.

## [0.4.1] - 2026-09-11

### Fixed

- **Sends that overlap on one session no longer fail.** System.Net.Quic
  rejects a write that starts while another is still pending, and nothing
  serialized the frames a `Session` sends, so in 0.4.0 two tasks sending at
  once could fail a send, a publish for example, with
  `InvalidOperationException`. Sends on a stream now take turns, and each
  frame is still written whole.
- **`SupervisedPubSub.RunPublisherAsync` sends `pubsub.publish_started_v1`
  before the publish, not beside it.** In 0.4.0 the fact was sent without
  waiting for it, so it could fail the publish with
  `InvalidOperationException` or be lost. It is now sent and finished
  first, then the publish, then `pubsub.publish_completed_v1`. A fact that
  fails to send still never fails the publish.
