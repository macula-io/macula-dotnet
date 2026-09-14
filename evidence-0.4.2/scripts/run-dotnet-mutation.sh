#!/usr/bin/env bash
# Mutation check for one committed fix in a macula-dotnet worktree: replaces one
# exact piece of text in one file, builds, runs the given tests and expects at
# least one of them to fail, then restores the file from HEAD. A replacement
# whose text does not occur exactly once is refused, so a mutation that changes
# nothing can never be reported as caught.
#   run-dotnet-mutation.sh <worktree> <label> <file in worktree> <old text> <new text> <test filter>
set -uo pipefail

if [ "$#" -ne 6 ]; then
    echo "usage: run-dotnet-mutation.sh <worktree> <label> <file> <old> <new> <filter>" >&2
    exit 2
fi

worktree="$1"
label="$2"
file="$3"
old_text="$4"
new_text="$5"
filter="$6"
scratch="/tmp/claude-1000/-home-rl-work-github-com/3e4884aa-603d-4c5f-be80-8568ba5d22f3/scratchpad"
runner="$scratch/run-dotnet-tests.sh"
target="$worktree/$file"

if ! git -C "$worktree" diff --quiet -- "$file"; then
    echo "${file} has uncommitted changes; refusing to mutate it" >&2
    exit 2
fi

python3 - "$target" "$old_text" "$new_text" <<'PY'
import sys
path, old, new = sys.argv[1:4]
with open(path, encoding="utf-8", newline="") as source:
    text = source.read()
count = text.count(old)
if count != 1:
    sys.exit(f"mutation text occurs {count} times, expected exactly once")
with open(path, "w", encoding="utf-8", newline="") as source:
    source.write(text.replace(old, new))
PY
applied=$?
if [ "$applied" -ne 0 ] || git -C "$worktree" diff --quiet -- "$file"; then
    echo "mutation ${label} was not applied" >&2
    git -C "$worktree" checkout -- "$file"
    exit 2
fi

"$runner" "$worktree" "mutation-${label}" build > /dev/null
built=$?
test_exit=0
if [ "$built" -eq 0 ]; then
    "$runner" "$worktree" "mutation-${label}" test "$filter" | /usr/bin/grep -E '^exit|Total tests|Failed |aborted'
    test_exit=${PIPESTATUS[0]}
fi

git -C "$worktree" checkout -- "$file"
if ! git -C "$worktree" diff --quiet -- "$file"; then
    echo "WARNING: ${file} was not restored" >&2
    exit 3
fi

if [ "$built" -ne 0 ]; then
    echo "mutation ${label}: BUILD FAILED, not a usable mutation"
    exit 1
fi
if [ "$test_exit" -ne 0 ]; then
    echo "mutation ${label}: caught (tests failed), ${file} restored"
    exit 0
fi
echo "mutation ${label}: NOT caught (tests passed), ${file} restored"
exit 1
