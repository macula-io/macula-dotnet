#!/usr/bin/env bash
# Finishes the .NET series rebuild on uranus/series-rebuild after the replay:
# applies the combined commit 61e1fbb as three one-purpose commits, then the
# version 0.5.0 and changelog commit, then checks that the rebuilt tree equals
# the guard reference (the series tip with the 0.4.2 fixes merged) outside the
# changelog. Builds and runs the touched test classes (offline) after every
# commit. Stops at the first surprise. Never pushes, never touches local main.
set -uo pipefail

repo=/home/rl/work/github.com/macula-io/macula-dotnet
wt=/home/rl/work/worktrees/macula-dotnet/uranus-series-rebuild
sp=/tmp/claude-1000/-home-rl-work-github-com/3e4884aa-603d-4c5f-be80-8568ba5d22f3/scratchpad
parts="$sp/split-61e1fbb/parts"
runner="$sp/run-dotnet-tests.sh"
combined=61e1fbb
before_combined=7878e91
guard=e2c122f

fail() { echo "STOPPED: $*" >&2; git -C "$wt" status --short | head -20 >&2; exit 1; }

changed_paths() { git -C "$wt" status --porcelain | awk '{ print $2 }' | sort | paste -sd' ' -; }

expect_paths() {
    local want
    want=$(echo "$*" | tr ' ' '\n' | sort | paste -sd' ' -)
    [ "$(changed_paths)" = "$want" ] || fail "changed paths are '$(changed_paths)', expected '${want}'"
}

build_and_test() {
    local label="$1" classes="$2"
    "$runner" "$wt" "$label" build > "$sp/logs/${label}-build-summary.txt" 2>&1 || { cat "$sp/logs/${label}-build-summary.txt"; fail "build for ${label}"; }
    [ -z "$classes" ] && { echo "  ${label}: built"; return 0; }
    "$runner" "$wt" "$label" test "(${classes})&Category!=Live" > "$sp/logs/${label}-test-summary.txt" 2>&1 || { cat "$sp/logs/${label}-test-summary.txt"; fail "tests for ${label}"; }
    echo "  ${label}: $(/usr/bin/grep -E 'Total tests|Passed:' "$sp/logs/${label}-test-summary.txt" | tr -s ' ' | paste -sd' ' -)"
}

commit_all() {
    local message="$1"
    git -C "$wt" add -A -- src tests CHANGELOG.md
    git -C "$wt" commit --quiet -F "$message" || fail "commit with ${message}"
    echo "$(git -C "$wt" log -1 --format='%h %s' HEAD)"
}

[ -z "$(git -C "$wt" status --porcelain)" ] || fail "rebuild worktree is not clean"
[ "$(git -C "$wt" log -1 --format=%s HEAD)" = "$(git -C "$repo" log -1 --format=%s "$before_combined")" ] || fail "HEAD is not the replayed ${before_combined}"

source_paths=$(git -C "$repo" show --name-only --format='' "$combined" | /usr/bin/grep -v '^CHANGELOG.md$' | paste -sd' ' -)
existing=""
for path in $source_paths; do
    git -C "$repo" cat-file -e "${before_combined}:${path}" 2>/dev/null && existing="$existing $path"
done
git -C "$wt" diff --quiet "$before_combined" HEAD -- $existing || fail "the rebuilt files the combined commit touches differ from ${before_combined}"

echo '######## part 1: CALL ucan_token typing'
git -C "$wt" checkout "$combined" -- src/Macula/Frame/CallFrames.cs
cp "$parts/SessionReaderTests.part1.cs" "$wt/tests/Macula.Tests/SessionReaderTests.cs"
expect_paths src/Macula/Frame/CallFrames.cs tests/Macula.Tests/SessionReaderTests.cs
build_and_test split-part1 'FullyQualifiedName~Macula.Tests.SessionReaderTests|FullyQualifiedName~Macula.Tests.FrameGoldenVectorTests'
commit_all "$parts/message-part1.txt"

echo '######## part 2: drop warnings and MalformedFrameException'
git -C "$wt" checkout "$combined" -- src/Macula/Connection/ControlChannel.cs src/Macula/Connection/DropWarnings.cs tests/Macula.Tests/CapturingListener.cs tests/Macula.Tests/DropWarningsTests.cs tests/Macula.Tests/SessionReaderTests.cs
cp "$parts/Session.part2.cs" "$wt/src/Macula/Connection/Session.cs"
expect_paths src/Macula/Connection/ControlChannel.cs src/Macula/Connection/DropWarnings.cs src/Macula/Connection/Session.cs tests/Macula.Tests/CapturingListener.cs tests/Macula.Tests/DropWarningsTests.cs tests/Macula.Tests/SessionReaderTests.cs
build_and_test split-part2 'FullyQualifiedName~Macula.Tests.DropWarningsTests|FullyQualifiedName~Macula.Tests.SessionReaderTests'
commit_all "$parts/message-part2.txt"

echo '######## part 3: STREAM_OPEN refusal and RefuseAsync'
git -C "$wt" checkout "$combined" -- src/Macula/Connection/FrameStream.cs src/Macula/Connection/Session.cs src/Macula/Streaming/StreamHandle.cs tests/Macula.Tests/StreamAcceptTests.cs
expect_paths src/Macula/Connection/FrameStream.cs src/Macula/Connection/Session.cs src/Macula/Streaming/StreamHandle.cs tests/Macula.Tests/StreamAcceptTests.cs
build_and_test split-part3 'FullyQualifiedName~Macula.Tests.StreamAcceptTests|FullyQualifiedName~Macula.Tests.FrameStreamTests|FullyQualifiedName~Macula.Tests.DropWarningsTests|FullyQualifiedName~Macula.Tests.SessionReaderTests'
commit_all "$parts/message-part3.txt"

echo '######## version 0.5.0 and changelog'
cp "$parts/CHANGELOG.final.md" "$wt/CHANGELOG.md"
python3 - "$wt/src/Macula/Macula.csproj" <<'PY' || fail "version line"
import sys
path = sys.argv[1]
text = open(path, encoding="utf-8", newline="").read()
old, new = "<Version>0.4.2</Version>", "<Version>0.5.0</Version>"
if text.count(old) != 1:
    sys.exit("version line not found exactly once")
open(path, "w", encoding="utf-8", newline="").write(text.replace(old, new))
PY
expect_paths CHANGELOG.md src/Macula/Macula.csproj
build_and_test final-version ''
commit_all "$parts/message-final.txt"

echo '######## guard: rebuilt tree vs the series tip with 0.4.2 merged, outside the changelog'
if git -C "$repo" diff --quiet "$guard" "$(git -C "$wt" rev-parse HEAD)" -- . ':(exclude)CHANGELOG.md'; then
    echo "GUARD PASSED: the rebuilt tree equals ${guard} apart from CHANGELOG.md"
else
    git -C "$repo" diff --stat "$guard" "$(git -C "$wt" rev-parse HEAD)" -- . ':(exclude)CHANGELOG.md'
    fail "the rebuilt tree differs from the guard reference"
fi
git -C "$repo" log --oneline origin/main..uranus/series-rebuild | wc -l
echo "FINISHED at $(git -C "$wt" rev-parse --short HEAD)"
