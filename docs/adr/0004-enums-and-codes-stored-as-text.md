# ADR-0004: Enums and codes stored as text

**Status:** Accepted — implemented in the EF Core value converters in
`TruckVisit.Infrastructure/Persistence/Configurations`.

## Context

`visit_status_history` rows are retained for seven years — long enough that the rows will outlive
the version of the code that wrote them, and long enough that whoever reads them during a
regulatory audit may not have that code, or that enum definition, in front of them at all.

## Decision

Status enums and coded value objects (`TerminalCode`, `LocationCode`, and the visit status itself)
are persisted as text, not as integer enum ordinals. `AtGate` is still legible to an auditor in
2033; a bare `2` is legible only next to a copy of the enum that produced it, which is exactly the
artifact that is not guaranteed to still exist.

## Alternatives considered

- Storing status as an integer ordinal (the EF Core default for enums) was the implicit alternative
  and was rejected on the same reasoning that drives the rest of the data model: the audit trail
  must remain readable independent of the application that produced it.

## Consequences

Text columns are a few bytes larger per row than an integer ordinal — negligible against the ~85 GB
total footprint at seven years — and comparisons and indexes work identically either way. What is
bought is that the stored data is self-describing: no lookup table, no application deployment, no
tribal knowledge is required to read `visit_status_history` correctly years after the code that
wrote it has changed or been retired.
