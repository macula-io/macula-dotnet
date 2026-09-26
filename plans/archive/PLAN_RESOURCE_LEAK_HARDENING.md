# PLAN_RESOURCE_LEAK_HARDENING.md

**Status:** Survey complete — hardening not started
**Created:** 2026-09-12
**Last Updated:** 2026-09-12

## Overview

Survey of `src/Macula` for potential memory and resource leaks. No code was
changed; every finding below is read-only analysis with file:line references
against the current tree. The dominant theme: **dedicated QUIC streams and
`CancellationTokenSource`s have no disposal safety net**, and two public
entry points (`ContentTransfer`, `StreamHandle`) never release streams they
open on both success and failure paths.

General hygiene found to be GOOD and not repeated here: `ConnectCoreAsync`
disposes connection+control stream on every failure path (Session.cs:228-236),
pool's `CloseSessionAsync` bounds its close with a CTS (StationPool.cs:1075-1091),
the `DialedSession` lease pattern is correct, and `Envelope.MaxFrameBytes`
(~16MB) caps `FrameStream` buffer growth.

---

## Findings (ranked)

### CRITICAL

#### F1. `ContentTransfer` never releases its dedicated stream — success OR failure
`ContentTransfer.cs:72` (`PutAsync`) and `:95` (`GetAsync`).

Every put/get opens a dedicated bidirectional QUIC stream via
`session.OpenDedicatedStreamAsync` and **never** calls
`FinishAndStopReadingAsync`/`AbortBothAsync` on it — not on success, not on
any of the throw paths (`HashMismatch`, `VerifyFailed`, `RemoteError`,
timeout, cancellation, session end). `FrameStream` is not `IDisposable`, and
the underlying `QuicStream` is only released by an explicit
abort/finish or whole-connection teardown. Consequences:

- Each transfer permanently consumes an outbound stream slot on the
  connection; after enough transfers the station's stream limit is exhausted
  and `OpenOutboundStreamAsync` starts failing/blocking.
- Each abandoned stream retains its `FrameStream` buffer (up to ~16MB, see F6).

This is the single most important fix: give `ContentTransfer` a teardown
(finish-and-stop on success, abort on failure) via try/finally, matching
`macula_content_transfer.erl`'s own stream closure.

#### F2. `StreamHandle.AcceptAsync` abandons the accepted stream on non-refusal failures
`StreamHandle.cs:99-120`, specifically 107-108 + 116-119.

`OpenInboundAsync` only releases the stream via `RefuseAsync` for its four
handled shapes (CBOR decode, frame-too-large, not-a-STREAM_OPEN, parse
failure). If it instead throws — `OperationCanceledException` when the
timeout fires mid-first-frame-read, `EndOfStreamException` when the peer
opens then immediately resets, or any `QuicException`/`IOException` — the
just-accepted QUIC stream is never aborted/disposed. The peer gets no
RESET/STOP_SENDING (its side can hang) and the local stream slot is consumed
until connection teardown. The OCE path also misclassifies as
`TimeoutException` (see F4).

Fix: make the invariant explicit — `OpenInboundAsync` either returns an
`Inbound` or has freed the stream; abort in a catch before rethrowing.

#### F3. `StreamHandle.OpenAsync` leaks the stream if the STREAM_OPEN write fails
`StreamHandle.cs:64-82`.

The stream is opened at :66 and the signed STREAM_OPEN written at :80. If
`SendFrameAsync` throws (send timeout, session ended, `ct` cancelled), the
freshly opened stream is abandoned with no abort. Same slot-consumption
consequence as F2, on the caller side.

### HIGH

#### F4. `when (!ct.IsCancellationRequested)` misclassifies any OCE as a timeout
Five sites share the pattern:

- `StreamHandle.cs:116` (AcceptAsync) and `:273` (`RecvFrameTimeoutAsync`)
- `Session.cs:406` (`ServeOneCallGatedAsync`) and `:192` (handshake)
- `Subscription.cs:79` (`RecvEventAsync`)
- `FrameStream.cs:163` (`CallAsync`)

`System.Net.Quic` throws `OperationCanceledException` on connection dispose
and aborted streams. When a session is closed concurrently, all of these
report "timed out" — a lie that also destroys the operation's debug context
(the real exception is discarded). Not a memory leak, but resource-lifetime
hardening belongs in this pass: prefer
`when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)`
or compare `e.CancellationToken == timeoutCts.Token`.

#### F5. `ContentTransfer.GetAsync` trusts the manifest's declared size
`ContentTransfer.cs:108`: `var buffer = new byte[manifest.Size]` from an
untrusted wire manifest. A malicious or corrupt `manifest.Size` drives an
unbounded allocation (OOM) before any chunk verification. Chunk writes at
:118 also trust `Size` alignment. Fix: validate `Size` against
`ChunkCount * chunk size` (and a sane cap) before allocating, and hash-verify
incrementally.

#### F6. No `IDisposable` safety net on `StreamHandle`/`FrameStream`
`FrameStream.cs:13-49`, `StreamHandle.cs:43-56`.

Stream teardown is entirely convention-driven ("call `AbortAsync`, not just
drop the handle" — StreamHandle.cs:28-30). A handle that escapes its scope
(exception unwind, app bug) silently holds: the `QuicStream`, its
`_sendGate` semaphore, and the `_buf` array which grows by doubling up to
`MaxFrameBytes` (~16MB) and **never shrinks** (FrameStream.cs:195-207).
Consider `IAsyncDisposable` on `StreamHandle` that aborts if the stream
wasn't already finished — it converts all of F1-F3 from leaks into
recoverable misuse.

### MEDIUM

#### F7. Unbounded task fan-out for inbound CALLs
`StationPool.cs:1046-1061` (`ServeLinkCallsAsync`): one `Task.Run` per
inbound CALL, no concurrency cap, no backpressure. A CALL flood (or slow
handlers) creates unbounded tasks; each awaits `BuildCallReplyAsync` and a
handler, holding payload + closures. The session-side queue caps at 64
(ControlChannel.cs:109) but the pool's dispatch does not. Consider a
bounded dispatch (`SemaphoreSlim`) or `Parallel.ForEachAsync`-style
concurrency limit.

#### F8. `EventDedup` growth between sweeps
`EventDedup.cs:30-55`: `_seen` is bounded only by event rate ×
`DedupWindow` (default 60s). At high event rates the dictionary can grow
large between sweeps and `Sweep()` is O(all entries). Not unbounded (entries
are removed), but a memory-pressure knob under sustained traffic; consider
a rate cap or bucket sweep.

#### F9. Fire-and-forget close task in Session's end callback
`Session.cs:117`: `_ = Task.Run(() => CloseAsync().AsTask())`. If
`_connection.CloseAsync(0)`/`DisposeAsync` throws inside `CloseOnceAsync`
(the GOODBYE send is guarded, those are not), the fault is unobserved and
swallowed. Attach an observation continuation.

#### F10. `RunPublisherAsync` CTS ownership and unobserved callbacks
`SupervisedPubSub.cs:59-99`: the returned `CancellationTokenSource` is
caller-owned but the contract never says to dispose it; a caller that
doesn't leaks it. Inside the background task, an `onDone` that throws
faults the task unobserved (:81, :95).

#### F11. Dead `Subscription` objects retained by the ended channel
`ControlChannel.cs:669-676` (`End`): subscriptions are `End()`ed but not
removed from `_subscriptions`. A Session object the application keeps
referenced after close retains every subscription (each with its 256-slot
event channel) until the Session itself is GC'd. Clear the list on `End`.

#### F12. Static `OpenSessions.Live` registry depends on app-side dispose
`OpenSessions.cs:10-13` + `Session.DisposeAsync`. A `Session` the
application drops without `DisposeAsync`/`CloseAsync` stays in the
process-lifetime static dictionary with its live `QuicConnection`, reader
task, and buffers — a genuine process-lifetime leak reachable from any
caller of `Session.ConnectAsync` that fails to dispose. No finalizer
exists. Consider a debug-mode registry watchdog or a finalizer on
`Session` as a last-chance close.

### LOW / hygiene

- `ControlChannel._stop` (ControlChannel.cs:138) and `StationPool._poolCts`
  (StationPool.cs:233) CTS instances are never disposed. No timer is
  attached so the cost is trivial — dispose for correctness.
- `StationPool._linkTasks` (StationPool.cs:244) accumulates completed Task
  objects forever (append-only queue). Bounded by the number of links ever
  spawned; harmless today, prune on dispose.
- `AcceptAsync`'s total-budget loop (StreamHandle.cs:105-114): a peer that
  floods refused opens holds the accept loop busy until the deadline, and
  the refusal path consumes the shared timeout budget. A per-attempt budget
  or refusal cap would bound this (ties into F2's fix).
- `KeyPair.Save` (KeyPair.cs:146) leaves a `.tmp` file behind if the write
  fails before `File.Move`.

---

## Phases

- [ ] Phase 1 — Stream lifetime invariant: make every `FrameStream`
      producer own teardown on failure. Fix F1 (ContentTransfer
      try/finally), F2 (AcceptAsync abort-on-throw), F3 (OpenAsync
      abort-on-throw). Test: repeated put/get and accept-timeout cycles
      must not grow stream counts on a live session.
- [ ] Phase 2 — OCE classification (F4): tighten all five filters to
      require the local timeout CTS to be the cancelled source.
- [ ] Phase 3 — Untrusted-input bounds (F5): validate manifest size before
      allocation; incremental hash verify.
- [ ] Phase 4 — Safety net (F6): `IAsyncDisposable` on `StreamHandle`;
      dispose aborts unfinished streams. Optionally shrink-or-release the
      `FrameStream` buffer on finish.
- [ ] Phase 5 — Backpressure and retention (F7, F8, F11, F12): bounded
      inbound-call dispatch, dedup sweep under load, clear `_subscriptions`
      on `End`, dispose watchdog for the static registry.
- [ ] Phase 6 — Hygiene (F9, F10, LOW): observation continuations on
      fire-and-forget tasks, CTS disposal, `_linkTasks` prune.

## Files to Create/Modify

| File | Purpose | Status |
|------|---------|--------|
| `src/Macula/Content/ContentTransfer.cs` | F1 stream teardown, F5 size validation | Not started |
| `src/Macula/Streaming/StreamHandle.cs` | F2/F3 abort-on-throw, F4, F6 disposable | Not started |
| `src/Macula/Connection/FrameStream.cs` | F6 buffer release/dispose support | Not started |
| `src/Macula/Connection/Session.cs` | F4 (serve/handshake), F9, F12 watchdog | Not started |
| `src/Macula/Connection/Subscription.cs` | F4 | Not started |
| `src/Macula/Connection/ControlChannel.cs` | F11, LOW CTS dispose | Not started |
| `src/Macula/Connection/StationPool.cs` | F7, LOW (_linkTasks prune) | Not started |
| `src/Macula/Connection/EventDedup.cs` | F8 | Not started |
| `src/Macula/Connection/SupervisedPubSub.cs` | F10 | Not started |
| `src/Macula/Identity/KeyPair.cs` | LOW tmp-file cleanup | Not started |

## Success Criteria

- [ ] A live-session stress test (N sequential `ContentTransfer.PutAsync`/
      `GetAsync` calls) shows no growth in open-stream count and succeeds
      beyond N where it previously exhausted the stream budget.
- [ ] `AcceptAsync` timeout against a peer that never completes a
      STREAM_OPEN leaves no live QUIC stream behind (repeatable 1000x,
      zero growth).
- [ ] Concurrent `CloseAsync` during any of the five F4 sites raises
      `SessionEndedException`/`IOException`, never a `TimeoutException`.
- [ ] A malformed manifest (huge `Size`, mismatched chunks) fails fast
      without a large allocation.
- [ ] Disposing a `StreamHandle` that never finished aborts the stream;
      zero observable leaks in a loop that drops handles.
- [ ] All tests green: `dotnet test tests/Macula.Tests`.
