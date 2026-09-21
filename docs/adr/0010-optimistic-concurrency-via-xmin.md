# ADR-0010: Optimistic concurrency via `xmin`

**Status:** Accepted — configured as the concurrency token in `VisitConfiguration.cs` and translated
to a domain conflict in `VisitRepository`.

## Context

Two gate terminals can advance the same visit at the same moment — a genuine race, not a
theoretical one, at a busy gate. The write path overall is low-contention (0.23 writes/s on
average), so the case for a heavyweight locking scheme is weak, but a lost update on the status that
does happen to collide is worse than making the losing caller retry.

## Decision

PostgreSQL's `xmin` system column is used as the optimistic concurrency token — no extra column, no
extra storage, no application code needed to maintain it. A second gate terminal that tries to
advance a visit already changed by the first receives a 409, not a silently overwritten status.
There are in fact two mechanisms that can produce that 409 — the `xmin` check and the unique index
on `(VisitId, Sequence)` that both concurrent writers race to insert into — and the repository
translates both failures into the same domain-level conflict so the caller never has to know which
one fired.

## Alternatives considered

- An explicit `rowversion`-style column was the implicit alternative on other databases; `xmin` was
  preferred specifically because it is free on PostgreSQL — no column, no write, no migration to
  maintain it.

## Consequences

Clients must handle a 409 and retry — conflicts are rare, and a lost status update is worse than
asking a caller to retry. The two-mechanism behaviour was not designed in advance: an integration
test that runs the race against a real database found that EF sends the `INSERT` before the `UPDATE`
that would have tripped the concurrency token, so the sequence's unique index rejects the race
first. Written expecting only the concurrency exception, that test failed, and without the fix an
ordinary race at a busy gate would have reached the client as a 500 instead of a 409 — the clearest
argument in this codebase for testing persistence against the real engine rather than an in-memory
substitute.
