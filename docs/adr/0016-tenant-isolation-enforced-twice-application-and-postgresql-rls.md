# ADR-0016: Tenant isolation enforced twice — application filter and PostgreSQL RLS

**Status:** Accepted — implemented in `TenantScopeConnectionInterceptor.cs` and the
`TenantIsolationWithRowLevelSecurity` migration, on top of the application-layer row scoping.

## Context

Row scoping is resolved in the application layer, before the repository, so the repository itself
never reasons about authorization — an empty terminal scope returns an empty page, never all rows.
That gate holds only for connections that go through this codebase. A guarantee that depends on
application code remembering to apply a filter is not a guarantee — the same reasoning that already
justifies the append-only trigger for audit immutability (ADR-0003) applies equally to tenant
isolation, since a future query anywhere in the codebase could omit the `TerminalId` predicate.

## Decision

A second, independent gate is added directly in the database. `truckvisit_app` is a `NOLOGIN` role
that nothing connects as directly; `TruckVisitDbContext` always authenticates as the schema's owning
role for DDL, and `TenantScopeConnectionInterceptor` switches every connection opened for an
authenticated caller into `truckvisit_app` with `SET ROLE` immediately after setting the session
variables the RLS policies read: `app.current_terminals` (comma-joined, with a charset that excludes
the delimiter so no claim can smuggle a second terminal in) and `app.terminals_all_scope` for the
cross-terminal auditor case. All three tables run `FORCE ROW LEVEL SECURITY`, so even the owning
role's connection — the one exemption RLS grants by default — is bound by the same policies. The
policies read those variables with `current_setting(..., true)`, which returns `NULL` rather than
raising when a session never set them, so a connection that never opts into a scope sees nothing,
never everything — fail closed. State is re-established on every connection open rather than assumed
to survive from the last one, because Npgsql pools physical connections and one physical connection
legitimately serves different callers over its life. `truckvisit_app` is also granted no `DELETE` on
`visits` or `visit_movements`, and no `UPDATE` or `DELETE` at all on `visit_status_history` — the
same guarantee the append-only trigger gives, enforced a second time, at the privilege level.

## Alternatives considered

- Application-layer filtering alone was the status quo this ADR adds to, not replaces — it was kept
  because it is still the layer that turns an empty scope into an empty page rather than an error,
  and RLS is deliberately the second, independent gate rather than a replacement for it.

## Consequences

A write against a row outside the caller's scope behaves exactly like a read that finds nothing —
zero rows touched, indistinguishable from targeting a row that does not exist — which is the same
404-not-403 choice already made for reads, arrived at independently because it is how PostgreSQL's
row security behaves by default. `idempotency_records` deliberately carries no policy, since replays
are deduplicated by `(Key, UserId)`, not by terminal, and the table has no `TerminalId` to scope by.
One sharp edge is accepted rather than hidden: `FORCE` means an owner-role connection that ever
queried `visits` directly — nothing in this codebase does today — would see zero rows, not every
row, because its session set no scope and the policy fails closed; and a production database whose
migration-owner role is not literally named `truckvisit` needs the equivalent role-membership grant
run once, by hand, alongside the migration.
