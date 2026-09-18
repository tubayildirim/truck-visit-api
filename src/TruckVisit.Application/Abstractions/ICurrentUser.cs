namespace TruckVisit.Application.Abstractions;

/// <summary>
/// The authenticated caller, as the use cases need to see them.
/// </summary>
/// <remarks>
/// Implemented over the bearer token's claims in the API layer. The application layer depends on
/// this abstraction rather than on <c>HttpContext</c> so that use cases stay testable and so that
/// "who is acting" can never be taken from a request body — the case requires <c>createdBy</c> to
/// be trustworthy, and a client-supplied value is not.
/// </remarks>
public interface ICurrentUser
{
    /// <summary>Stable identifier of the principal, recorded in the audit trail.</summary>
    string UserId { get; }

    /// <summary>Terminals this principal may read and write. Empty means no terminal access.</summary>
    IReadOnlySet<string> TerminalIds { get; }

    /// <summary>True when the principal may act across every terminal (e.g. a compliance auditor).</summary>
    bool HasGlobalTerminalAccess { get; }

    /// <summary>
    /// Whether the principal may act on <paramref name="terminalId"/>.
    /// The argument is compared in its normalised form, so casing and stray whitespace in a token
    /// claim cannot silently grant or deny access.
    /// </summary>
    bool CanAccessTerminal(string terminalId);
}
