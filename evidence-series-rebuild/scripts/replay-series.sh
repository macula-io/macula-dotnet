#!/usr/bin/env bash
# Replays the .NET series (origin/main..61e1fbb, before the combined commit) onto
# uranus/series-rebuild, which already carries the 0.4.1 release and the 0.4.2
# fixes. Skips the 0.4.1 carry-overs, folds the session-end level test into the
# session-end commit, and defers every CHANGELOG and version change to one final
# commit. After each commit: build, then the test classes the commit touches
# (offline only). Stops at the first unexpected conflict or failure, leaving the
# worktree as it is for inspection. Never pushes, never touches local main.
set -uo pipefail

repo=/home/rl/work/github.com/macula-io/macula-dotnet
wt=/home/rl/work/worktrees/macula-dotnet/uranus-series-rebuild
sp=/tmp/claude-1000/-home-rl-work-github-com/3e4884aa-603d-4c5f-be80-8568ba5d22f3/scratchpad
runner="$sp/run-dotnet-tests.sh"
deferred="CHANGELOG.md src/Macula/Macula.csproj"
session_url="https://claude.ai/code/session_012autaf2dnckUJ3RakEBrot"

steps="d1f67da ddb3e29 cdff56a cbbf756 080b424 99372c6 f1f0c35 0f90ac8 4bb2283 7ec12f4 80202e6 b48ba67 c4875ae+7ff3e6e 20c5611 7878e91"

fail() { echo "STOPPED: $*" >&2; git -C "$wt" status --short | head -20 >&2; exit 1; }

[ -z "$(git -C "$wt" status --porcelain)" ] || fail "rebuild worktree is not clean"

apply_commit() {
    local sha="$1"
    git -C "$wt" cherry-pick --no-commit "$sha" > /dev/null 2>&1
    local unmerged
    unmerged=$(git -C "$wt" diff --name-only --diff-filter=U)
    for file in $unmerged; do
        case " $deferred " in
            *" $file "*) git -C "$wt" checkout HEAD -- "$file" ;;
            *) fail "unexpected conflict in ${file} applying ${sha}" ;;
        esac
    done
    git -C "$wt" cherry-pick --quit > /dev/null 2>&1 || true
    for file in $deferred; do
        git -C "$wt" checkout HEAD -- "$file"
    done
}

commit_step() {
    local step="$1" first="${1%%+*}" message_file="$sp/replay-message.txt"
    git -C "$wt" log -1 --format=%B "$first" > "$message_file"
    if [ "$first" = "d1f67da" ]; then
        python3 - "$message_file" <<'PY' || exit 1
import sys
path = sys.argv[1]
text = open(path, encoding="utf-8").read()
old = "\n\nBumps the package to 0.5.0 and starts CHANGELOG.md."
if text.count(old) != 1:
    sys.exit("d1f67da message: the version sentence was not found exactly once")
open(path, "w", encoding="utf-8").write(text.replace(old, ""))
PY
    fi
    if [ "$step" != "$first" ]; then
        python3 - "$message_file" "$session_url" <<'PY' || exit 1
import sys
path, url = sys.argv[1:3]
text = open(path, encoding="utf-8").read()
cut = text.find("\n\nCo-Authored-By:")
if cut < 0:
    sys.exit("folded message: no trailer block found")
body = text[:cut].rstrip()
body += "\n\nThe test also closes a second session locally and expects exactly one\ninformation line for it, next to the single warning for the session the\nstation ended, matching macula-go's test."
text = body + "\n\nCo-Authored-By: Claude Opus 5 <noreply@anthropic.com>\nClaude-Session: " + url + "\n"
open(path, "w", encoding="utf-8").write(text)
PY
    fi
    git -C "$wt" diff --cached --quiet && fail "step ${step} staged nothing"
    local author date
    author=$(git -C "$wt" log -1 --format='%an <%ae>' "$first")
    date=$(git -C "$wt" log -1 --format=%aI "$first")
    git -C "$wt" commit --quiet --author="$author" --date="$date" -F "$message_file" || fail "commit of ${step}"
}

test_step() {
    local step="$1" label="replay-${1%%+*}"
    local classes filter
    classes=$(git -C "$wt" show --name-only --format='' HEAD | /usr/bin/grep -E '^tests/Macula.Tests/[A-Za-z]+Tests\.cs$' | sed -E 's#tests/Macula.Tests/([A-Za-z]+)\.cs#FullyQualifiedName~Macula.Tests.\1#' | paste -sd'|' -)
    "$runner" "$wt" "$label" build > "$sp/logs/${label}-build-summary.txt" 2>&1 || { cat "$sp/logs/${label}-build-summary.txt"; fail "build after ${step}"; }
    if [ -z "$classes" ]; then
        echo "  ${step}: built, no test class touched"
        return 0
    fi
    filter="(${classes})&Category!=Live"
    "$runner" "$wt" "$label" test "$filter" > "$sp/logs/${label}-test-summary.txt" 2>&1 || { cat "$sp/logs/${label}-test-summary.txt"; fail "tests after ${step}"; }
    echo "  ${step}: $(/usr/bin/grep -E 'Total tests|Passed:|No test matches' "$sp/logs/${label}-test-summary.txt" | tr -s ' ' | paste -sd' ' -)"
}

for step in $steps; do
    for sha in ${step//+/ }; do
        apply_commit "$sha"
    done
    commit_step "$step"
    echo "$(git -C "$wt" log -1 --format='%h %s' HEAD)"
    test_step "$step"
done
echo "REPLAY COMPLETE at $(git -C "$wt" rev-parse --short HEAD)"
