# ADR-0001: Modular monolith, not microservices

**Status:** Accepted — implemented as a single deployable (`TruckVisit.Api`, `.Application`,
`.Domain`, `.Infrastructure`) with dependencies enforced inward-only.

## Context

20,000 visits/day is 0.23 writes per second on average, even at a 10× peak-hour concentration
writes stay in the low single digits per second, and the 300 req/s peak is almost entirely reads
against operator search screens. There is one bounded context in this domain — a truck visit and
its lifecycle — with no second context (vessel scheduling, yard planning) in the stated
requirements. "Scalable" in the brief is a property of the read path, and the read path already
scales horizontally behind three stateless replicas.

## Decision

The API is built as a modular monolith: one deployable unit with internal module boundaries
(`Api` → `Application` → `Domain`, with `Infrastructure` as an adapter) enforced in code rather
than by network separation. Section 9 of `ARCHITECTURE.md` states plainly what would have to
change for that answer to stop holding.

## Alternatives considered

- Separate services for visits, audit and search — rejected because the numbers do not support it:
  splitting would add network hops, distributed-transaction concerns, and three independent
  deployment pipelines, while removing not one line of business logic.

## Consequences

Modules cannot be scaled or deployed independently — a genuine limit if one bounded context ever
needs radically different scaling from another. That cost is accepted because boundaries are kept
honest in code: `TruckVisit.Domain` and `TruckVisit.Application` carry zero NuGet package
references, and the application talks only to ports. If a second bounded context appears,
extraction is a mechanical refactor, not a rewrite.
