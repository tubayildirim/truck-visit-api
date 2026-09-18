using TruckVisit.Application.Abstractions;

namespace TruckVisit.Application.Visits;

/// <summary>Use case: read one visit (GET /api/visits/{id}).</summary>
public sealed class GetVisitByIdHandler(IVisitRepository repository, ICurrentUser currentUser)
{
    public async Task<VisitDetailView> HandleAsync(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await repository.FindAsync(visitId, cancellationToken);

        // A visit at a terminal the caller cannot see is reported as missing, not as forbidden.
        // Distinguishing the two would let anyone with a token probe for which identifiers exist
        // elsewhere in the network.
        if (visit is null || !currentUser.CanAccessTerminal(visit.TerminalId.Value))
        {
            throw new VisitNotFoundException(visitId);
        }

        return VisitMapper.ToDetail(visit);
    }
}
