#!/usr/bin/env bash
# hecate-tube canary caller for tube.watch_video_clip from the macula-dotnet SDK. Scratch only.
#
#   run-tube-canary.sh build          build, then swap in the local OpenSSL-free libmsquic
#   run-tube-canary.sh check          no network: SDK version and whether QUIC loads
#   run-tube-canary.sh run <run-id>   one run: station path, then direct dial, two identities
#
# Output is JSON lines, also saved under results/.
set -euo pipefail

here="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project="$here/TubeCanary/TubeCanary.csproj"
configuration="Release"
target_framework="net10.0"
out_dir="$here/TubeCanary/bin/$configuration/$target_framework"
app_dll="$out_dir/TubeCanary.dll"
# System.Net.Quic cannot load Unofficial.MsQuic's libmsquic on this box (it needs OpenSSL 1.1).
# This locally built libmsquic has no OpenSSL dependency; see memory reference_dotnet_quic_gotchas.
msquic_substitute="/home/rl/work/github.com/beam-campus/bc-gitops-demo-web/deps/quicer/c_build/msquic/bin/Release/libmsquic.so.2.3.8"
station_route="station-de-frankfurt.macula.io:4433"
clip_id="clip-01a0471969fd71c2930cc459a797e6ca"
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
    if [ -z "$run_id" ]; then
        echo "run needs a run id, for example: $0 run baseline" >&2
        exit 2
    fi
    local stamp
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    mkdir -p "$results_dir"
    MACULA_DOTNET_COMMIT="$source_commit" dotnet "$app_dll" "$run_id" "$clip_id" "$station_route" \
        | tee "$results_dir/tube-$run_id-$stamp.jsonl"
}

diag() {
    local stamp
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    mkdir -p "$results_dir"
    MACULA_DOTNET_COMMIT="$source_commit" dotnet "$app_dll" --dht-diag "$station_route" \
        | tee "$results_dir/tube-dht-diag-$stamp.jsonl"
}

case "${1:-}" in
    build) build ;;
    check) check ;;
    run) run_canary "${2:-}" ;;
    diag) diag ;;
    *) echo "usage: $0 build | check | run <run-id> | diag" >&2; exit 2 ;;
esac
