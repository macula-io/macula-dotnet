# Changelog

All notable changes to `Macula`, this repository's NuGet package, are
documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/). The package is
pre-1.0, so a minor version may carry a small breaking change; each one is
called out below. Releases before 0.5.0 predate this file; see the git tags.

## [0.5.0] - Unreleased

### Changed

- **Direct dial tries every authorized provider.** `DirectDial.CallAsync`,
  `CallWithUcanAsync`, `CallWithCertChainAsync`, `OpenStreamDirectAsync`,
  `OpenStreamDirectWithCertChainAsync` and `GetDirectAsync` try each
  advertised provider in the order the DHT returns them, instead of only
  the first. A provider that can't be reached before the request is sent
  is skipped for the next one, and a request that has been sent is never
  sent again. `GetDirectAsync` also retries while no provider has
  announced the content yet.
- **Breaking: the timeout bounds the whole call**, finding the provider
  included. A timeout sized for the request alone can now run out during
  resolution, and a timeout that is zero or negative, including
  `Timeout.InfiniteTimeSpan`, throws `ArgumentOutOfRangeException`.
  `ResolveAsync`, `ResolveWithCertChainAsync` and
  `ResolveStationEndpointAsync` give up after 10 seconds.
  `PutDirectAsync`'s timeout covers the endpoint lookup and the dial.
  When the timeout cuts off a `GetDirectAsync` transfer, the
  `TimeoutException` carries the previous failure as its inner exception.
