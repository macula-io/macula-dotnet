#!/usr/bin/env bash
# Secures the evidence behind the .NET series rebuild and its follow-up commits
# (build, test and mutation logs, the replay and split scripts, commit message
# parts) on a local WIP branch, before the .NET lane parks. The scratchpad lives
# under /tmp and does not survive a reboot. Leaves out notes from reviews of
# other repositories, transcript tools, and what wip/uranus-0.4.2-evidence
# already holds. Never pushes. Leaves local main alone.
set -euo pipefail

scratch="/tmp/claude-1000/-home-rl-work-github-com/3e4884aa-603d-4c5f-be80-8568ba5d22f3/scratchpad"
worktree="/home/rl/work/worktrees/macula-dotnet/uranus-0.4.2-evidence"
branch="wip/uranus-series-rebuild-evidence"
secured_logs="evidence-0.4.2/logs"
target="evidence-series-rebuild"
session_url="https://claude.ai/code/session_012autaf2dnckUJ3RakEBrot"

if git -C "$worktree" show-ref --verify --quiet "refs/heads/${branch}"; then
    echo "branch ${branch} already exists, refusing" >&2
    exit 1
fi
if [ -n "$(git -C "$worktree" status --porcelain)" ]; then
    echo "worktree ${worktree} is not clean, refusing" >&2
    exit 1
fi

git -C "$worktree" switch --quiet -c "$branch"
dest="$worktree/$target"
mkdir -p "$dest/logs" "$dest/scripts" "$dest/messages" "$dest/split-61e1fbb"

# The logs the 0.4.2 evidence doesn't hold yet.
comm -23 <(/usr/bin/ls "$scratch/logs" | sort) <(git -C "$worktree" ls-tree --name-only "HEAD:${secured_logs}" | sort) |
    while IFS= read -r name; do
        cp -p "$scratch/logs/$name" "$dest/logs/"
    done

cp -p "$scratch/replay-series.sh" "$scratch/apply-split-and-finish.sh" "$scratch/run-dotnet-tests.sh" \
    "$scratch/run-dotnet-mutation.sh" "$scratch/reply-verification/mutations-control.sh" \
    "$scratch/secure-series-rebuild-evidence.sh" "$dest/scripts/"
cp -p "$scratch/replay-message.txt" "$scratch/merge-0.4.2-message.txt" "$scratch/series-messages.txt" \
    "$scratch/series-files.txt" "$scratch/petname-files.txt" "$scratch/candidate-files.txt" \
    "$scratch"/reply-verification/message-*.txt "$dest/messages/"
cp -rp "$scratch/split-61e1fbb/." "$dest/split-61e1fbb/"

cat > "$dest/README.md" <<'README'
# Evidence behind the .NET series rebuild (WIP, local only, never push)

Copied out of a session scratchpad under /tmp, which does not survive a reboot,
when the .NET lane parked on 2026-09-14.

- logs/: build, test and mutation logs written after the 0.4.2 evidence
  (wip/uranus-0.4.2-evidence): the series replay, the 61e1fbb split, the tree
  guard, the follow-up commits on uranus/series-rebuild up to 682ef6d, and the
  red run of the WIP commit on wip/uranus-series-rebuild-2026-09-14.
- scripts/: the replay and split scripts, the test and mutation runners, the
  mutation checks for 682ef6d, and this securing script.
- messages/: commit message parts and file lists the replay used, and the
  messages of the reply check commits.
- split-61e1fbb/: the three-part split of 61e1fbb, with its intermediate files.

Left out: notes from reviews of other repositories, transcript tools, and what
the 0.4.2 evidence already holds (frame samples, decoder cost).
README

file_count=$(find "$dest" -type f | wc -l)
git -C "$worktree" add -f -- "$target"
git -C "$worktree" commit --quiet -F - <<MSG
WIP: keep the logs, scripts and message parts behind the series rebuild

Build, test and mutation logs, the replay and split scripts and the commit
message parts behind uranus/series-rebuild, copied from a session scratchpad
so they survive a reboot. Local only.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: ${session_url}
MSG

committed_count=$(git -C "$worktree" ls-tree -r --name-only HEAD -- "$target" | wc -l)
echo "copied files: ${file_count}, committed files: ${committed_count}"
git -C "$worktree" log -1 --format='%h %s'
git -C "$worktree" ls-tree -r --name-only HEAD -- "$target" | awk -F/ '{ print $2 }' | sort | uniq -c
printf 'bin, obj or go paths committed: '
git -C "$worktree" ls-tree -r --name-only HEAD -- "$target" | /usr/bin/grep -c -E '(^|/)(bin|obj|go)/' || true
git -C "$worktree" status --short | head -3
