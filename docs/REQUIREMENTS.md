# Acceptance Criteria — where each one is satisfied

Every criterion from the backlog item, mapped to the code, the test or the document that answers
it. Written so a reviewer can check the work without first having to find it.

Where something is deliberately documented rather than built, it says so and links to the reasoning.

---

## 1. Business requirements

| Requirement | Where | Notes |
|---|---|---|
| Gate operators need near real-time visibility of truck status | `GET /api/visits` with `currentStatus` filter | `CurrentStatus` is denormalised onto `visits` so a list query needs no aggregation (ADR-006) |
| Regulatory audits occur regularly | `visit_status_history` | Immutability enforced at three levels — see §2 below |
| Support future expansion to multiple terminals without significant redesign | `TerminalCode`, terminal-scoped authorization, every index leading with `TerminalId` | Adding a terminal is data, not a change. The same key is the shard boundary if one terminal ever outgrows shared infrastructure |

## 2. Operational requirements

| Requirement | Where | Notes |
|---|---|---|
| Up to 20 000 truck visits per day | [ARCHITECTURE §2](ARCHITECTURE.md#2-what-the-numbers-actually-mean) | 0.23 writes/second — the analysis that drives every other decision |
| Peaks of 300 requests per second | Index strategy, projection-only search, 3+ replicas, per-principal rate limiting | Read-dominated by ~3 orders of magnitude |
| 99.95% availability | `deploy/k8s/truckvisit-api.yaml` | 3 replicas, `maxUnavailable: 0`, zone spread, PDB, separate liveness/readiness, `preStop` drain, EF retry-on-failure for RDS failover |
| Full audit history of status changes | `StatusChange`, `TruckVisitDbContext.GuardAuditTrail`, migration trigger | (1) no public setter, asserted by reflection test; (2) `SaveChanges` refuses a modified entry; (3) database trigger refuses raw `UPDATE`/`DELETE`, proven by integration test |
| Retention period of 7 years | [ARCHITECTURE §6](ARCHITECTURE.md#retention-and-growth) | ~330 M rows / ~85 GB. Monthly partitioning and archival tiering are **documented, not implemented** — see [limitation 1](ASSUMPTIONS-AND-TRADEOFFS.md#4-known-limitations) |

## 3. Architectural requirements

| Requirement | Where |
|---|---|
| High-level architecture diagram | [ARCHITECTURE §3](ARCHITECTURE.md#3-high-level-architecture) — context and component diagrams |
| Logical component and module boundaries | [ARCHITECTURE §3](ARCHITECTURE.md#internal-structure). Enforced, not just drawn: Domain and Application have zero NuGet references |
| API design | [ARCHITECTURE §5](ARCHITECTURE.md#5-api-design) |
| Data storage rationale | [ARCHITECTURE §6](ARCHITECTURE.md#6-data-storage) |
| Security model | [ARCHITECTURE §7](ARCHITECTURE.md#7-security-model) |

## 4. Technical requirements

| Requirement | Where |
|---|---|
| Endpoint for adding a visit record | `POST /api/visits` |
| Endpoint for retrieving a visit by Id | `GET /api/visits/{id}` |
| Endpoint for searching visits | `GET /api/visits` |
| `terminalId`, `currentStatus`, `movementFrom`, `movementTo`, `createdTimeFrom`, `createdTimeTo`, `createdBy`, `page`, `pageSize` | All nine bound in `VisitEndpoints.SearchVisitsAsync`; `movementFrom`/`movementTo` interpreted as location codes — [assumption A3](ASSUMPTIONS-AND-TRADEOFFS.md#a3--movementfrom-and-movementto) |
| Current status may be updated | `PATCH /api/visits/{id}/status` — absent from the task list; [assumption A2](ASSUMPTIONS-AND-TRADEOFFS.md#a2--a-status-update-endpoint-that-the-task-list-omits) |
| All transitions retained as immutable audit history | See §2 above |
| *(added)* A movement can be marked complete | `POST /api/visits/{id}/movements/{movementId}/completion` — a visit cannot reach `Completed` while any movement is still outstanding, a rule the task list does not ask for but which closes a real gap: without it a visit could be marked finished while its cargo was never moved |
| *(added)* The audit chain can be verified independently | `GET /api/visits/{id}/audit/verification` — each entry carries a SHA-256 hash chained to the previous one (`StatusChange.EntryHash`/`PreviousHash`); this endpoint proves the chain is unbroken, or says exactly where it isn't — see [ARCHITECTURE §6](ARCHITECTURE.md#immutability--enforced-three-times) and `AuditTamperDetectionTests` |
| `hasOutstandingMovements`, `movementCompletedFrom`, `movementCompletedTo` search filters | The gate worklist query: on site, cargo not yet moved |
| Statuses: Pre-Registered, At Gate, On Site, Completed | `VisitStatus`; transition rules in `VisitStatusTransitions` — [assumption A1](ASSUMPTIONS-AND-TRADEOFFS.md#a1--lifecycle-shape) |
| Record includes truck, driver, collections and deliveries | `Visit`, `Truck`, `Driver`, `Movement` |
| Truck has at least a unit number and license plate | `Truck.Create` — both required |
| Driver information captured | `Driver` — fields are [assumption A4](ASSUMPTIONS-AND-TRADEOFFS.md#a4--driver-fields) |
| Movements support collections and deliveries | `MovementType` |
| Unit numbers and plates capitalised, no whitespace | `NormalizedCode` — normalised in the constructor, with a `tr-TR` test proving it is culture-independent |
| Latest stable version of .NET | .NET 10 (LTS) across every project |
| All business logic covered by unit tests | Domain and application layers, no infrastructure required — see the total below |
| Architecture rules enforced, not just documented | `TruckVisit.ArchitectureTests` — layer boundaries, package references, and domain invariants (no public setter outside `init`, no mutable collection exposed) fail the build if broken |
| Integration tests where they add confidence | 17 tests against real PostgreSQL, in three files: `VisitPersistenceTests` (round-tripping, concurrency, search), `AuditTamperDetectionTests` (the append-only trigger, and tamper detection with the trigger switched off), `TenantIsolationTests` (row-level security, proven the same way — assume every application-layer guard is absent) |

**148 tests in total** (`dotnet test`). The count is not asserted here as a fixed number for long —
it is the current total the moment this document was last touched; `dotnet test`'s own summary is
the source of truth.

## 5. Security requirements

| Requirement | Where |
|---|---|
| OAuth2 / JWT Bearer authentication | `AddJwtBearer`, bound from `Authentication:Schemes:Bearer` so `dotnet user-jwts` issues real signed tokens locally — no development bypass in the pipeline |
| Authorization based on terminal access | `CurrentUser`, `SearchVisitsHandler.ResolveTerminalScope`, enforced in every handler |
| TLS enforced | `UseHsts` + `UseHttpsRedirection` outside Development; TLS terminates at the ingress in a cluster |
| Input validation | Value objects reject at construction; handlers bound paging and date ranges |
| Security headers | `SecurityHeadersMiddleware` |
| Audit logging | `visit_status_history` — queryable, retained, immutable, attributed to the token's `sub` |
| Secrets managed outside source control | Environment / user-secrets only; start-up fails fast if absent. `.gitignore` excludes local settings |
| *(beyond the brief)* Supply-chain gate | `NuGetAudit` at `low` in `all` mode with warnings as errors; three transitive CVEs pinned forward |
| *(beyond the brief)* Rate limiting | Per-principal, health probes exempt — [D9](ASSUMPTIONS-AND-TRADEOFFS.md#d9--rate-limiting) |
| *(beyond the brief)* Tenant isolation enforced twice | Application-layer filtering, plus PostgreSQL row-level security as an independent database-level gate — [ADR-0016](adr/0016-tenant-isolation-enforced-twice-application-and-postgresql-rls.md), `TenantIsolationTests` |

## 6. Observability requirements

| Requirement | Where |
|---|---|
| Structured logging | JSON console with scopes; `[LoggerMessage]` source generation keeps templates queryable |
| Correlation IDs | `CorrelationIdMiddleware` — honours the client's header, bounds it, echoes it on every response including errors |
| Metrics for API throughput and latency | ASP.NET Core's built-in `http.server.request.duration` |
| *(added)* Business metrics | `VisitMetrics` — registrations, transitions, and refused transitions |
| Audit logging of status changes | `visit_status_history` |
| Health check endpoint | `/health/live` and `/health/ready`, deliberately separate |
| Error tracking strategy | `GlobalExceptionHandler` — one mapping; known failures at information level, unknown at error with correlation id and no detail in the body |

## 7. Bonus

| Topic | Where |
|---|---|
| Containerising with Docker | `Dockerfile` — multi-stage, tests run inside the build, chiselled non-root runtime. `docker-compose.yml` for local |
| Kubernetes deployment | `deploy/k8s/truckvisit-api.yaml` — sized from the operational requirements, each decision commented |
| AWS services | [ARCHITECTURE §10](ARCHITECTURE.md#10-deployment) — RDS Multi-AZ, Secrets Manager, EKS, ALB, CloudWatch, ECR, migrations as a pipeline step |

## 8. Tasks

| # | Task | Status |
|---|---|---|
| 1 | Design API endpoints | [ARCHITECTURE §5](ARCHITECTURE.md#5-api-design) + OpenAPI document |
| 2 | Implement create endpoint | `POST /api/visits`, with idempotency |
| 3 | Implement read endpoint | `GET /api/visits/{id}`, 404 for absent *and* invisible |
| 4 | Implement search endpoint | `GET /api/visits`, filters + paging metadata |
| 5 | Data storage setup | EF Core + PostgreSQL, migration, `docker-compose` |
| 6 | Security implementation | §5 above |
| 7 | Unit testing | 148 tests total across four projects — see §4 above |
| 8 | Code review and refactoring | Visible in the commit history — e.g. `harden(domain)` after review, and `fix(infra): a concurrent status change must answer 409, not 500`, which an integration test found |
| 9 | *(added)* Prove the capacity claim | [ARCHITECTURE §9](ARCHITECTURE.md#9-scalability) — measured against a seeded million-row database, not left as arithmetic; `tools/capacity/` |
| 10 | *(added)* CI | `.github/workflows/ci.yml` — build, analyse, test against a real PostgreSQL service container, then a separate image-build job |

## Submission checklist

| Item | Status |
|---|---|
| Source code in a GitHub repository | ✅ |
| Commit history showing progression of work | ✅ each commit explains *why*, not just *what* |
| README with setup, execution and tooling requirements | ✅ [README.md](../README.md) |
| Architecture documentation | ✅ [ARCHITECTURE.md](ARCHITECTURE.md) |
| Assumptions and trade-offs document | ✅ [ASSUMPTIONS-AND-TRADEOFFS.md](ASSUMPTIONS-AND-TRADEOFFS.md) |
| Decision records, one per architectural choice | ✅ [docs/adr/](adr/) |
| Test execution instructions | ✅ [README — Running the tests](../README.md#running-the-tests) |
| CI pipeline | ✅ [.github/workflows/ci.yml](../.github/workflows/ci.yml) |
| Capacity claim measured, not just argued | ✅ [ARCHITECTURE §9](ARCHITECTURE.md#9-scalability) |
