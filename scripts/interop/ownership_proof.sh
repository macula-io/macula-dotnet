#!/usr/bin/env bash
# The Macula package's ownership proofs through mcl_om's own verifier: Signer signs a payload and sends it the
# real way to capture, and macula-go's scripts/interop/erlang_ownership_proof.escript verify must accept it
# once, refuse it with one field changed (bad_signature) and refuse it sent again (replayed), in macula's
# pinned CI image.
#
#   MACULA_GO_DIR=<macula-go clone> MACULA_BUILD=<macula checkout, compiled> MCL_OM_SRC=<dir> \
#     scripts/interop/ownership_proof.sh
#
# MCL_OM_SRC holds mcl_om_wire, mcl_om_ownership_proof_replay and mcl_om_ownership_proof from mcl-om 0.32.0
# (91e59d87). The escript and the teststation come from macula-go at libmacula.version. Needs Go, the .NET SDK
# and podman. The proof carries the time it was signed, so the two halves run back to back.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(tr -d '[:space:]' < "$root/libmacula.version")"
image="${MACULA_CI_IMAGE:-ghcr.io/macula-io/macula-ci-otp@sha256:aff1d39bc4aa29d13044b90b38e9b7f4b757d50818cc11c5bb7e84cdbf82ac70}"
: "${MACULA_GO_DIR:?a macula-go clone}" "${MACULA_BUILD:?a compiled macula checkout}" "${MCL_OM_SRC:?mcl_om 0.32.0 proof modules}"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT

git -C "$MACULA_GO_DIR" fetch --quiet --tags origin
git -C "$MACULA_GO_DIR" show "$version:scripts/interop/erlang_ownership_proof.escript" > "$work/erlang_ownership_proof.escript"
GOBIN="$work" go install "github.com/macula-io/macula-go/teststation/cmd/teststation@$version"
(cd "$root/scripts/interop/capture" && go build -o "$work/capture" .)
dotnet run --project "$root/scripts/interop/Signer" -c Release -- ownership "$work/teststation" "$work/capture" "$work/payload.txt"
podman run --rm --name "macula-dotnet-ownership-proof-$$" \
  -v "$MACULA_BUILD:/macula:ro" -v "$MCL_OM_SRC:/mcl-om:ro" -v "$work:/work:ro" \
  "$image" escript /work/erlang_ownership_proof.escript verify /macula/_build/default/lib/macula/ebin /mcl-om /work/payload.txt
