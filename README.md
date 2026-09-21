# Truck Visit Management API

A .NET 10 REST API for the "Smart Gate" solution: it records truck visits to a terminal, tracks
them through their lifecycle, and retains every status change as an immutable audit trail.

| | |
|---|---|
| **Architecture** | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| **Assumptions & trade-offs** | [docs/ASSUMPTIONS-AND-TRADEOFFS.md](docs/ASSUMPTIONS-AND-TRADEOFFS.md) |
| **Acceptance criteria, mapped** | [docs/REQUIREMENTS.md](docs/REQUIREMENTS.md) |

---

## Requirements

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | **10.0** | Everything |
| Docker | any recent | PostgreSQL, and the integration tests |

`dotnet-ef` is **not** installed globally — it is pinned in `.config/dotnet-tools.json` and restored
with `dotnet tool restore`, so migrations are always generated with the version that matches the
runtime.

---

## Quick start

### Option A — everything in containers

```bash
docker compose up --build
```

The API is on `http://localhost:8080`. The image build runs the domain unit tests, so a build that
succeeds has already passed them.

### Option B — database in Docker, API from the SDK

```bash
# 1. PostgreSQL
docker compose up -d postgres

# 2. Tooling and schema
dotnet tool restore
dotnet ef database update \
  --project src/TruckVisit.Infrastructure \
  --startup-project src/TruckVisit.Api

# 3. Run
dotnet run --project src/TruckVisit.Api
```

The API starts on `http://localhost:5080`. In Development it applies pending migrations at start-up
and serves its OpenAPI document at `/openapi/v1.json`.

> Production never migrates at start-up — an application that migrates its own schema races itself
> the moment it runs more than one replica. See [ARCHITECTURE §10](docs/ARCHITECTURE.md#10-deployment).

### Configuration

No secret is committed. The connection string is read from configuration in this order:

```bash
# Deployed environments
export ConnectionStrings__TruckVisit="Host=...;Database=truckvisit;Username=...;Password=..."

# Local, if you do not want the docker-compose defaults
dotnet user-secrets set "ConnectionStrings:TruckVisit" "Host=localhost;..." \
  --project src/TruckVisit.Api
```

Start-up fails immediately if it is missing, rather than on the first request that touches the
database.

---

## Getting a token

Every endpoint except the health probes requires a JWT. Local development uses **real, signed
tokens** issued by the built-in `dotnet user-jwts` tool — there is no development bypass in the
pipeline, because that is the shortcut that quietly reaches production.

```bash
# An operator at one terminal
dotnet user-jwts create \
  --project src/TruckVisit.Api \
  --name operator-1 \
  --claim terminal=DOVER

# An operator covering two terminals
dotnet user-jwts create \
  --project src/TruckVisit.Api \
  --name operator-2 \
  --claim terminal=DOVER \
  --claim terminal=HARWICH

# A compliance auditor with cross-terminal read access
dotnet user-jwts create \
  --project src/TruckVisit.Api \
  --name auditor-1 \
  --scope terminals.all
```

The command prints the token and stores the signing key in user-secrets — not in the repository.

| Claim | Meaning |
|---|---|
| `sub` | Recorded as `createdBy` / `changedBy`. Never taken from a request body |
| `terminal` | One terminal the caller may act on. May repeat |
| `scope=terminals.all` | Cross-terminal access |

---

## Example requests

```bash
TOKEN="<paste the token>"
API=http://localhost:5080
```

### Register a visit

```bash
curl -X POST "$API/api/visits" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: gate-terminal-3-20260921-0001" \
  -d '{
    "terminalId": "DOVER",
    "truck":  { "unitNumber": "mscu 123 4567", "licensePlate": "34 abc 123" },
    "driver": { "fullName": "Ada Lovelace", "documentId": "A1234567", "phoneNumber": "+905001234567" },
    "movements": [
      { "type": "Delivery",   "unitNumber": "MSCU1234567", "from": "DEPOT-A", "to": "YARD1"  },
      { "type": "Collection", "unitNumber": "TGHU7654321", "from": "YARD3",   "to": "DEPOT-B" }
    ]
  }'
```

Returns `201` with the stored visit. The unit number and plate come back as `MSCU1234567` and
`34ABC123` — normalised on the way in, per the acceptance criteria.

Repeat the identical call with the same `Idempotency-Key` and it returns **`200`** with the same
visit, rather than creating a second one.

### Advance the status

```bash
curl -X PATCH "$API/api/visits/{id}/status" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{ "status": "AtGate", "reason": "Arrived ahead of slot." }'
```

Skipping a step (`PreRegistered → OnSite`) returns `409` with a problem document naming the allowed
next status. Nothing is written when a transition is refused.

### Read one visit, with its full audit trail

```bash
curl "$API/api/visits/{id}" -H "Authorization: Bearer $TOKEN"
```

### Search

```bash
curl -G "$API/api/visits" \
  -H "Authorization: Bearer $TOKEN" \
  --data-urlencode "terminalId=DOVER" \
  --data-urlencode "currentStatus=AtGate" \
  --data-urlencode "movementFrom=YARD1" \
  --data-urlencode "createdTimeFrom=2026-09-01T00:00:00Z" \
  --data-urlencode "page=1" \
  --data-urlencode "pageSize=25"
```

| Parameter | Type |
|---|---|
| `terminalId` | string, normalised |
| `currentStatus` | `PreRegistered` \| `AtGate` \| `OnSite` \| `Completed` |
| `movementFrom`, `movementTo` | location codes, normalised |
| `createdTimeFrom`, `createdTimeTo` | ISO 8601 |
| `createdBy` | string, exact match |
| `page` | ≥ 1, default 1 |
| `pageSize` | 1–200, default 25 |

Responses carry `items`, `page`, `pageSize`, `totalCount`, `totalPages`, `hasNextPage`,
`hasPreviousPage`.

### Health

```bash
curl "$API/health/live"    # process alive — runs no dependency checks
curl "$API/health/ready"   # database reachable — gates load-balancer membership
```

---

## Running the tests

```bash
# Everything
dotnet test

# Domain rules only — no database, no Docker, runs in milliseconds
dotnet test tests/TruckVisit.Domain.Tests
```

`tests/TruckVisit.Domain.Tests` covers the lifecycle state machine, the audit guarantee, the
normalisation rules and the validation boundaries — including a test that runs under a `tr-TR`
locale, where the default `ToUpper()` maps `i` to `İ` and would store the same plate differently
depending on the server's regional settings.

Integration tests use Testcontainers and need Docker running; they exercise the real pipeline
through `WebApplicationFactory` against a real PostgreSQL rather than an in-memory substitute,
because an in-memory provider cannot prove that the append-only constraints and indexes exist.

---

## Project layout

```
src/
  TruckVisit.Domain          business rules — zero NuGet references
  TruckVisit.Application     use cases and ports — zero NuGet references
  TruckVisit.Infrastructure  EF Core, PostgreSQL, adapters
  TruckVisit.Api             HTTP edge, auth, observability, composition root
tests/
  TruckVisit.Domain.Tests        67 tests, no infrastructure
  TruckVisit.Application.Tests   use-case behaviour with test doubles
  TruckVisit.Api.IntegrationTests  real pipeline, real database
deploy/k8s                   sample Kubernetes manifest
docs/                        architecture, assumptions and trade-offs
```

Dependencies point inward only. The domain and application layers reference no NuGet packages at
all — if either grows an `<ItemGroup>`, a business rule has leaked into a framework.

---

## Build quality gates

Warnings are errors, solution-wide:

- **Analyzers** at `latest-recommended`, with `CA1304` / `CA1305` / `CA1310` promoted to errors so
  no culture-sensitive string operation can be written implicitly.
- **`NuGetAudit`** at `low` severity in `all` mode: a known vulnerability in any dependency, direct
  or transitive, fails the build. Three were found during development and pinned forward — see
  `Directory.Packages.props`.
- **Central package management**: every version lives in one file.

`CA1707` (no underscores in member names) is suppressed in `tests/` only, so test names can read as
sentences. Production code keeps the complete set.
