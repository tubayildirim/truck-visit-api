# ADR-0007: `TerminalId` as tenancy key and leading index column

**Status:** Accepted — implemented across `ix_visits_terminal_status_created`,
`ix_visits_terminal_created`, and the RLS policies in the `TenantIsolationWithRowLevelSecurity`
migration.

## Context

One of the three requirements shaping the whole system is expansion to multiple terminals without
redesign. The brief asks for authorization based on terminal access with no mechanism specified, and
shared-schema tenancy with row-level scoping is what that requirement calls for — a schema or
database per terminal would be a larger operational commitment than stated requirement justifies.
For that boundary to actually hold under load, it needs to be more than a `WHERE` clause a future
query has to remember to include.

## Decision

`TerminalId` is the tenancy key, and it leads every index on `visits` and its related tables —
`ix_visits_terminal_status_created` and `ix_visits_terminal_created` both begin with it. That makes
the tenancy boundary a physical property of the access path itself: the dominant query shape (one
terminal, a status, ordered by recency) and "everything at this terminal today" are both served by
an index seek, not a scan filtered after the fact.

## Alternatives considered

- A database or schema per terminal was the strong-isolation alternative, considered under A7 in
  `ASSUMPTIONS-AND-TRADEOFFS.md` and rejected as more operational commitment than the stated
  requirement — shared infrastructure with `TerminalId` scoping — asks for.

## Consequences

Isolation is logical, not physical: a bug that omits the `TerminalId` predicate is not stopped by
schema separation alone, which is why ADR-0016 adds PostgreSQL row-level security as a second,
independent gate on the same key. In exchange, adding a terminal is a data change, not a
redesign, and `TerminalId` is already the natural shard boundary if any one terminal ever outgrows
shared infrastructure.
