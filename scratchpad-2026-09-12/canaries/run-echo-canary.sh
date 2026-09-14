#!/usr/bin/env bash
# Echo canary for io.macula.echo from the macula-dotnet SDK. Scratch only.
#
#   run-echo-canary.sh build            build, then swap in the local OpenSSL-free libmsquic
#   run-echo-canary.sh check            no network: SDK version and whether QUIC loads
#   run-echo-canary.sh run [second]     one canary run: Frankfurt plus a second station (default Paris)
#
# A run makes at most four calls (two stations x "hello" and a map) and prints JSON lines,
# also saved under results/.
set -euo pipefail

here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project="$here/EchoCanary/EchoCanary.csproj"
configuration="Release"
target_framework="net10.0"
out_dir="$here/EchoCanary/bin/$configuration/$target_framework"
app_dll="$out_dir/EchoCanary.dll"
# System.Net.Quic cannot load Unofficial.MsQuic's libmsquic on this box (it needs OpenSSL 1.1).
# This locally built libmsquic has no OpenSSL dependency; see memory reference_dotnet_quic_gotchas.
msquic_substitute="/home/rl/work/github.com/beam-campus/bc-gitops-demo-web/deps/quicer/c_build/msquic/bin/Release/libmsquic.so.2.3.8"
primary_station="station-de-frankfurt.macula.io:4433"
default_second_station="station-fr-paris.macula.io:4433"
results_dir="$here/results"
source_commit="$(cat "$here/SOURCE_COMMIT")"

build() {
    dotnet build "$project" -c "$configuration"
    local count=0
    while IFS= read -r target; do
        cp -L "$msquic_substitute" "$target"
        count=$((count + 1))
    done < <(find "$out_dir" -name 'libmsquic.so')
    echo "replaced libmsquic.so in $count place(s)"
}

check() {
    MACULA_DOTNET_COMMIT="$source_commit" dotnet "$app_dll" --check
}

run_canary() {
    local run_id="${1:-}"
    local second_station="${2:-$default_second_station}"
    if [ -z "$run_id" ]; then
        echo "run needs a run id, for example: $0 run baseline" >&2
        exit 2
    fi
    local stamp
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    mkdir -p "$results_dir"
    MACULA_DOTNET_COMMIT="$source_commit" dotnet "$app_dll" "$run_id" "$primary_station" "$second_station" \
        | tee "$results_dir/$run_id-$stamp.jsonl"
}

case "${1:-}" in
    build) build ;;
    check) check ;;
    run) run_canary "${2:-}" "${3:-}" ;;
    *) echo "usage: $0 build | check | run <run-id> [second-station host:port]" >&2; exit 2 ;;
esac
