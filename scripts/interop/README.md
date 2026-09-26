# Interop checks against the verifiers themselves

The unit tests hold every proof's bytes to its verifier's vector. These
scripts go further, once per release: a proof this package makes is handed to
the verifier that will judge it in the field, which must accept it once and
refuse it changed and replayed. Neither runs in CI; each needs a macula
checkout compiled (v12.12.0 or later). `Signer` is the .NET side of both.

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
