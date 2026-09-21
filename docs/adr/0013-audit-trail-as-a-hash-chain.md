# ADR-0013: Audit trail as a hash chain

**Status:** Accepted — implemented in `StatusChange.EntryHash`/`PreviousHash`/`IsSelfConsistent()`
and `Visit.VerifyAuditTrail()` (`TruckVisit.Domain`), proved by
`tests/TruckVisit.Api.IntegrationTests/AuditTamperDetectionTests.cs`.

## Context

Append-only is already enforced three times (ADR-0003): a type with no mutating members, a
`SaveChanges` guard, and a `BEFORE UPDATE OR DELETE` trigger. All three *prevent* tampering, and
none of them can *detect* it if prevention is circumvented — someone with database access can
disable the trigger, edit a row, and re-enable it, leaving nothing in the database that records that
it happened. For a system whose stated purpose is surviving regular regulatory audits, that is the
wrong failure mode: a regulator does not ask whether the table could have been edited, they ask
whether it was.

## Decision

Each `StatusChange` stores a SHA-256 digest of its own fields combined with the previous entry's
digest (`EntryHash`, `PreviousHash`), so the trail is a chain rather than a pile of independent rows.
`IsSelfConsistent()` recomputes an entry's hash and compares it to the stored one; `Visit
.VerifyAuditTrail()` walks the whole chain and returns an `AuditVerification` — intact, or broken at
a specific sequence with a finding describing what failed. Altering any entry invalidates every hash
after it, and the break is detectable without needing an untouched copy to compare against. The
canonical byte representation uses a delimiter that cannot occur in any field, and timestamps in
round-trip invariant-culture format, so two different sets of fields cannot serialise to the same
string.

## Alternatives considered

- Relying on the trigger and the `SaveChanges` guard alone was the status quo this ADR adds to. It
  was judged insufficient specifically because both are prevention mechanisms that assume the
  account running the write is constrained — a DBA, a restore script, or anyone holding the owning
  role is not.

## Consequences

Every write computes and stores an extra hash, and every audit read can optionally walk the chain to
verify it — cost that is negligible against the read/write volumes involved. In exchange, an attack
that neither the trigger nor the application layer can stop (owning-role access, trigger disabled,
row edited, trigger re-enabled) is still caught: `AuditTamperDetectionTests` proves this concretely
by disabling the trigger, editing a row's `Reason`, re-enabling the trigger, and showing
`VerifyAuditTrail()` still reports the break at the correct sequence — and the same test proves a
deleted entry is caught too, as a gap in the chain rather than a changed hash.
