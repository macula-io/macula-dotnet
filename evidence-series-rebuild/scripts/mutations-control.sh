#!/usr/bin/env bash
# Mutation checks for the control stream reply check on uranus/series-rebuild,
# one after another in one worktree (each builds it). Needs a clean tree with
# the commit checked out.
#   mutations-control.sh [worktree]
set -uo pipefail

scratch="/tmp/claude-1000/-home-rl-work-github-com/3e4884aa-603d-4c5f-be80-8568ba5d22f3/scratchpad"
mutate="$scratch/run-dotnet-mutation.sh"
worktree="${1:-/home/rl/work/worktrees/macula-dotnet/uranus-series-rebuild}"
channel="src/Macula/Connection/ControlChannel.cs"
warnings="src/Macula/Connection/DropWarnings.cs"
tests="FullyQualifiedName~Macula.Tests.SessionReaderTests"
result_test="$tests.A_result_that_does_not_verify_leaves_the_call_pending"
error_test="$tests.An_error_that_does_not_verify_leaves_the_call_pending"
unparsed_test="$tests.A_signed_reply_that_does_not_parse_leaves_the_call_pending"

# 1. A signed reply that doesn't parse fails its call again, as before the fix.
"$mutate" "$worktree" reply-parse-fails-call "$channel" \
    'DropReply(type, DropReason.Malformed, callIdField);' \
    'if (callId is not null && _calls.TryRemove(Convert.ToHexStringLower(callId), out var failed)) { failed.TrySetException(new IOException("the reply did not parse")); }' \
    "$unparsed_test"

# 2. The reader never drops a reply for its signature.
"$mutate" "$worktree" reply-signer-never-drops "$channel" \
    'if (DropWarnings.ReplySignerCheck(frame) is { } unsigned)' \
    'if (DropWarnings.ReplySignerCheck(frame) is { } unsigned && unsigned == DropReason.NotAStreamOpen)' \
    "$result_test|$error_test"

# 3. A well-formed signature by another key passes.
"$mutate" "$worktree" signature-by-another-key-passes "$warnings" \
    '            _ => DropReason.InvalidSignature,' \
    '            _ => null,' \
    "$result_test|$error_test"

# 4. An ERROR is checked against responded_by instead of reported_by.
"$mutate" "$worktree" error-checked-against-responded-by "$warnings" \
    '== "error" ? "reported_by"' \
    '== "errors" ? "reported_by"' \
    "$error_test"

# 5. A RESULT is checked against reported_by instead of responded_by.
"$mutate" "$worktree" result-checked-against-reported-by "$warnings" \
    ': "responded_by");' \
    ': "reported_by");' \
    "$result_test"

git -C "$worktree" status --short
