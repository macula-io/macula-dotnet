# Changelog

All notable changes to `Macula`, this repository's NuGet package, are
documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package is
pre-1.0, so a minor version may carry a small breaking change; each one is
called out below. Releases before 0.4.1 predate this file; see the git tags.


## [0.5.0] - Unreleased

### Changed

- **A CALL handler receives its caller.** A map payload reaches the
  handler with the caller's 32-byte node id under `"caller"`, the caller
  the CALL's signature was verified against, replacing any `"caller"` the
  sender put in the payload. A payload that isn't a map reaches the
  handler unchanged and carries no caller.
- **Breaking: `StreamHandle.AcceptAsync` refuses a STREAM_OPEN not signed by
  its caller.** Stream handlers previously received the STREAM_OPEN's caller
  field unverified; upgrade if a stream handler relies on it. A stream whose
  first frame doesn't verify against the caller it names, has no signature
  or caller, is of another type, or doesn't decode is aborted in both
  directions with application error code 2
  (`StreamHandle.RefusedStreamCode`), with nothing written, and
  `AcceptAsync` waits for the next stream within its timeout. A provider
  that accepts a stream and won't serve it refuses it with
  `StreamHandle.RefuseAsync`, which writes a STREAM_ERROR, finishes the
  send side and stops reading with code 2.
- **A frame that doesn't decode ends a session with
  `MalformedFrameException`**, where it used to end with a plain
  `IOException`.
- **An inbound CALL with a wrongly typed `ucan_token` is dropped as
  malformed**, and serving goes on. It used to end the session.
- **A stream handler receives its caller.** Map args of an accepted
  STREAM_OPEN carry the verified caller under `"caller"`, replacing any
  `"caller"` the opener put there, as a CALL handler's payload does.
- **Drop warnings.** A session traces a dropped CALL (`dropped_call`), a
  RESULT or ERROR for no pending call (`dropped_reply`) and a refused
  stream (`refused_stream_open`) with its `count`, `reason`, and
  `procedure` or `call_id`. The first of a kind in an interval is traced at
  once, and the rest are counted into one closing line when the interval
  ends. `Session.DropWarningInterval` sets the interval, 60 seconds by
  default.
- **Direct dial tries every authorized provider.** `DirectDial.CallAsync`,
  `CallWithUcanAsync`, `CallWithCertChainAsync`, `OpenStreamDirectAsync`,
  `OpenStreamDirectWithCertChainAsync` and `GetDirectAsync` try each
  advertised provider in the order the DHT returns them, instead of only
  the first. A provider that can't be reached before the request is sent
  is skipped for the next one, as is one whose CALL was never sent because
  its session had ended or its turn to write didn't come in time, and a
  request that has been sent is never sent again. `GetDirectAsync` also retries while no provider has
  announced the content yet, and a DHT lookup that fails is retried within
  the timeout instead of ending the call.
- **Breaking: a direct-dial call that runs out of time throws what it
  observed.** It throws the last candidate failure, else why an answered
  DHT lookup found nothing (`ProcedureNotAdvertisedException`,
  `NoTrustedAdvertisementException`, `NoAuthorizedAdvertisementException`
  or `ContentNotAnnouncedException`), else a failed lookup's own exception,
  and a `TimeoutException` only when nothing was observed at all. A call
  whose lookups all failed or went unanswered used to throw
  `ProcedureNotAdvertisedException` or `StationEndpointNotFoundException`.
  A `station_endpoint` lookup follows the same rule, throwing
  `StationEndpointNotFoundException` only when a lookup was answered, and
  retries a lookup that fails within its budget. A record that names no
  dialable address is looked up again too, and when it is the latest answer
  the call throws the new `StationEndpointMalformedException`.
- **Breaking: the timeout bounds the whole call**, finding the provider
  included. A timeout sized for the request alone can now run out during
  resolution, and a timeout that is zero or negative, including
  `Timeout.InfiniteTimeSpan`, throws `ArgumentOutOfRangeException`.
  `ResolveAsync`, `ResolveWithCertChainAsync` and
  `ResolveStationEndpointAsync` give up after 10 seconds.
  `PutDirectAsync`'s timeout covers the endpoint lookup and the dial.
  When the timeout cuts off a `GetDirectAsync` transfer, the
  `TimeoutException` carries the previous failure as its inner exception.
- **Breaking: direct dial reuses a session this process already has open
  to the provider's station under the same identity.** A station keeps one
  connection per identity and closes the older one when a newer one
  arrives, so a second dial used to close `resolveVia` or a `StationPool`
  link. `OpenStreamDirectAsync`, `OpenStreamDirectWithCertChainAsync`,
  `PutDirectAsync` and `GetDirectAsync` now run on that open session, on a
  dedicated QUIC stream, and leave it open. A session direct dial dials
  itself is shared the same way by the requests that find it, and closes
  when the last of them is done. The stream calls return
  `DirectDial.OpenedStream` (`Stream`, `Session`) instead of a
  `(Session, StreamHandle)` tuple: dispose it once the stream is done.
  `CallAsync`, `CallWithUcanAsync` and `CallWithCertChainAsync` run on
  such a session too.
- **Breaking: one reader per session, so calls, subscriptions and serving
  run at the same time.** Each `Session` reads its own control stream and
  routes every frame: a reply to its call, an event to each matching
  subscription, an inbound CALL to a queue of 64. `Session.SubscribeAsync`
  returns a `Subscription` with its own queue of 256 events and its own
  `RecvEventAsync`; disposing it sends UNSUBSCRIBE once no other
  subscription on the session holds that realm and topic. Topics match by
  the station's rule: split on "/", equal segment counts, and "*" matches
  exactly one segment. `Session.RecvAsync`, `Session.RecvEventAsync` and
  `Session.UnsubscribeAsync` are removed. A subscription that falls behind
  ends with `ConsumerOverflowException` after the events already queued, and
  the session stays up; an inbound CALL that doesn't fit gets
  `temporary_relay_failure`, and serving carries on. GOODBYE, or HELLO or CONNECT after the
  handshake (`ProtocolViolationException`), or a frame that can't be
  decoded, ends the session: waiting calls and every later operation on it
  throw `SessionEndedException`, whose `InnerException` is the reason, the
  session closes its connection, and direct dial no longer reuses it. Every
  session end is reported once through `System.Diagnostics.Trace` with its
  reason, the session's node id and the station's. Other frames nothing waits for are counted in
  `Session.UnroutedFrameCounts` and reported through
  `System.Diagnostics.Trace` at most once a minute per frame type.
  `StationPool` runs on all of this, without a pump or send gate of its
  own.
- **Sends on a session are bounded.** Writes on a session's control stream
  take turns. A call waits for its turn within its own timeout, and every
  other send within a 30 second send timeout; when that runs out, only that
  caller gets a `TimeoutException`, the frame was not sent, and the session
  carries on. A write that stalls for more than 30 seconds ends the session
  with `SendTimeoutException`, and a `StationPool` link dials again. A call
  that runs out of time throws `CallTimeoutException`, and a call whose
  session ends first throws `SessionEndedException`; the `WriteStarted` of
  either says whether its CALL may have reached the station, and a reply that
  arrives after that is counted as unrouted. The replies a session makes on its own,
  such as `temporary_relay_failure`, and its RPC telemetry facts go to a
  writer of their own: they never hold up its reader or cost a call time,
  `rpc.sent_v1` goes once the CALL is written, and when 64 frames already
  wait there the next one is dropped.
- **Breaking: `StationPool.CallAsync` tries another link only when the CALL
  was not sent.** It moves to the next connected link only when the call's
  `SessionEndedException` or `CallTimeoutException` says its CALL was never
  written there. A reply, RESULT or ERROR, is returned from the link that
  gave it, and a call that timed out after its write started throws, so a
  provider never runs one call twice. Pool calls no longer publish RPC
  telemetry facts, as macula's pool doesn't.
- **Breaking: a UCAN-gated procedure binds the token to its caller.**
  `Policy.Check` takes the CALL's caller as well as its token, and a
  `Policy.Required` procedure accepts a token only when its `aud` is that
  caller's 32-byte node id as lowercase hex, with no `did:` prefix
  (`Convert.ToHexStringLower(caller.NodeId())`). A token with another or
  no audience, or a CALL without a caller, is refused as Unauthorized
  (`UcanToken.WrongAudienceException`, `UcanToken.NoCallerException`).
  Mint tokens for gated procedures with that audience.
- **An inbound CALL must be signed by the caller it names.**
  `Session.ServeOneCallAsync`, `ServeOneCallGatedAsync` and `StationPool`
  drop a CALL whose signature doesn't verify against its `caller` field,
  without a reply and before any policy or handler runs, matching the
  Erlang station link.

### Fixed

- **A `StationPool` subscription to a wildcard topic receives its events.**
  The pool looked up handlers by an event's own topic, so a subscription to
  `orders/*` never saw an event published to `orders/placed`. Each link
  subscription now hands its events to the handlers of its own pattern, and
  events are deduplicated per pattern, so overlapping subscriptions such as
  `orders/*` and `orders/placed` each receive an event once.

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
  instead. Lists and maps make room for at most 1024 items before their
  items decode.
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
