# ADR-0003: Append-only audit enforced at three levels

**Status:** Accepted — implemented in `StatusChange.cs`, `TruckVisitDbContext.SaveChanges`, and the
`trg_visit_status_history_append_only` trigger in the `20260921103604_InitialCreate` migration.

## Context

The service's central requirement is a full audit history of status changes retained for seven
years, reconstructable for regulatory audits. `visit_status_history` is the table an audit actually
inspects, so a guarantee that depends on every call site remembering not to mutate it is not a
guarantee — it is a hope. Immutability has to hold against application code that forgets, against
attached entity graphs and bulk operations that bypass the aggregate, and against a direct SQL
statement run by whoever holds credentials.

## Decision

Append-only is enforced three times, at three different levels, each catching what the level below
it cannot. **Type level:** `StatusChange` exposes no setter and no mutating method, checked by a
reflection-based unit test that fails the build if one is added. **Persistence level:**
`SaveChanges` inspects the EF change tracker and throws if any audit entry is `Modified` or
`Deleted`. **Database level:** a `BEFORE UPDATE OR DELETE` trigger on `visit_status_history` raises
an exception on any direct SQL attempt — a trigger rather than `REVOKE`, because `REVOKE` does not
constrain a superuser and the owning role usually is one; `REVOKE` is applied to the application
role in production as an additional layer, and RLS grants (ADR-0016) later make it literally true
rather than aspirational.

## Alternatives considered

- SQL Server temporal tables or PostgreSQL system-versioning were considered and rejected: they
  record *that* a row changed, not *why* it was allowed to, and the audit trail here is a business
  concept carrying `Reason`, `ChangedBy` and a gapless `Sequence` that must be validated before
  anything is written. Automatic versioning would also tie the model to one vendor.
- Event sourcing was considered seriously, since the requirement is literally an immutable log of
  state changes, but rejected: it would force the search endpoint to build and maintain projections
  over 51 million visits for no requirement to replay or derive alternative histories.

## Consequences

Three enforcement points is more code than trusting convention, and `AuditTamperDetectionTests`
exists specifically to prove each layer holds by assuming every layer above it is absent. The payoff
is that the guarantee the whole feature exists to provide does not depend on anyone remembering
anything, at any layer, including a direct database connection.
