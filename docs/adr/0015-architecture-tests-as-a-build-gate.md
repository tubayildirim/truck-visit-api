# ADR-0015: Architecture tests as a build gate

**Status:** Accepted — implemented in
`tests/TruckVisit.ArchitectureTests/LayerBoundaryTests.cs` and `DomainInvariantTests.cs`.

## Context

This document asserts a set of structural claims about the codebase — zero packages in `Domain` and
`Application` (ADR-0002), no mediator/mapper/validation library (ADR-0009), an aggregate whose state
can only change through its own methods, value objects that are sealed. Every codebase's README
claims it follows clean architecture; six months and forty pull requests later the domain references
the ORM "just for this one query" and nobody remembers agreeing not to. A rule that lives only in a
document is a rule that erodes; a rule that fails the build is a rule that holds.

## Decision

`LayerBoundaryTests` and `DomainInvariantTests` turn the architecture document's claims into
assertions that run on every build. `LayerBoundaryTests` checks that `Domain` and `Application`
declare no NuGet package references, that `Domain` depends on nothing else in the solution, that
`Application` depends only on `Domain`, that neither references an infrastructure framework by name,
and that persistence types stay internal to `Infrastructure`. `DomainInvariantTests` checks by
reflection that no domain type has a public mutable setter, that no domain type exposes a mutable
collection type, that every value object is sealed, that every domain exception derives from the
shared `DomainException` base, and — named separately because the whole audit feature rests on it —
that `StatusChange` exposes no public mutating member.

## Alternatives considered

- Relying on code review and the architecture document alone was the status quo, and it is the
  thing these tests exist to replace: a reviewer can miss a violation the way any manual check can,
  and a document cannot fail a build.

## Consequences

Writing and maintaining these tests is extra effort for rules that produce no user-visible behavior
on their own. What they buy is a standing guard: any pull request that adds a package reference to
`Domain`, exposes a mutable collection, or gives a domain type a public setter fails the build
immediately rather than being caught — or missed — in review months later. The rule itself has to be
stated precisely to be checkable at all: the first version of `No_domain_type_can_be_mutated
_after_construction` said "no public setter" and failed on `init` accessors used by records like
`AuditVerification`, which are safely immutable after construction — the test forced the rule to be
restated as "no public setter that is not `init`-only," which is exactly the distinction that
matters and exactly the kind of correction a test like this is supposed to produce.
