# ADR-0006: Denormalise `CurrentStatus` and `LastStatusChangedAt`

**Status:** Accepted — implemented as columns on `visits` in
`TruckVisit.Infrastructure/Persistence/Configurations/VisitConfiguration.cs`.

## Context

The search endpoint — the busiest one in the system, given a read-dominated load of up to 300 req/s
against 0.23 writes/s — needs each visit's current status and when it last changed, for every row
on every page. `visit_status_history` is the largest table in the schema (~204 M rows at seven
years, versus ~51 M for `visits`), and deriving current status from it on every search request would
mean aggregating the largest table in the system on the single hottest path.

## Decision

`CurrentStatus` and `LastStatusChangedAt` are stored directly on the `visits` row, duplicating
information that is also derivable from `visit_status_history`. Both are written inside the same
method, in the same transaction, as the audit append that changes them — there is exactly one code
path that can update either.

## Alternatives considered

- Computing current status by querying the latest `visit_status_history` row per visit was the
  normalised alternative, and it is what denormalisation was chosen to avoid on the search path.

## Consequences

Two representations of the same fact — `visits.CurrentStatus` and the last row in
`visit_status_history` — must stay consistent, and a future code path that appends to the history
without going through the one method that also updates `visits` would silently desynchronise them.
That risk is accepted because both writes happen inside a single transactional method today; in
exchange, the dominant search query reads an indexed column on `visits` instead of aggregating over
a table four times its size.
