# Changelog

All notable changes to `Macula`, this repository's NuGet package, are
documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package is
pre-1.0, so a minor version may carry a small breaking change; each one is
called out below. Releases before 0.4.1 predate this file; see the git tags.

## [0.5.0] - Unreleased

### Changed

- **Direct dial tries every authorized provider.** `DirectDial.CallAsync`,
  `CallWithUcanAsync`, `CallWithCertChainAsync`, `OpenStreamDirectAsync`,
  `OpenStreamDirectWithCertChainAsync` and `GetDirectAsync` try each
  advertised provider in the order the DHT returns them, instead of only
  the first. A provider that can't be reached before the request is sent
  is skipped for the next one, and a request that has been sent is never
  sent again. `GetDirectAsync` also retries while no provider has
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
  retries a lookup that fails within its budget.
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
  `CallAsync` and its variants still dial their own connection.
- **Breaking: one reader per session, so calls, subscriptions and serving
  run at the same time.** Each `Session` reads its own control stream and
  routes every frame: a reply to its call, an event to each matching
  subscription, an inbound CALL to a queue of 64. `Session.SubscribeAsync`
  returns a `Subscription` with its own queue of 256 events and its own
  `RecvEventAsync`; disposing it sends UNSUBSCRIBE once no other
  subscription on the session holds that realm and topic. Topics match by
  the station's rule: split on "/", equal segment counts, and "*" matches
  exactly one segment. `Session.RecvAsync`, `Session.RecvEventAsync` and
  `Session.UnsubscribeAsync` are removed. A consumer that falls behind ends
  with `ConsumerOverflowException` after the items already queued, and the
  session stays up; an inbound CALL that doesn't fit gets
  `temporary_relay_failure`. GOODBYE, or HELLO or CONNECT after the
  handshake (`ProtocolViolationException`), ends the session and fails
  waiting calls with the reason, and direct dial no longer reuses a session
  that has ended. Other frames nothing waits for are counted in
  `Session.UnroutedFrameCounts` and reported through
  `System.Diagnostics.Trace` at most once a minute per frame type.
  `StationPool` runs on all of this, without a pump or send gate of its
  own.
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

## [0.4.1] - Unreleased

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
