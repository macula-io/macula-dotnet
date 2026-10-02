# PLAN_ACTOR_ARCHITECTURE_DECISION.md

**Status:** Decision pending
**Created:** 2026-09-12
**Last Updated:** 2026-09-12

## End goal

> This exists so the C# SDK gets actor-model guarantees (supervision, bounded
> mailboxes, crash isolation) without ending up with two C# SDKs to maintain.

## Context

- `macula-dotnet` is a pre-1.0 NuGet package (`Macula` 0.5.0), actively
  developed, nothing in production depends on it. Breaking changes are cheap
  right now — the window where this decision is cheapest will close.
- The 2026-09-12 leak surveys of the SDK family showed every hand-rolled
  port drifts from the Erlang core in lifecycle bugs (dotnet F1-F3 stream
  leaks, F7 unbounded fan-out; Erlang F1 immortal hosts, F4 unbounded inbox,
  F5 stuck handlers). The Erlang core is immune to several of these classes
  *because* of OTP discipline — supervisors, bounded mailboxes, monitors.
  That is the honest motivation for this decision.
- `macula-dotnet` is already ~80% actor-shaped: `ControlChannel` reader/
  writer loops are actors, `Channel`s are bounded mailboxes (256/64 caps),
  `SessionEndedException` is deathwatch, `DisposeAsync` is shutdown. What is
  missing is **supervision** (restart policy, per-component isolation), not
  message-passing.

## Options

### A — Harden macula-dotnet with an internal supervision layer

Add a small supervisor primitive (order of 200 lines) over the existing
task/channel architecture: restart policies for the reader/writer/subscriber
pumps, per-component failure isolation (a subscriber pump crash must not end
the session), mailbox drop policies already present but made declarative.
One C# SDK remains. No new framework dependency.

### B — Build `macula-dotakka` (Akka.NET) as the successor, archive A

A new repo using Akka.NET's actors, supervision trees, `BoundedMailbox`,
`DeathWatch`, and FSM handshake states (a 1:1 mapping to `gen_statem`).
Commits publicly to *replacing* `macula-dotnet`: A is archived once B
reaches wire parity + passes a hardening suite. Deliberately does NOT use
Akka.Remote/Cluster — macula's mesh is its own transport; shipping a second
one is confusion, not leverage.

### C — Parallel `macula-dotakka` alongside A (rejected, listed for the record)

Two C# SDKs tracked forever. Doubles the wire-protocol tracking cost the
surveys just demonstrated; every future protocol change lands twice. Only
defensible as a throwaway experiment, not as product.

## Evaluation

| Criterion | A (harden) | B (replace with Akka.NET) |
|-----------|-----------|---------------------------|
| **Supervision** (restart policy, crash isolation) | Partial: hand-rolled, covers the known failure modes, no framework guarantees | Native: supervision trees, `OneForOne`, per-stream actor children |
| **Backpressure** | Already present (`Channel` caps 64/256); declarative drop policies would be new work | Config-declared `BoundedMailbox` with drop-head/drop-tail, for free |
| **Wire-parity / drift risk** | One C# SDK, no drift | Drift only if A is not actually sunset; must enforce the archive commitment |
| **Re-hardening cost** | Zero — keeps the StationPool CTS-race fixes, close-data-loss finding, adversarial-review hardening | High — re-learns each against the live fleet (the Go/Rust ports each hit real data-loss bugs; expect similar here) |
| **Async/QUIC impedance** | None — the code is already async-first | Real: actors must not `await` inline; PipeTo discipline at the I/O boundary, else thread starvation + deadlocks |
| **Framework risk** | None | Akka.NET dependency (Apache-2.0, fine), allocation overhead per message, config surface |
| **API surface for users** | Task-based, unchanged | Actor handles (`IActorRef`) as first-class; more familiar to Erlang-shaped users, less idiomatic for plain .NET |
| **Effort to first useful release** | Weeks (one focused pass) | Months (port + supervision tree design + hardening re-run) |
| **Long-term maintainability** | Good, but supervision stays bespoke code forever | Best-in-class if the actor discipline is kept (it is exactly the Erlang core's own shape) |

## Decision gates

Choose **B** over A when any of these becomes true:

1. **Observed supervision failure**: a concrete crash in A's pumps that a
   hand-rolled supervisor can't cheaply contain (not merely "Akka would be
   nicer").
2. **Actor-API demand**: C# users explicitly asking for actor handles or
   OTP-style semantics rather than tasks.
3. **Strategic statement**: the goal is to showcase the actor model as a
   flagship C# implementation of macula, accepting the re-hardening cost as
   the price of admission.

Choose **A** when none of the gates is met — which is the situation today.

Hard commitments if B is chosen:

- `macula-dotnet` is archived (read-only) before `macula-dotakka` goes
  stable — never two maintained C# SDKs.
- Akka.Remote/Cluster are explicitly out of scope.
- A port of `PLAN_RESOURCE_LEAK_HARDENING.md`'s success criteria becomes
  B's acceptance suite (stream teardown, timeout-vs-cancel classification,
  manifest bounds, fan-out bounds).

## Recommendation

**A now.** Do the leak-hardening pass first (it shrinks A's remaining gap
to supervision alone), then add the small supervision layer. Revisit B in
one release cycle with the gates above — if supervision turns out to be the
one thing A cannot do cheaply and cleanly, B is waiting, and the pre-1.0
window means the switch is still cheap.

## Phases (for option A, until the decision is revisited)

- [ ] Phase 1 — Leak hardening from `PLAN_RESOURCE_LEAK_HARDENING.md`
      (stream lifetime, OCE classification, manifest bounds, disposable
      `StreamHandle`).
- [ ] Phase 2 — Internal supervisor primitive: restart policy for
      reader/writer/subscriber pumps; per-component isolation (subscriber
      pump crash never ends the session).
- [ ] Phase 3 — Decision review against the three gates; if any gate
      fires, draft the dotakka transition plan instead.

## Files to Create/Modify (option A)

| File | Purpose | Status |
|------|---------|--------|
| `src/Macula/Connection/*` | Leak fixes from the survey plan | Not started |
| `src/Macula/Connection/Supervisor.cs` (provisional) | Small supervisor/restart primitive | Not started |
| `plans/PLAN_RESOURCE_LEAK_HARDENING.md` | Existing survey — Phase 1 source | Survey complete |

## Success Criteria

- [ ] A crashed subscriber pump on a live session restarts (or retires)
      without ending the session — the first real supervision test.
- [ ] All `PLAN_RESOURCE_LEAK_HARDENING.md` success criteria pass.
- [ ] Single C# SDK published: exactly one `Macula` NuGet package exists.
- [ ] The decision gates are re-evaluated and recorded here within one
      release cycle (decision log entry + status update).

## Open questions

- Does anyone currently consume `Macula` 0.5.0 from NuGet? (Determines how
  loudly the breaking window can be used.)
- If B is ever chosen: does `macula-dotakka` live in this repo (replacing
  `src/`) or a new repo with this one archived?
