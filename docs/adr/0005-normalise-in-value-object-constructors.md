# ADR-0005: Normalise in value-object constructors

**Status:** Accepted — implemented in `UnitNumber`, `LicensePlate`, `TerminalCode` and
`LocationCode` (`TruckVisit.Domain`), built on `NormalizedCode.cs`.

## Context

The acceptance criteria require case-insensitive matching on coded fields, and terminal claims in a
JWT have to compare equal to the same codes as stored in the database. Getting normalisation wrong
is not cosmetic: under a Turkish locale, default upper-casing maps `i` to `İ` (U+0130), not `I` — a
server running under `tr-TR` would store the same plate differently from an identical server under
`en-GB`, producing two unmatchable records for one truck and an audit trail that cannot be joined
back together.

## Decision

`UnitNumber`, `LicensePlate`, `TerminalCode` and `LocationCode` normalise once, in their
constructors: whitespace stripped, upper-cased with `ToUpperInvariant` under the **invariant**
culture, never `ToUpper`. Because construction is the only way to obtain one of these types, no call
site — including the authorization path, which parses a `terminal` claim through the same
`TerminalCode` type as stored data — can forget to normalise.

## Alternatives considered

- Enabling `InvariantGlobalization` globally was considered and rejected: it would mask the
  culture-sensitivity problem rather than solve it, leaving other culture-sensitive calls in place
  to start corrupting data the day the switch was flipped. Instead, CA1304/CA1305/CA1310 are build
  errors, and a unit test runs under `tr-TR` specifically to prove the behaviour.

## Consequences

Normalisation logic exists in exactly one place per type instead of being repeated, and possibly
forgotten, at every call site. The cost is that the invariant-culture requirement has to be actively
defended — the analyzer rules and the `tr-TR` test both exist because "just remember to use
`ToUpperInvariant`" is precisely the kind of convention this codebase otherwise refuses to rely on.
