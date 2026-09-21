# ADR-0011: `Idempotency-Key` on create

**Status:** Accepted — implemented via `idempotency_records` (composite key `(Key, UserId)`),
`IdempotencyStore.cs`, and the `Idempotency-Key` header on `POST /api/visits`.

## Context

This was not requested by the brief. Gate hardware and mobile clients retry aggressively over flaky
links, and `POST /api/visits` has no natural way to detect a retry on its own: without an
idempotency mechanism, one lorry arriving once can become three visit records — an error that no
later correction can fully undo in an audit trail that is supposed to be a faithful record of what
happened.

## Decision

Clients may send an `Idempotency-Key` header on create. The key is scoped by `(Key, UserId)` as a
composite primary key on `idempotency_records`, so the uniqueness guarantee comes from the database
itself rather than from a read-then-write race in application code. A replay returns 200, not 201 —
a client retrying over a flaky link needs to distinguish "I created this" from "this already
existed"; returning 201 twice would hide a real failure mode instead of surfacing it.

## Alternatives considered

- No explicit alternative mechanism is discussed in the source documents beyond the composite-key
  design itself; the decision was to add the guarantee rather than rely on the caller not to retry,
  which is not an alternative worth naming.

## Consequences

Every create request carries the overhead of an idempotency lookup and, on first use, an insert into
`idempotency_records`. The table needs pruning — records older than roughly seven days are not
plausible retries and the plan (not yet built) is a scheduled job against an index that already
exists for it. In exchange, a duplicated arrival signal cannot silently triple a gate's audit record.
