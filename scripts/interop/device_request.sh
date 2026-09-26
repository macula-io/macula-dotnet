#!/usr/bin/env bash
# The Macula package's device request proofs through the realm's own verifier: Signer signs a join session's
# body, and MaculaRealm.Identity.DeviceRequestProof.verify/5 must accept it once, refuse it with a field
# changed (bad_proof) and refuse it sent again (replayed).
#
#   MACULA_BUILD=<macula checkout, compiled> REALM_SRC=<macula-realm checkout> scripts/interop/device_request.sh
#
# Compiles the realm's identity.ex, identity/device_request_replay.ex and identity/device_request_proof.ex from
# REALM_SRC's apps/macula_realm, with elixir and erl on PATH (the realm's Elixir on OTP 28).
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
: "${MACULA_BUILD:?a compiled macula checkout}" "${REALM_SRC:?a macula-realm checkout}"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
dotnet run --project "$root/scripts/interop/Signer" -c Release -- device-request "$work/proof.txt"
elixir -pa "$MACULA_BUILD/_build/default/lib/macula/ebin" "$root/scripts/interop/realm_device_request.exs" \
  "$REALM_SRC/apps/macula_realm/lib/macula_realm" "$work/proof.txt"
