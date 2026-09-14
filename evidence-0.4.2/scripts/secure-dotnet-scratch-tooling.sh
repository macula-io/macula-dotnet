#!/usr/bin/env bash
# Secures the previous Uranus session's .NET scratchpad (lost on reboot) on a local
# WIP branch in its own worktree: tool source, canary apps, frame samples, and the
# red, green, live and mutation logs. Leaves out built binaries, key files, the
# git-archive copy of the repo, third-party research copies and Rust files
# (Neptune's). Never pushes. Leaves local main alone.
set -euo pipefail

repo="/home/rl/work/github.com/macula-io/macula-dotnet"
old="/tmp/claude-1000/-home-rl-work-github-com/6400ff64-212b-4343-8b3d-30bdddd30d87/scratchpad"
branch="wip/uranus-scratchpad-2026-09-12"
worktree="/home/rl/work/worktrees/macula-dotnet/uranus-scratchpad-2026-09-12"
base_ref="origin/main"
target="scratchpad-2026-09-12"
session_url="https://claude.ai/code/session_012autaf2dnckUJ3RakEBrot"

if git -C "$repo" show-ref --verify --quiet "refs/heads/${branch}"; then
    echo "branch ${branch} already exists, refusing" >&2
    exit 1
fi
if [ -e "$worktree" ]; then
    echo "worktree path ${worktree} already exists, refusing" >&2
    exit 1
fi

git -C "$repo" worktree add -b "$branch" "$worktree" "$base_ref"
dest="$worktree/$target"

copy_matching() {
    local to="$1"
    shift
    mkdir -p "$dest/$to"
    local file
    for file in "$@"; do
        [ -f "$file" ] || continue
        cp -p "$file" "$dest/$to/"
    done
}

copy_matching live-tests "$old/run-dotnet-live-direct-dial.sh"
copy_matching live-tests/results "$old"/results/dotnet-live-*.log
copy_matching canaries "$old"/echo-canary-dotnet/*.sh "$old"/echo-canary-dotnet/*.log "$old/echo-canary-dotnet/SOURCE_COMMIT"
copy_matching canaries/EchoCanary "$old"/echo-canary-dotnet/EchoCanary/*.cs "$old"/echo-canary-dotnet/EchoCanary/*.csproj
copy_matching canaries/TubeCanary "$old"/echo-canary-dotnet/TubeCanary/*.cs "$old"/echo-canary-dotnet/TubeCanary/*.csproj
copy_matching canaries/results "$old"/echo-canary-dotnet/results/*
copy_matching frame-samples/generator "$old"/frame-samples-dotnet/*.cs "$old"/frame-samples-dotnet/*.csproj
copy_matching frame-samples/dotnet "$old"/frame-samples/dotnet/*.bin
copy_matching logs "$old"/dotnet-*.log "$old"/dotnet-*.out "$old"/dotnet-*.msg "$old"/dotnet-*-commit.txt "$old"/slot-dotnet-*.log
copy_matching mutations "$old"/mut-dotnet-*.log "$old"/m-*.cs
copy_matching backups "$old/Session.cs.bak" "$old/DirectDial_main.cs"
copy_matching issue-1 "$old"/dotnet_issue_*

cat > "$dest/README.md" <<'README'
# .NET lane scratchpad, 09-10 to 09-12 (WIP, local only, never push)

Copied out of a session scratchpad under /tmp, which does not survive a reboot.

- live-tests/: the live test runner (swaps in an OpenSSL-free libmsquic built
  elsewhere on this workstation) and the live run logs it wrote.
- canaries/: the .NET callers and scripts from the hecate-echo and hecate-tube
  canaries, their build and check logs, and their results. They were built against
  a `git archive` of this repo at SOURCE_COMMIT, so project references point at
  `../src/Macula`. Identities are generated at run time; the only constants are
  the public realm id and a clip id.
- frame-samples/: the generator and the 15 frame samples it wrote for cross-SDK
  decoder tests. Samples carry a throwaway identity's public key and signature only.
- logs/: red, green, offline, live and build logs, and draft commit messages, for
  the commits on local main from d1f67da to 61e1fbb and the 0.4.1 release.
- mutations/: mutation-check logs and the mutated source copies they ran.
- backups/: two source files as they stood before an edit.
- issue-1/: the draft and duplicate search for issue #1.

Left out: build output, the git-archive copy of the repo, copies of third-party
sources fetched for research, and every Rust file.
README

file_count=$(find "$dest" -type f | wc -l)
git -C "$worktree" add -f -- "$target"
git -C "$worktree" commit --quiet -F - <<MSG
WIP: keep the .NET lane scratchpad from 09-10 to 09-12

Tool source, canary callers, frame samples and the test and mutation logs,
copied from a session scratchpad so they survive a reboot. Local only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: ${session_url}
MSG

committed_count=$(git -C "$repo" ls-tree -r --name-only "$branch" -- "$target" | wc -l)
echo "copied files: ${file_count}, committed files: ${committed_count}"
git -C "$repo" log -1 --format='%h %s' "$branch"
git -C "$repo" ls-tree -r --name-only "$branch" -- "$target" | awk -F/ '{ print $2 }' | sort | uniq -c
printf 'rust, bin or obj paths committed: '
git -C "$repo" ls-tree -r --name-only "$branch" -- "$target" | /usr/bin/grep -c -i -E '(^|/)(bin|obj|target)/|\.rs$|rust' || true
