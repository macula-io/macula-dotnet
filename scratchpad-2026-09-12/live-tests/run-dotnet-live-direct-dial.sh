#!/usr/bin/env bash
# Runs a filtered set of macula-dotnet's live tests once (default: DirectDialTests, DirectDialUcanTests).
#   run-dotnet-live-direct-dial.sh [dotnet-test-filter]
# System.Net.Quic can't load Unofficial.MsQuic's libmsquic on this box (it needs OpenSSL 1.1),
# so the locally built OpenSSL-free libmsquic is copied into the test output first.
# Scratch only; see memory reference_dotnet_quic_gotchas.
set -euo pipefail

repo="/home/rl/work/github.com/macula-io/macula-dotnet"
configuration="Debug"
target_framework="net10.0"
out_dir="$repo/tests/Macula.Tests/bin/$configuration/$target_framework"
msquic_substitute="/home/rl/work/github.com/beam-campus/bc-gitops-demo-web/deps/quicer/c_build/msquic/bin/Release/libmsquic.so.2.3.8"
default_filter="FullyQualifiedName~Macula.Tests.DirectDialTests|FullyQualifiedName~Macula.Tests.DirectDialUcanTests"
filter="${1:-$default_filter}"
results_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/results"

dotnet build "$repo/tests/Macula.Tests" -c "$configuration"

replaced=0
while IFS= read -r target; do
    cp -L "$msquic_substitute" "$target"
    replaced=$((replaced + 1))
done < <(find "$out_dir" -path '*linux-x64*' -name 'libmsquic.so')
echo "replaced libmsquic.so in $replaced place(s)"

mkdir -p "$results_dir"
dotnet test "$repo/tests/Macula.Tests" --no-build -c "$configuration" --filter "$filter" \
    --logger "console;verbosity=normal" \
    | tee "$results_dir/dotnet-live-$(date -u +%Y%m%dT%H%M%SZ).log"
