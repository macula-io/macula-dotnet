#!/usr/bin/env bash
# Builds, or runs the offline tests of, macula-dotnet in one worktree, the way CI
# does (Release, Category!=Live by default). Test runs get a GC heap cap and a
# timeout, so a red test that allocates or loops without bound fails inside the
# test host instead of pressing on the shared machine. Logs go to the scratchpad.
#   run-dotnet-tests.sh <worktree> <label> build
#   run-dotnet-tests.sh <worktree> <label> test [filter]
# Prints the run's outcome lines in full, then at most 40 failure or build error
# lines, so a long run never hides its outcome.
set -uo pipefail

if [ "$#" -lt 3 ]; then
    echo "usage: run-dotnet-tests.sh <worktree> <label> build|test [filter]" >&2
    exit 2
fi

worktree="$1"
label="$2"
mode="$3"
filter="${4:-Category!=Live}"

scratch="/tmp/claude-1000/-home-rl-work-github-com/3e4884aa-603d-4c5f-be80-8568ba5d22f3/scratchpad"
logs="$scratch/logs"
configuration="Release"
heap_limit_hex="0x40000000"
test_timeout_seconds=600
project="$worktree/tests/Macula.Tests"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
log="$logs/${label}-${mode}-${stamp}.log"
outcome_pattern='Test Run Successful|Test Run Failed|Test Run Aborted|Total tests:|^[[:space:]]+(Passed|Failed|Skipped): |No test matches|Build succeeded|Build FAILED|aborted|crashed|Stack overflow'
failure_pattern='error CS|^[[:space:]]+Failed |OutOfMemory'

mkdir -p "$logs"

case "$mode" in
    build)
        dotnet build "$project" --configuration "$configuration" > "$log" 2>&1
        exit_code=$?
        ;;
    test)
        DOTNET_GCHeapHardLimit="$heap_limit_hex" timeout "$test_timeout_seconds" \
            dotnet test "$project" --no-build --configuration "$configuration" \
            --filter "$filter" --logger "console;verbosity=normal" > "$log" 2>&1
        exit_code=$?
        ;;
    *)
        echo "mode must be build or test" >&2
        exit 2
        ;;
esac

echo "exit ${exit_code}, log ${log}"
/usr/bin/grep -E "$outcome_pattern" "$log" | awk '{ print substr($0, 1, 240) }'
/usr/bin/grep -E "$failure_pattern" "$log" | awk '{ print substr($0, 1, 240) }' | head -40
printf 'warnings: '
/usr/bin/grep -c 'warning CS' "$log"
exit "$exit_code"
