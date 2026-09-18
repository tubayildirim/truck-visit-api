namespace TruckVisit.Application.Abstractions;

/// <summary>
/// The requested visit does not exist, or exists at a terminal the caller cannot see.
/// </summary>
/// <remarks>
/// Both cases deliberately produce the same 404. Returning 403 for the second would turn the
/// endpoint into an oracle: a caller could enumerate identifiers and learn which visits exist at
/// terminals they have no right to know about.
/// </remarks>
public sealed class VisitNotFoundException(Guid visitId)
    : Exception($"Visit '{visitId}' was not found.")
{
    public Guid VisitId { get; } = visitId;
}

/// <summary>
/// The caller is authenticated but holds no claim for the terminal they are acting on.
/// Maps to HTTP 403 — used on writes, where hiding the terminal's existence buys nothing.
/// </summary>
public sealed class TerminalAccessDeniedException(string terminalId)
    : Exception($"Caller is not authorized for terminal '{terminalId}'.")
{
    public string TerminalId { get; } = terminalId;
}

/// <summary>
/// A request argument is unusable before the domain is even reached (bad page size, inverted date
/// range). Maps to HTTP 400.
/// </summary>
public sealed class RequestValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// The visit was modified by someone else between read and write.
/// Maps to HTTP 409 (ADR-010).
/// </summary>
public sealed class ConcurrencyConflictException(Guid visitId)
    : Exception($"Visit '{visitId}' was modified by another request. Re-read it and retry.")
{
    public Guid VisitId { get; } = visitId;
}
