using System.Security.Claims;
using TruckVisit.Application.Abstractions;
using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Api.Security;

/// <summary>
/// Reads the authenticated caller out of the bearer token's claims.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a claim is turned into an authorization fact. Terminal claims are pushed
/// through <see cref="TerminalCode"/> so they arrive in the same canonical form as the stored
/// value — otherwise a token issued with "dover " would silently fail to match rows saved as
/// "DOVER", and an operator would be told there are no trucks at their own terminal.
/// </para>
/// <para>
/// A malformed terminal claim is dropped rather than throwing. One bad entry in a token should
/// narrow what the caller can see, never hand them a 500 or — worse — abort the filter and leave
/// the query unscoped.
/// </para>
/// </remarks>
internal sealed class CurrentUser(IHttpContextAccessor httpContextAccessor) : ICurrentUser
{
    /// <summary>Claim carrying one terminal the caller may act on. May appear multiple times.</summary>
    public const string TerminalClaimType = "terminal";

    /// <summary>Claim value granting access to every terminal, e.g. for a compliance auditor.</summary>
    public const string GlobalAccessClaimValue = "terminals.all";

    /// <summary>Claim type that may carry <see cref="GlobalAccessClaimValue"/>.</summary>
    public const string ScopeClaimType = "scope";

    private IReadOnlySet<string>? _terminalIds;

    public string UserId =>
        Principal?.FindFirstValue("sub")
        ?? Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new InvalidOperationException(
            "The authenticated principal carries no 'sub' claim. The audit trail cannot attribute "
            + "this request, so it must not proceed.");

    public IReadOnlySet<string> TerminalIds => _terminalIds ??= ReadTerminalClaims();

    public bool HasGlobalTerminalAccess =>
        Principal?.HasClaim(ScopeClaimType, GlobalAccessClaimValue) == true;

    private ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;

    public bool CanAccessTerminal(string terminalId)
    {
        if (HasGlobalTerminalAccess)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return false;
        }

        try
        {
            return TerminalIds.Contains(TerminalCode.Create(terminalId).Value);
        }
        catch (DomainValidationException)
        {
            // An unusable terminal identifier is not an identifier the caller holds.
            return false;
        }
    }

    private HashSet<string> ReadTerminalClaims()
    {
        var principal = Principal;

        if (principal is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var terminals = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in principal.FindAll(TerminalClaimType))
        {
            try
            {
                terminals.Add(TerminalCode.Create(claim.Value).Value);
            }
            catch (DomainValidationException)
            {
                // Skip: see the remarks on this class.
            }
        }

        return terminals;
    }
}
