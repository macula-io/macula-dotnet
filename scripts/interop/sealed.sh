#!/usr/bin/env bash
# Sealed calls and streams across macula 13 and the Macula package, both ways, through macula-go's test station,
# each caller's seal report checked: an Erlang provider (named key, required) called and streamed to by Peer; then a
# .NET provider, called and streamed to by macula-go's erlang_sealed.escript, which also calls it clear at its station
# and must be refused sealed_required. The Erlang side runs in macula's pinned CI image.
#
#   MACULA_GO_DIR=<macula-go clone> MACULA_BUILD=<compiled macula, 13.1.0 or later> \
#     scripts/interop/sealed.sh [pq_hybrid|pq_pure]
#
# The escript and the teststation come from macula-go at libmacula.version. PEER runs the .NET side (default: dotnet
# run of scripts/interop/Peer); it must reach the loopback the station listens on. PODMAN_CGROUP_PARENT, when set,
# is the Erlang container's --cgroup-parent.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(tr -d '[:space:]' < "$root/libmacula.version")"
image="${MACULA_CI_IMAGE:-ghcr.io/macula-io/macula-ci-otp@sha256:aff1d39bc4aa29d13044b90b38e9b7f4b757d50818cc11c5bb7e84cdbf82ac70}"
profile="${1:-pq_hybrid}"
: "${MACULA_GO_DIR:?a macula-go clone}" "${MACULA_BUILD:?a compiled macula, 13.1.0 or later}"
peer="${PEER:-dotnet run --project $root/scripts/interop/Peer -c Release --}"
work="$(mktemp -d)"
erl_name="macula-dotnet-sealed-$$"
cleanup() {
  podman rm -f "$erl_name" > /dev/null 2>&1 || true
  [ -n "${peer_pid:-}" ] && kill "$peer_pid" 2> /dev/null || true
  [ -n "${station_pid:-}" ] && kill "$station_pid" 2> /dev/null || true
  exec 3>&- || true
  rm -rf "$work"
}
trap cleanup EXIT

mkdir "$work/interop"
git -C "$MACULA_GO_DIR" fetch --quiet --tags origin
git -C "$MACULA_GO_DIR" show "$version:scripts/interop/erlang_sealed.escript" > "$work/interop/erlang_sealed.escript"
teststation="${MACULA_TESTSTATION:-}"
if [ -z "$teststation" ]; then
  GOBIN="$work" go install "github.com/macula-io/macula-go/teststation/cmd/teststation@$version"
  teststation="$work/teststation"
fi
mkfifo "$work/stations.in"
"$teststation" "$profile" < "$work/stations.in" > "$work/stations.out" &
station_pid=$!
exec 3> "$work/stations.in"

# first_line FILE PATTERN waits up to 180 s for a line of FILE matching PATTERN.
first_line() {
  for _ in $(seq 1 900); do
    line="$(grep -m1 -E "$2" "$1" 2> /dev/null || true)"
    [ -n "$line" ] && { echo "$line"; return 0; }
    sleep 0.2
  done
  echo "sealed.sh: no line matching $2 in $1" >&2
  cat "$1" >&2 || true
  return 1
}
info="$(first_line "$work/stations.out" '^\{')"
read -r host port station realm < <(python3 -c 'import json,sys; d=json.loads(sys.argv[1]); s=d["stations"][0]; print(s["host"], s["port"], s["node_id"], d["realm_id"])' "$info")
erl() {
  podman run --rm --name "$erl_name" --network host ${PODMAN_CGROUP_PARENT:+--cgroup-parent "$PODMAN_CGROUP_PARENT"} \
    -e MACULA_OFF_REFUSED=1 -e MACULA_SEALED_PEER=dotnet -e MACULA_SEAL_REPORT=1 \
    -v "$MACULA_BUILD:/macula:ro" -v "$work/interop:/interop:ro" \
    "$image" escript /interop/erlang_sealed.escript /macula/_build/default/lib/macula "$host" "$port" "$station" "$realm" "$profile" "$@"
}

echo "== $profile: an Erlang provider, a .NET caller"
erl serve 180 > "$work/erlang.out" 2>&1 < /dev/null &
first_line "$work/erlang.out" '^serving' > /dev/null
provider="$(first_line "$work/erlang.out" '^node ' | awk '{print $2}')"
$peer call "$host:$port@$station" "$profile" "$realm" "$provider"
podman rm -f "$erl_name" > /dev/null 2>&1 || true

echo "== $profile: a .NET provider, an Erlang caller"
$peer serve "$host:$port@$station" "$profile" "$realm" 180 > "$work/peer.out" 2>&1 < /dev/null &
peer_pid=$!
first_line "$work/peer.out" '^serving' > /dev/null
provider="$(first_line "$work/peer.out" '^node ' | awk '{print $2}')"
erl call "$provider" < /dev/null
echo "== $profile: both ways sealed, both reports sealed (.NET)"
