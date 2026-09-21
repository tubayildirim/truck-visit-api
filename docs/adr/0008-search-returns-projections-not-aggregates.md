# ADR-0008: Search returns projections, not aggregates

**Status:** Accepted — implemented in `IVisitRepository`'s search method and the `AsNoTracking()`
projection path in `VisitRepository`.

## Context

`GET /api/visits` is the busiest endpoint in the system: 300 req/s peak against 0.23 writes/s means
the service is read-dominated by roughly three orders of magnitude, and a list view is read far more
often than any single visit's full detail. A list view needs current state to display a row, not the
complete transition history that produced it — returning full aggregates would multiply rows read
per page by trail length, on exactly the path where request volume is highest.

## Decision

The repository returns two different shapes behind one port: lightweight projections for search
results, and full aggregates (`Visit` with its owned `StatusChange` and `Movement` collections) for
single-visit reads and writes. Search is `AsNoTracking()` and selects only the columns a list row
displays.

## Alternatives considered

- Returning the full `Visit` aggregate — history included — from search was the naive alternative,
  rejected because materialising full aggregates per list row reads an order of magnitude more data
  than the list actually displays.

## Consequences

`IVisitRepository` exposes two return shapes instead of one, which is more surface area than a
single uniform aggregate-in, aggregate-out repository would have. That cost buys a search path that
scales with page size instead of with audit-trail depth, and it is also what makes the read-replica
migration path (§9's first "next bottleneck") a connection-string change rather than a redesign:
search is already read-only and projection-only.
