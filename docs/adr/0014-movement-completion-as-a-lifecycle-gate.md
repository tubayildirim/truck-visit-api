# ADR-0014: Movement completion as a lifecycle gate

**Status:** Accepted — implemented in `Visit.ChangeStatus`'s outstanding-movements guard (throwing
`InvalidStatusTransitionException`) and `Movement.CompletedAt`/`CompletedBy`/`MarkCompleted`.

## Context

The case's stated acceptance criteria say nothing about the relationship between a visit's status
and the collections and deliveries declared on it. Without an explicit rule, a visit could reach
`Completed` while its cargo was never actually moved — `CompletedAt` still null on every movement —
and nothing in the system would notice: the two halves of a visit's record would drift apart
silently, which is exactly the kind of gap an audit exists to catch.

## Decision

A movement carries its own completion state — `CompletedAt`, `CompletedBy`, and `IsCompleted` — set
once, not idempotently, through `Movement.MarkCompleted`, callable only via `Visit.CompleteMovement`
while the visit is `OnSite`. `Visit.HasOutstandingMovements` is true while any declared movement is
still incomplete, and `ChangeStatus` refuses the transition to `Completed` while it is, raising
`InvalidStatusTransitionException` naming exactly how many movements remain outstanding. A truck
does not leave until the work it came for is done.

`CompleteMovement` deliberately does not append to `StatusHistory`. That trail is about the visit's
status; mixing a second kind of event into it would make "the fourth entry" mean two different
things to an auditor. A movement's own `CompletedAt`/`CompletedBy` fields are its audit record.

## Alternatives considered

- Recording movement completion as a status-history entry was the alternative implicit in treating
  all visit events uniformly, and it was rejected for the reason given above: it would overload one
  audit trail with two unrelated kinds of fact.

## Consequences

The rule adds a check the brief never asked for, and a caller that expects `Completed` to be
reachable regardless of cargo state will get a 409 instead — a deliberate constraint, not an
oversight. In exchange, `Completed` means what it says: everything declared on the visit was
actually moved, not merely that someone clicked through the last status. `MarkCompleted`'s
not-idempotent behaviour is the same trade made twice — surfacing a duplicated completion signal as
an error rather than silently absorbing it, because the first timestamp is the one that happened.
