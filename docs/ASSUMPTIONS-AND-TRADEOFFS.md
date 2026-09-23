# Assumptions, Trade-offs and Known Limitations

This document covers what the brief left open, what was decided instead, what was considered and
rejected, and what is knowingly missing.

---

## 1. Assumptions made from incomplete or ambiguous requirements

### A1 — Lifecycle shape

**Ambiguity.** The brief names four statuses — `Pre-Registered`, `At Gate`, `On Site`, `Completed` —
but never says which transitions are legal.

**Assumed.** Strictly forward, one step at a time. No skipping, no reversing, `Completed` terminal.
Repeating the current status is refused rather than ignored.

**Why.** The order of the names describes a physical sequence: a truck cannot be on site before it
has been processed at the gate. Refusing a repeat rather than ignoring it matters more than it
looks — a silent no-op would let a duplicated gate signal appear to succeed, while writing the entry
anyway would pad an audit trail with events that never happened.

**If wrong.** The rules live in one lookup table (`VisitStatusTransitions`). Adding a `Rejected`
status, or permitting `AtGate → Completed` for a turned-away truck, is a one-line data change.

### A2 — A status-update endpoint that the task list omits

**Ambiguity.** The acceptance criteria state that "a visit's current status may be updated, however
all status transitions must be retained". The Tasks section lists only create, read and search.

**Assumed.** `PATCH /api/visits/{id}/status` was added.

**Why.** Without a way to change status, the audit requirement — the point of the feature — could
never be exercised. A submission that skipped it would satisfy the task list while failing the
acceptance criteria. `PATCH` rather than `PUT` because this modifies one facet, not the resource.

### A3 — `movementFrom` and `movementTo`

**Ambiguity.** These two search parameters are named but never defined. They could be a date range
over movements, or origin/destination locations.

**Assumed.** Location codes: where a unit comes from and where it is going.

**Why.** The same parameter list already establishes a naming convention for date ranges —
`createdTimeFrom` and `createdTimeTo`. The absence of "time" in `movementFrom` is the distinguishing
signal. The alternative reading also has no field to filter on: movements in the supplied domain
sketch carry no timestamp of their own.

**If wrong.** `Movement` gains a timestamp and the filter changes to a range predicate; the
repository's filter composition is unaffected.

### A4 — Driver fields

**Ambiguity.** "Driver information must be captured", with no fields listed.

**Assumed.** `FullName` (required), `DocumentId` (required — the ID or licence number checked at the
barrier), `CompanyName` (required), `PhoneNumber` (optional).

**Why.** A name to call out, a document to check against the ID at the barrier, a number to reach the
cab — and the haulier, because in a ro-ro terminal the accountable party for a unit is the company,
not the individual driver: who gets billed, who a damage claim goes to, who is called when a booked
truck does not arrive. `CompanyName` is required for the same reason `FullName` and `DocumentId` are.
`Nationality`, date of birth and licence expiry were considered and rejected: customs systems already
hold them, the gate admission decision does not use them, and collecting a personal field because it
might one day be useful is how a retention policy becomes unenforceable. This is the only personal
data in the model and it sits under a seven-year retention rule — see §4 for the GDPR consequence.

### A5 — A visit must declare at least one movement

**Ambiguity.** The brief says a visit includes "a list of collections and deliveries" without saying
whether it may be empty.

**Assumed.** At least one is required; at most 50.

**Why.** A visit exists in order to collect or deliver something; admitting a truck with no recorded
purpose is a hole in the gate record. The upper bound is a denial-of-service control, not tidiness —
an unbounded list on an unauthenticated-adjacent create endpoint is an attack surface.

**Risk acknowledged.** If pre-registration legitimately happens before the load is known, this is
too strict. Relaxing it means deleting one guard clause and one test.

### A6 — `createdBy` is the authenticated principal

**Ambiguity.** `CreatedBy` appears in the domain sketch with no stated source.

**Assumed.** Taken from the token's `sub` claim, never from the request body. The same applies to
`ChangedBy` on every audit entry.

**Why.** An audit trail attributed by the caller is not an audit trail. Regulatory audits occur
regularly, per the brief's own business requirements.

### A7 — Terminal access model

**Ambiguity.** "Authorization based on terminal access" — mechanism unspecified.

**Assumed.** One `terminal` claim per terminal the caller may act on; `scope=terminals.all` for
cross-terminal read (compliance, central operations). Shared database with row-level scoping rather
than a database per terminal.

**Why.** Claims are the natural carrier in a JWT model, and shared-schema tenancy is what "support
future expansion to multiple terminals without significant redesign" asks for. Strong isolation
would be a schema or database per terminal, which is a larger operational commitment than the stated
requirement justifies.

### A8 — Timestamps come from the server

**Assumed.** `TimeProvider` on the server supplies every timestamp; clients cannot set them. An
audit entry may not pre-date the entry before it.

**Why.** Client clocks are wrong, and a gate device with a wrong clock would otherwise write a
history that reads out of order. `TimeProvider` rather than `DateTimeOffset.UtcNow` so time is
injectable and the rules are testable.

### A9 — Search filters are exact matches

**Assumed.** `createdBy` matches exactly; location and terminal filters match after normalisation.
No partial or fuzzy matching.

**Why.** Nothing in the brief asks for it, and it is the boundary at which a relational index stops
being the right structure. See §3 on Elasticsearch.

---

## 2. Decisions and their rationale

### D1 — Modular monolith, not microservices

**Considered:** separate services for visits, audit and search.

**Rejected because** the numbers do not support it. 20 000 visits/day is 0.23 writes per second.
There is one bounded context. Splitting would add network hops, distributed transaction concerns and
three deployment pipelines, while removing no business logic. "Scalable" in the brief is a property
of the read path, and the read path scales horizontally here already.

**Kept honest by** enforcing the boundaries in code: the domain and application layers have zero
NuGet references, and the application talks only to ports. If a second bounded context appears —
vessel scheduling, yard planning — extraction is a mechanical refactor.

### D2 — Rich domain model, not an anemic one

**Considered:** DTOs with a service layer holding the rules.

**Rejected because** the central requirement is an audit guarantee, and a guarantee that depends on
every service remembering to write a history row is not a guarantee. Putting the rules inside the
aggregate makes there exactly one code path that can append to the trail, and it cannot run without
validating the transition first.

### D3 — PostgreSQL, not a document store

**Considered:** MongoDB, storing each visit as a document with its history embedded.

**Rejected because** the search endpoint filters across eight parameters and two collections,
pagination needs accurate totals, and status transitions need ACID guarantees. A document store
would make the aggregate read trivially cheap and the search endpoint — the hot path — expensive.

**Considered:** SQL Server, which is in the advertised stack.

**Chosen PostgreSQL because** RDS runs both, the `xmin` system column gives optimistic concurrency
at zero cost, and the development machine is Apple Silicon where SQL Server runs only under
emulation. The persistence layer is behind `IVisitRepository`; if DFDS's estate is SQL Server, the
change is the provider package, the `xmin` token swapped for `rowversion`, and the migration —
roughly a day, with no change above the infrastructure layer.

### D4 — Not event sourcing

**Considered seriously**, because the requirement is literally an immutable log of state changes,
and event sourcing would make the audit trail the primary model rather than a duplicate of it.

**Rejected because** it would also make every other part harder for no stated benefit: the search
endpoint needs current state across 51 million visits, which means building and maintaining
projections; there is no requirement to replay, to compute alternative histories, or to derive new
views from past events. The append-only table delivers the required guarantee with a fraction of the
machinery. Revisit if the terminal later wants temporal queries — "what did the yard look like at
14:00 last Tuesday".

### D5 — Not SQL Server temporal tables / PostgreSQL triggers for history

**Considered:** let the database version rows automatically.

**Rejected because** the audit trail here is a business concept, not a technical one. It carries
`Reason`, `ChangedBy` and a gapless `Sequence`, and it must refuse invalid transitions before
anything is written. A system-versioned table records *that* a row changed, not *why* it was allowed
to. Automatic versioning would also tie the model to one vendor.

### D6 — No mediator, mapper or validation library

**Considered:** MediatR, AutoMapper, FluentValidation — the conventional trio.

**Rejected because** at four use cases each adds a dependency and a layer of indirection without
removing a line of logic. Hand-written mapping turns a renamed property into a compile error rather
than a field that quietly becomes null in production. MediatR also became commercially licensed,
which is a supply-chain decision a Lead should make deliberately rather than by habit. The
Application layer's package count is zero, and that is enforced by its `.csproj`.

### D7 — Idempotency on create

**Not requested.** Added because gate hardware and mobile clients retry aggressively over flaky
links, and without it one lorry arriving once can become three visit records — an error no later
correction can fully undo in an audit trail. The key is scoped by `(Key, UserId)` with a composite
primary key, so the uniqueness guarantee comes from the database rather than from a read-then-write
race in application code.

### D8 — Security advisories fail the build

`NuGetAudit` at `low` severity in `all` mode, with warnings as errors. Three transitive advisories
surfaced during development and were pinned forward rather than suppressed. A quality gate that only
warns is a quality gate that is ignored.

### D9 — Rate limiting

**Not requested.** Added because a 99.95% target and a single misbehaving client are in direct
tension: a polling loop with a bad interval can saturate a replica on its own. Partitioned by
principal so one client's burst cannot consume another's budget, and health probes are exempt — a
throttled readiness check would pull healthy replicas out of rotation exactly when the service is
busiest.

---

## 3. Trade-offs accepted

**Modular monolith over microservices.** The write rate is 0.23/s and there's one bounded context.
Splitting would add network hops and deployment complexity without removing a line of business logic.
Module boundaries are enforced in code, so if a second context appears, extraction is a refactor.

**Offset pagination over keyset.** Operators work from recent arrivals, so deep pages are rare in
practice. Query 7 in the capacity test shows this costs 77 ms at page 2000 vs 1 ms at page 1 —
worth fixing, not urgent. Sort key is already indexed for the switch when the time comes.

**Denormalised `CurrentStatus` / `LastStatusChangedAt`.** These are written twice: on `visits` and
derived from `visit_status_history`. The inconsistency risk is contained — both writes happen inside
one method in one transaction. The alternative is aggregating the largest table on the search hot path.

| Trade-off | Accepted | Cost |
|---|---|---|
| **Relational search vs Elasticsearch** | Indexed SQL | No fuzzy or cross-field relevance. Migration path documented. |
| **Optimistic concurrency** | 409 on conflict, clients retry | Conflicts are rare; a lost status update is worse than a retry |
| **Shared tenancy** | Shared schema, row scoping | Not physical isolation. `TerminalId` is the natural shard key if isolation requirements change. |
| **Build strictness** | Warnings as errors, full analyzers | Occasional build breaks on style. Every alert during development was a real signal, including three CVEs. |

---

## 4. Known limitations

These are absent deliberately, with the reasoning recorded rather than discovered later.

**Not implemented in code, documented as design:**

1. **Table partitioning.** Monthly partitions on `CreatedTime` for `visits` and
   `visit_status_history`. Necessary somewhere around year two; premature at zero rows.
2. **Archival tiering.** Moving partitions older than ~18 months to cheaper storage or S3/Parquet.
3. **Idempotency pruning.** A scheduled job to delete records older than ~7 days. The index
   supporting it exists.
4. **Read replicas.** The search path is `AsNoTracking()` and projection-only, so routing it to a
   replica is a connection-string change.
5. **GDPR erasure.** Driver data is personal data under a seven-year retention rule. The intended
   approach is pseudonymising the driver columns on request while leaving the audit chain
   structurally intact, since the terminal's security and customs obligations are the lawful basis
   for retaining the visit itself. Not implemented — it needs a legal decision, not a technical one.

**Genuinely missing:**

6. **No API versioning.** There is one consumer today. The moment there are two, `/api/v1/` and a
   deprecation policy are needed before the first breaking change, not after.
7. **No OTLP exporter wired.** Metrics and traces are emitted through the standard .NET APIs, so
   collection is a configuration step, but no collector endpoint is configured here.
8. **Movement count is a correlated subquery** in the search projection. Fine at page size 25;
   denormalise it onto `visits` if it ever shows up in a query plan.

> `.github/workflows/ci.yml` and the measured query plans in
> [ARCHITECTURE §9](ARCHITECTURE.md#9-scalability) close what used to be listed here as missing CI
> and an unmeasured capacity claim; the rest of this section still needs a pass to catch up with
> that (tracked separately).

---

## 5. If this were going to production

In priority order, and deliberately not attempted inside a case study:

1. Load test against a seeded 50 M-row database; validate or fix the index strategy.
2. CI pipeline: build, analyzers, full test suite, vulnerability audit, image scan.
3. Partitioning and the archival job, before the data makes them expensive to add.
4. A decision from Legal on driver-data erasure, then implement it.
5. API versioning before the second consumer.
6. Contract tests against the real gate devices — the one class of defect no amount of unit testing
   in this repository can find.
