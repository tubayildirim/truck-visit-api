# ADR-0012: Security advisories fail the build

**Status:** Accepted — configured in `Directory.Packages.props`
(`NuGetAudit=true`, `NuGetAuditMode=all`, `NuGetAuditLevel=low`, warnings as errors).

## Context

Dependencies accumulate known vulnerabilities after they are pinned, including transitively, where
no one is looking at the direct reference that changed nothing. A vulnerability scan that only warns
is a scan whose output is routinely ignored under delivery pressure — the failure mode is not
"the tool didn't run," it is "the tool ran and nobody acted on it."

## Decision

`NuGetAudit` is enabled at `low` severity in `all` mode (direct and transitive dependencies), and
warnings are treated as build errors — so a known vulnerability in any dependency fails the build,
full stop. Three transitive advisories were found during development
(`Microsoft.OpenApi` 2.0.0 → 2.12.2, `System.Security.Cryptography.Xml` 9.0.0 → 10.0.12, `SSH.NET`
2024.2.0 → 2026.0.0) and were pinned forward rather than suppressed.

## Alternatives considered

- Leaving `NuGetAudit` at its default (warn, not error) was the implicit alternative, and was
  rejected on the stated principle that a quality gate that only warns is a quality gate that is
  ignored.

## Consequences

The build breaks on advisories in transitive dependencies the team did not choose directly, which
can block work until a fix or a pin is available — friction that is treated as acceptable because
every instance of it during development was a real signal, including all three CVEs above.
Suppressing an advisory without pinning forward is not an option this configuration allows.
