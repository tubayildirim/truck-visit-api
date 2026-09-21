# Truck Visit Management API — Architecture

## 1. Context

Gate management at a large terminal needs near real-time visibility of trucks arriving to collect
and deliver cargo. This service records a **visit** — one truck's arrival at one terminal — and the
lifecycle it passes through, keeping every status change for seven years so that regulatory audits
can reconstruct what happened and when.

Three requirements shape every decision below:

| Requirement | Consequence |
|---|---|
| Full audit history of status changes, retained 7 years | Immutability is a structural property, not a convention |
| Up to 20 000 visits/day, peaks of 300 req/s | The system is overwhelmingly **read**-dominated |
| Expansion to multiple terminals without redesign | `TerminalId` is a tenancy boundary from day one |

---

## 2. What the numbers actually mean

The stated figures are small in write terms and large in storage terms. Reading them the other way
round produces the wrong architecture, so they are worked through here first.

**Write load.** 20 000 visits/day is **0.23 writes per second** on average. Even at a 10× peak-hour
concentration, writes stay in the low single digits per second. Write throughput is a non-problem.

**Read load.** The 300 req/s peak is therefore almost entirely reads: operator screens polling the
search endpoint. The system is read-dominated by roughly three orders of magnitude.

**Storage over the retention period.**

| Table | Rows at 7 years | Assumption | Approx. size incl. indexes |
|---|---|---|---|
| `visits` | ~51 M | 20 000/day × 365 × 7 | ~30 GB |
| `visit_status_history` | ~204 M | ~4 entries per visit | ~35 GB |
| `visit_movements` | ~77 M | ~1.5 movements per visit | ~18 GB |
| **Total** | | | **~85 GB** |

That is comfortably inside a single managed PostgreSQL instance. Storage volume is a
**query-planning** problem, not a capacity problem: 51 M rows demands correct indexes, not
sharding.

**Availability.** 99.95% permits ~4.4 hours of downtime per year — about 22 minutes a month. That
budget is consumed by ordinary deploys and node replacements long before any incident, which is why
the deployment runs three replicas with `maxUnavailable: 0` rather than one.

**Conclusion:** a correctly indexed, well-bounded single service. Not a distributed system.

---

## 3. High-level architecture

```mermaid
flowchart LR
    Gate["Gate devices<br/>& operator UI"] -->|HTTPS + JWT| Ingress[Ingress / ALB<br/>TLS termination]
    Ingress --> API["Truck Visit API<br/>3+ replicas"]
    API -->|EF Core| DB[("PostgreSQL / RDS<br/>Multi-AZ")]
    API -.->|OTLP| Obs["Logs · Metrics · Traces"]
    IdP["Identity provider<br/>OAuth2 / OIDC"] -.->|signing keys| API
```

The API is a single deployable unit with clear internal module boundaries — a **modular monolith**.
Section 9 explains why, and what would have to change for that to stop being the right answer.

### Internal structure

```mermaid
flowchart TD
    subgraph Api["TruckVisit.Api — HTTP edge"]
        E[Endpoints] --- MW["Middleware<br/>correlation · security headers · problem details"]
        SEC["CurrentUser<br/>claims → authorization facts"]
    end
    subgraph App["TruckVisit.Application — use cases"]
        H["RegisterVisit · GetVisitById<br/>SearchVisits · ChangeVisitStatus"]
        P["Ports: IVisitRepository<br/>IIdempotencyStore · ICurrentUser"]
    end
    subgraph Dom["TruckVisit.Domain — business rules"]
        AG["Visit aggregate<br/>StatusChange · Movement"]
        VO["Value objects<br/>UnitNumber · LicensePlate · TerminalCode"]
        SM[VisitStatusTransitions]
    end
    subgraph Inf["TruckVisit.Infrastructure — adapters"]
        R[VisitRepository] --- CTX[TruckVisitDbContext]
        CFG["EF configurations<br/>converters · indexes"]
    end

    Api --> App --> Dom
    Inf --> App
    Inf --> Dom
```

Dependencies point inward only. `TruckVisit.Domain` and `TruckVisit.Application` have **zero NuGet
package references** — if either ever grows an `<ItemGroup>`, a business rule has leaked into a
framework.

---

## 4. Domain model

```mermaid
stateDiagram-v2
    [*] --> PreRegistered: Register
    PreRegistered --> AtGate: truck arrives
    AtGate --> OnSite: admitted
    OnSite --> Completed: departs
    Completed --> [*]
```

Forward-only, no skipping, `Completed` terminal. The case does not specify this — see
[assumptions](ASSUMPTIONS-AND-TRADEOFFS.md#a1-lifecycle-shape).

**`Visit`** is the aggregate root and the only way to change anything about a visit. Every
collection is exposed through `AsReadOnly()`, every property setter is private, and there is
exactly one code path that appends to the audit trail — one that cannot run without first
validating the transition.

**`StatusChange`** has no public setter and no mutating member. Its `Sequence` field gives the
trail a total order that cannot tie, because timestamps can collide within a millisecond and clocks
can be stepped backwards.

**Value objects** (`UnitNumber`, `LicensePlate`, `TerminalCode`, `LocationCode`) normalise at
construction: whitespace stripped, upper-cased with the **invariant** culture. This is the
acceptance criterion about capitalisation, implemented once where no call site can forget it. It is
also a correctness guard — see §7.

---

## 5. API design

| Method | Route | Success | Notes |
|---|---|---|---|
| `POST` | `/api/visits` | 201 / 200 | `Idempotency-Key` header supported; 200 on replay |
| `GET` | `/api/visits/{id}` | 200 | Full detail with complete audit trail |
| `GET` | `/api/visits` | 200 | 8 filters + pagination with metadata |
| `PATCH` | `/api/visits/{id}/status` | 200 | Not in the task list; added — see assumptions |
| `GET` | `/health/live`, `/health/ready` | 200 | Anonymous |

**Errors** are RFC 9457 `application/problem+json`, produced in exactly one place
(`GlobalExceptionHandler`). Endpoints contain no `try`/`catch` and no validation, so the same rule
violation cannot return 400 from one route and 500 from another.

| Condition | Status |
|---|---|
| Value fails a domain rule | 400 + `field` |
| Caller holds no claim for the terminal (write) | 403 |
| Visit absent, **or** at an invisible terminal | 404 |
| Illegal transition, or concurrent modification | 409 |
| Anything unrecognised | 500, no detail |

**Replay returns 200, not 201.** A client retrying over a flaky link needs to distinguish "I
created this" from "this already existed"; returning 201 twice hides a real failure mode.

**Search returns summaries, not full aggregates.** A list view needs current state, not every
transition that led to it. Returning history would multiply rows read per page by trail length —
on the busiest endpoint in the system.

---

## 6. Data storage

**PostgreSQL via EF Core.** Chosen because the audit trail is inherently relational, the search
endpoint filters across eight columns and two tables, status transitions need ACID guarantees, and
~85 GB sits well inside one managed instance. AWS RDS runs it natively.

### Schema

| Table | Purpose |
|---|---|
| `visits` | Aggregate root; `Truck` and `Driver` inlined as owned value objects |
| `visit_movements` | Owned collection; collections and deliveries |
| `visit_status_history` | Owned collection; **append-only** |
| `idempotency_records` | Composite key `(Key, UserId)` |

**Enums and value objects are stored as text.** These rows outlive the code that wrote them by
years. `AtGate` is still legible to an auditor in 2033; a bare `2` is legible only next to a copy of
this enum.

### Immutability — enforced three times

1. **Type level.** `StatusChange` exposes no setter and no mutating method. A unit test asserts
   this by reflection, so adding one fails the build.
2. **Persistence level.** `SaveChanges` inspects the change tracker and throws if any audit entry
   is `Modified` or `Deleted` — catching attached graphs and bulk operations that bypass the model.
3. **Database level.** The migration installs a `BEFORE UPDATE OR DELETE` trigger on
   `visit_status_history` that raises an exception, so a direct SQL statement is refused too. A
   trigger rather than a `REVOKE`, because `REVOKE` does not constrain a superuser and the owning
   role usually is one — the guarantee has to hold for every connection, not just the polite ones.
   `REVOKE` is still applied to the application role in production as a second layer.

Defence in depth is warranted here because this table is what a regulatory audit actually inspects.

### Indexes

| Index | Serves |
|---|---|
| `ix_visits_terminal_status_created` | The dominant query: one terminal, a status, ordered by recency |
| `ix_visits_terminal_created` | "Everything at this terminal today" |
| `ix_visits_created_by` | `createdBy` filter |
| `visit_movements(From)`, `(To)`, `(UnitNumber)` | `movementFrom` / `movementTo` filters |
| `visit_status_history(VisitId, Sequence)` unique | Ordering, and refusing a duplicated append |

Every visit index **leads with `TerminalId`**. That is deliberate: it makes the tenancy boundary a
physical property of the access path, not only a `WHERE` clause someone must remember.

### Concurrency

PostgreSQL's `xmin` system column as an optimistic concurrency token — no extra column, no extra
storage, no application code to maintain. Two gate terminals advancing the same visit simultaneously
produce a 409 for the second, not a lost update.

There are in fact **two** mechanisms, and which one fires is an implementation detail the caller
should never see. Both gates compute the next audit sequence number from the history they loaded,
so both attempt to insert the same one; EF sends that `INSERT` before the `UPDATE` that would have
tripped the concurrency token, and the unique index on `(VisitId, Sequence)` rejects it first. The
repository therefore translates *both* failures into the same domain-level conflict.

That was not designed in advance — it was found by the integration test that runs the race against
a real database. Written expecting only the concurrency exception, it failed, and without the fix
an ordinary race at a busy gate would have reached the client as a 500 instead of a 409. It is the
clearest argument in this repository for testing persistence against the real engine rather than an
in-memory substitute: no amount of unit testing against a fake repository could have produced it.

### Retention and growth

Seven years of history is a lifecycle problem, and the plan is documented rather than built:

- **Partition `visits` and `visit_status_history` by month on `CreatedTime`.** Operator queries
  touch recent data, so the planner prunes to one or two partitions regardless of total volume.
- **Tier storage.** Partitions older than ~18 months move to cheaper storage or export to S3 in
  Parquet for the analytics path; audits of old data are rare and may be slower.
- **Prune `idempotency_records`** after ~7 days. Retries are not plausible beyond that.
- **Driver data is personal data** under a 7-year retention rule, which is squarely in GDPR scope.
  The lawful basis is the terminal's own security and customs obligations; an erasure request is
  handled by pseudonymising the driver columns while leaving the audit chain structurally intact.

---

## 7. Security model

**Authentication.** OAuth2 / JWT Bearer. Settings bind from `Authentication:Schemes:Bearer`, which
is also where `dotnet user-jwts` writes — local development therefore uses **real, signed tokens**
and the pipeline contains no developer-only bypass, which is the shortcut that quietly ships.

**Authorization.** A fallback policy requires an authenticated user on every endpoint, so a route
added later is protected by default rather than by the author remembering. Terminal access comes
from `terminal` claims; `scope=terminals.all` grants cross-terminal read for auditors.

Claims pass through the same `TerminalCode` value object as stored data. Without that, a token
issued as `"dover "` would silently fail to match rows saved as `"DOVER"`, and an operator would be
told there are no trucks at their own terminal.

**Row scoping is resolved in the application layer**, before the repository, which therefore never
reasons about authorization. An empty terminal scope is handled explicitly and returns an empty
page — never all rows.

**404 vs 403.** A visit at an invisible terminal is reported as missing. Returning 403 would make
the endpoint an oracle: anyone with a token could enumerate identifiers and learn which visits exist
at terminals they have no right to know about. Writes return 403, where hiding existence buys
nothing.

**`createdBy` is never taken from the request body.** It comes from the authenticated principal's
`sub` claim. An audit trail attributed by the caller is not an audit trail.

**Transport and headers.** HSTS and HTTPS redirection outside Development; `X-Content-Type-Options`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a `default-src 'none'` CSP, a restrictive
`Permissions-Policy`, and the `Server` header removed.

**Secrets.** Nothing is committed. Connection strings arrive as `ConnectionStrings__TruckVisit` from
the environment, wired to AWS Secrets Manager in a cluster. Start-up fails fast if absent.

**Error bodies leak nothing.** Unrecognised exceptions return a fixed message and a correlation id.
An exception message can name a table, a column, a host, or a connection string.

### Supply chain

`NuGetAudit` is enabled at `low` severity in `all` mode, and warnings are errors — so a known
vulnerability in **any** dependency, direct or transitive, fails the build. Three transitive
advisories were found during development and pinned forward rather than suppressed:

| Package | Was | Now | Advisory |
|---|---|---|---|
| `Microsoft.OpenApi` | 2.0.0 | 2.12.2 | GHSA-v5pm-xwqc-g5wc |
| `System.Security.Cryptography.Xml` | 9.0.0 | 10.0.12 | GHSA-23rf-6693-g89p + 7 others |
| `SSH.NET` | 2024.2.0 | 2026.0.0 | GHSA-mggc-4xg6-vcxf + 1 |

### The culture trap

`ToUpperInvariant`, not `ToUpper`. Under a Turkish locale the default upper-casing maps `i` to `İ`
(U+0130), not `I`. A server running under `tr-TR` would store the same plate differently from an
identical server under `en-GB`: two unmatchable records for one truck, and an audit trail that
cannot be joined back together.

`InvariantGlobalization` is deliberately **not** enabled — that would mask the problem rather than
solve it, leaving culture-sensitive calls in place to start corrupting data the day the switch was
flipped. Instead, CA1304/CA1305/CA1310 are build **errors**, and a unit test runs under `tr-TR` to
prove the behaviour.

---

## 8. Observability

| Requirement | Implementation |
|---|---|
| Structured logging | JSON to stdout with scopes; no string interpolation — `[LoggerMessage]` source generation keeps templates queryable |
| Correlation IDs | `X-Correlation-ID` honoured from the client, bounded and stripped of control characters, echoed on every response including errors |
| API throughput & latency | ASP.NET Core's built-in `http.server.request.duration` histogram |
| Business metrics | `truckvisit.visits.registered`, `.status_changed`, `.transition_rejected` |
| Audit logging | The `visit_status_history` table is the audit log — queryable, retained, immutable |
| Health checks | `/health/live` (no dependencies) and `/health/ready` (database reachable) |
| Error tracking | One handler, one mapping; known failures at information level, unknown at error with full context |

**Why liveness and readiness are separate.** A pod that cannot reach the database should be pulled
out of the load balancer, not restarted — restarting will not bring the database back, and a restart
loop across every replica during an RDS failover turns a brief outage into a total one.

**Why `transition_rejected` is worth a counter.** A rise means a gate device is out of step with the
server — a fault no HTTP metric reveals, because every one of those requests is a perfectly healthy
409.

---

## 9. Scalability

**Today.** Three stateless replicas behind an ingress; horizontal scaling to 12 on CPU. 300 req/s
across three replicas is 100 req/s each against indexed queries returning ≤200 rows — well inside a
single instance's capacity. PostgreSQL sees a negligible write rate and an indexed read rate it
handles on modest hardware.

**Next bottlenecks, in the order they will arrive:**

1. **Read volume on the primary.** Add RDS read replicas and route search to them. Search is already
   `AsNoTracking()` and projection-only, so this is a connection-string change, not a redesign.
2. **Deep pagination.** Offset paging degrades at high page numbers. The fix is keyset pagination on
   `(CreatedTime, Id)` — both already indexed and already the sort order.
3. **Table size.** Monthly partitioning on `CreatedTime`, as in §6.
4. **Full-text / fuzzy search.** If operators need "plate starts with 34AB" or cross-field relevance
   ranking, relational `LIKE` stops being appropriate. At that point project a read model into
   **Elasticsearch** via the outbox pattern, keeping PostgreSQL as the system of record. This is
   deliberately *not* built now: it doubles the operational surface to solve a problem the stated
   requirements do not yet have.

**Why a modular monolith and not microservices.** There is one bounded context here. The write rate
is 0.23/s. Splitting it would add network hops, distributed transactions, and independent
deployment pipelines while removing not one line of business logic. Module boundaries are enforced
in code — the domain has no framework dependencies, the application talks only to ports — so if a
second bounded context appears (vessel scheduling, yard planning), extraction is a mechanical
refactor rather than a rewrite.

**Multi-terminal expansion** needs no redesign: `TerminalId` already scopes authorization,
filtering, and every index. Adding terminals is data. If one terminal ever outgrows shared
infrastructure, the same key is the natural shard boundary.

---

## 10. Deployment

**Container.** Multi-stage build; project files restore before sources so an edit does not
re-download the package graph. Domain tests run **inside** the image build — an image that cannot
pass its own tests is never produced.

Runtime is a **chiselled** (distroless) base: no shell, no package manager, non-root by default,
read-only root filesystem, all capabilities dropped. The `-extra` variant is required rather than
incidental, because ICU is needed (see §7).

**Kubernetes** (`deploy/k8s/truckvisit-api.yaml`): 3 replicas, `maxUnavailable: 0`, zone spread,
separate liveness/readiness/startup probes, a PodDisruptionBudget of 2, and a `preStop` pause so the
load balancer stops sending work before the process exits. No CPU limit — CFS throttling adds tail
latency exactly during the peaks this service is sized for — while the memory limit still bounds a
leak.

**On AWS**, concretely: RDS for PostgreSQL Multi-AZ (failover is why the EF provider enables retry
on transient faults), Secrets Manager for the connection string via External Secrets, EKS for the
workload, ALB for TLS termination, CloudWatch or an OTLP collector for logs and metrics, and ECR for
images. Migrations run as a pipeline step, never at application start-up — an app that migrates its
own schema races itself the moment it runs more than one replica, which this one always does.

---

## 11. Decision record

| # | Decision | Rationale |
|---|---|---|
| ADR-001 | Modular monolith, not microservices | One bounded context; 0.23 writes/s; boundaries enforced in code |
| ADR-002 | Domain and Application have zero packages | A package reference there means a rule leaked into a framework |
| ADR-003 | Append-only audit enforced at three levels | This table is what an audit inspects |
| ADR-004 | Enums and codes stored as text | Rows outlive the code by years |
| ADR-005 | Normalise in value-object constructors, invariant culture | Call sites cannot forget; `tr-TR` cannot corrupt |
| ADR-006 | Denormalise `CurrentStatus`, `LastStatusChangedAt` | Avoids aggregation over the largest table on the hot path |
| ADR-007 | `TerminalId` as tenancy key and leading index column | Makes the boundary physical, not conventional |
| ADR-008 | Search returns projections, not aggregates | List views do not need history |
| ADR-009 | No mediator, mapper or validation library | At four use cases each adds indirection without removing logic |
| ADR-010 | Optimistic concurrency via `xmin` | Free in PostgreSQL; correct for a low-contention write path |
| ADR-011 | `Idempotency-Key` on create | Gate hardware retries; one arrival must not become three records |
| ADR-012 | Security advisories fail the build | A gate that warns is a gate that is ignored |

Full reasoning, alternatives considered and limitations accepted:
[ASSUMPTIONS-AND-TRADEOFFS.md](ASSUMPTIONS-AND-TRADEOFFS.md).
