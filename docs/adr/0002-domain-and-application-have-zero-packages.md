# ADR-0002: Domain and Application have zero packages

**Status:** Accepted — enforced by `TruckVisit.Domain.csproj` and `TruckVisit.Application.csproj`
carrying no `<PackageReference>`.

## Context

Dependencies in the internal architecture point inward only: `Api` and `Infrastructure` depend on
`Application`, which depends on `Domain`. Nothing stops that discipline from eroding over time
except a rule that is checked, not one that is merely written down — a business rule expressed as a
framework attribute or a library call is a rule that can no longer be reasoned about, or tested,
independently of that framework.

## Decision

`TruckVisit.Domain` and `TruckVisit.Application` reference **zero NuGet packages**. `Domain` has no
`<ItemGroup>` at all; `Application` references only the `Domain` project. If either project ever
grows a package reference, that is the signal a business rule has leaked into a framework.

## Alternatives considered

- The conventional trio — MediatR, AutoMapper, FluentValidation — was considered for the
  Application layer specifically and rejected (see ADR-0009): at four use cases, each adds a
  dependency and a layer of indirection without removing a line of logic.

## Consequences

Use cases are plain classes, mapping is hand-written, and validation lives in value-object
constructors — more code written by hand than a framework would generate, but every one of those
paths is a compile error when it breaks rather than a runtime surprise. The zero-package rule is
enforced structurally by the `.csproj` files themselves, so it cannot regress silently; it can still
be defeated by a reviewer approving a package reference that should have been rejected.
