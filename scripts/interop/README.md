# Interop checks against macula itself

The unit tests hold every proof's bytes to its verifier's vector. These
scripts go further, once per release: a proof this package makes is handed to
the verifier that will judge it in the field, which must accept it once and
refuse it changed and replayed. None runs in CI; each needs a macula checkout
compiled at the version its section names. `Signer` is the .NET side of the
proof checks, `Peer` of the handshake, sealing and sealed-group checks below.

## Ownership proof v2, through mcl_om

```sh
MACULA_GO_DIR=<macula-go clone> MACULA_BUILD=<macula checkout, compiled> MCL_OM_SRC=<dir> \
  scripts/interop/ownership_proof.sh
```

`Signer ownership` signs a payload of every CBOR type and calls `capture` (a Go
provider pinned to macula-go v0.17.0) through macula-go's in-process stations,
so the payload crosses the real path: this package's JSON, libmacula's CBOR, a
signed CALL, a station, a provider's verification. The captured payload goes
to macula-go's `scripts/interop/erlang_ownership_proof.escript verify` (taken
from macula-go at `libmacula.version`), in macula's pinned CI image, which
delivers it through macula's frame codec and the station link's caller step
into mcl_om's `verify_asserted_by/3`: accepted, refused with one field changed
(`bad_signature`), refused sent again (`replayed`). `MCL_OM_SRC` holds
`mcl_om_wire.erl`, `mcl_om_ownership_proof_replay.erl` and
`mcl_om_ownership_proof.erl` from mcl-om 0.32.0 (`91e59d87`).

## Device request proof v2, through the realm

```sh
MACULA_BUILD=<macula checkout, compiled> REALM_SRC=<macula-realm checkout> scripts/interop/device_request.sh
```

`Signer device-request` signs a join session's body; `realm_device_request.exs`
compiles the realm's `Identity`, `DeviceRequestReplay` and
`DeviceRequestProof` from `REALM_SRC` and runs `DeviceRequestProof.verify/5`
on it as the realm receives a body: accepted, refused with `device_info`
changed (`bad_proof`), refused sent again (`replayed`). The mesh rule is not
checked here: its request is a payload as a handler receives it, which the
realm's mesh handler builds.

Run each within 60 s of signing (the proofs carry the time they were made).
The scripts run the two halves back to back.

## Handshake v5 and sealing, against macula

```sh
MACULA_GO_DIR=<macula-go clone> MACULA_BUILD=<macula 13.2.0 or later, compiled> scripts/interop/v5.sh [pq_hybrid|pq_pure]
MACULA_GO_DIR=<macula-go clone> MACULA_BUILD=<macula 13.1.0 or later, compiled> scripts/interop/sealed.sh [pq_hybrid|pq_pure]
```

`v5.sh` runs macula-go's `erlang_v5_station.escript` (a bare macula station on
macula's own `macula_peering` and `macula_quic`, in macula's pinned CI image)
and dials it with a pool (`Peer v5link`). Each side derives the session binding
from its own TLS exporter. It passes when the station counted a v5 connection
and no v4 one, every connection end drained, and the pool's link stayed up
while the station probed it with `liveness_ping`.

`sealed.sh` runs through macula-go's teststation: macula-go's
`erlang_sealed.escript` serves `~<node>/vault` and `~<node>/watch`
confidential required and `Peer call` calls and streams to them sealed, then
the other way round (`Peer serve`), where the Erlang caller also calls the .NET
provider clear and must be refused `sealed_required`. Every seal report, the
.NET caller's and the Erlang caller's, must say sealed, the provider called
and a key id. Both take the escripts and the teststation from macula-go at
`libmacula.version` (needs Go, the .NET SDK, podman and, for `sealed.sh`,
python3); `PEER` names the command that runs `Peer`.

## Sealed groups, against macula's own distributor

```sh
MACULA_GO_DIR=<macula-go clone> MACULA_BUILD=<macula 15.0.0-rc.2 or later, compiled> scripts/interop/groups.sh [pq_hybrid|pq_pure]
```

`groups.sh` runs macula-go's teststation and macula-go's
`erlang_groups.escript`, which serves the org's distributor
(`macula_group_keys`, policy required) and takes the Erlang member's seat. An
Erlang member and a .NET member each subscribe under the group, pulling its
epoch from that distributor over a sealed call with the org's grant, write
their barrier, and each publishes sealed under the group: each must open the
other's event, and both print the one epoch's key id. Then a .NET node the
distributor's removed set names must be refused `not_a_member`. Both halves
come from macula-go at `libmacula.version` (needs Go, podman, python3 and the
.NET SDK); `PEER` names the command that runs `Peer`.

## Last run, 0.9.0-rc.2 (2026-10-14)

- `groups.sh`, both profiles, against macula 15.0.0-rc.2 from hex compiled in
  `ghcr.io/macula-io/macula-ci-otp@sha256:aff1d39b...82ac70` (OTP 28),
  macula-go v0.27.0-rc.3's escript and teststation, libmacula v0.27.0-rc.3 as
  fetched and verified: PASS. The Erlang member opened the .NET member's
  sealed event and the .NET member opened the Erlang member's, both under one
  epoch (`deed85c6da7befcc` in pq_hybrid, `8e57ea778983b5a2` in pq_pure), and
  the .NET node the distributor had removed was refused `not_a_member` in
  each.

## Last run, 0.7.0 (2026-09-29)

- `v5.sh` and `sealed.sh`, both profiles, against macula 13.2.2 from hex
  compiled in `ghcr.io/macula-io/macula-ci-otp@sha256:aff1d39b...82ac70` (OTP
  28), macula-go v0.20.0's escripts and teststation, libmacula v0.20.0 as
  fetched and verified: PASS. The station counted `v5_connections => 1,
  v4_connections => 0` in each profile; the .NET caller's reports were sealed
  to the Erlang provider under one key for the call and the stream, the Erlang
  caller's to the .NET provider, and the clear call was refused
  `sealed_required`.

## Last run, 0.6.0 (2026-09-27)

- `ownership_proof.sh`: macula v12.12.0 compiled in
  `ghcr.io/macula-io/macula-ci-otp@sha256:aff1d39b...82ac70` (OTP 28), mcl-om
  `91e59d87`, macula-go v0.17.0's escript: `ok; one field changed:
  {error,bad_signature}; the same proof again: {error,replayed}`, exit 0. With
  a field changed after signing: `{error,bad_signature}` first, exit 1. (The
  escript prints "go-signed"; it is its own label.)
- `device_request.sh`: macula-realm `e24701b`, Elixir 1.18.4 on OTP 28.4.3:
  `binding-signed: :ok; one field changed: {:error, :bad_proof}; the same
  proof again: {:error, :replayed}`, exit 0. With the body changed after
  signing: `{:error, :bad_proof}` first, exit 1.
