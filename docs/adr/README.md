# Architecture Decision Records

This directory holds one record per architectural decision made in this service, each with its own
context, the decision itself, the alternatives that were considered and rejected, and the
consequences accepted along with it. `docs/ARCHITECTURE.md` §11 "Decision record" has the summary
table these expand on.

| # | Title |
|---|---|
| ADR-0001 | [Modular monolith, not microservices](0001-modular-monolith-not-microservices.md) |
| ADR-0002 | [Domain and Application have zero packages](0002-domain-and-application-have-zero-packages.md) |
| ADR-0003 | [Append-only audit enforced at three levels](0003-append-only-audit-enforced-at-three-levels.md) |
| ADR-0004 | [Enums and codes stored as text](0004-enums-and-codes-stored-as-text.md) |
| ADR-0005 | [Normalise in value-object constructors](0005-normalise-in-value-object-constructors.md) |
| ADR-0006 | [Denormalise `CurrentStatus` and `LastStatusChangedAt`](0006-denormalise-current-status-and-last-status-changed-at.md) |
| ADR-0007 | [`TerminalId` as tenancy key and leading index column](0007-terminal-id-as-tenancy-key-and-leading-index-column.md) |
| ADR-0008 | [Search returns projections, not aggregates](0008-search-returns-projections-not-aggregates.md) |
| ADR-0009 | [No mediator, mapper or validation library](0009-no-mediator-mapper-or-validation-library.md) |
| ADR-0010 | [Optimistic concurrency via `xmin`](0010-optimistic-concurrency-via-xmin.md) |
| ADR-0011 | [`Idempotency-Key` on create](0011-idempotency-key-on-create.md) |
| ADR-0012 | [Security advisories fail the build](0012-security-advisories-fail-the-build.md) |
| ADR-0013 | [Audit trail as a hash chain](0013-audit-trail-as-a-hash-chain.md) |
| ADR-0014 | [Movement completion as a lifecycle gate](0014-movement-completion-as-a-lifecycle-gate.md) |
| ADR-0015 | [Architecture tests as a build gate](0015-architecture-tests-as-a-build-gate.md) |
| ADR-0016 | [Tenant isolation enforced twice: application filter and PostgreSQL RLS](0016-tenant-isolation-enforced-twice-application-and-postgresql-rls.md) |
