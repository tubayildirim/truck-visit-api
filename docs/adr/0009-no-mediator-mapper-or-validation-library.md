# ADR-0009: No mediator, mapper or validation library

**Status:** Accepted — `TruckVisit.Application.csproj` carries no package references; use cases are
plain classes (`RegisterVisit`, `GetVisitById`, `SearchVisits`, `ChangeVisitStatus`).

## Context

The Application layer implements exactly four use cases. The conventional ASP.NET trio for a layer
like this is MediatR for request dispatch, AutoMapper for DTO mapping, and FluentValidation for
input rules — each solving a real problem at a larger scale, each adding a dependency and a layer of
indirection at this one.

## Decision

None of the three is used. Use cases are called directly, mapping between domain types and API
contracts is hand-written, and validation lives in value-object constructors and the domain's
transition rules rather than in a separate validation framework. The Application layer's package
count is zero, and that is enforced by its `.csproj`.

## Alternatives considered

- MediatR, AutoMapper and FluentValidation were considered together and rejected: at four use cases,
  each adds indirection without removing a line of logic. Hand-written mapping turns a renamed
  property into a compile error instead of a field that quietly becomes null in production. MediatR
  also became commercially licensed, which is a supply-chain decision a lead engineer should make
  deliberately rather than by habit.

## Consequences

More boilerplate is written by hand — mapping code, direct use-case calls — than a framework would
generate for free. In exchange, there is no indirection to trace through to find what runs for a
given request, no dependency whose licensing terms can change under the project, and a renamed field
fails the build instead of failing silently at runtime. This trade reverses at a larger number of
use cases; four is not that number.
