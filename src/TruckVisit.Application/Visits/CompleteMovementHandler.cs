using TruckVisit.Application.Abstractions;

namespace TruckVisit.Application.Visits;

/// <summary>
/// Use case: record that a collection or delivery has been carried out
/// (POST /api/visits/{id}/movements/{movementId}/completion).
/// </summary>
/// <remarks>
/// Not asked for by the case, which treats movements as static data attached to a visit. Modelling
/// them as work that is declared and then done is what lets the gate refuse to release a truck
/// whose cargo was never moved — without it, the record could show a completed visit whose
/// collections never happened, and nothing in the system would notice.
/// </remarks>
public sealed class CompleteMovementHandler(
    IVisitRepository repository,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<VisitDetailView> HandleAsync(
        CompleteMovementCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var visit = await repository.FindAsync(command.VisitId, cancellationToken)
            ?? throw new VisitNotFoundException(command.VisitId);

        if (!currentUser.CanAccessTerminal(visit.TerminalId.Value))
        {
            throw new VisitNotFoundException(command.VisitId);
        }

        visit.CompleteMovement(
            command.MovementId,
            completedBy: currentUser.UserId,
            occurredAt: timeProvider.GetUtcNow());

        await repository.SaveChangesAsync(cancellationToken);

        return VisitMapper.ToDetail(visit);
    }
}

/// <summary>
/// Use case: verify that a visit's audit trail has not been altered
/// (GET /api/visits/{id}/audit/verification).
/// </summary>
/// <remarks>
/// The answer a regulator actually wants is not "can this be edited" but "was it". The hash chain
/// carries its own proof, so this needs no reference copy and no trusted third party — it walks the
/// trail and reports the first break, if there is one.
/// </remarks>
public sealed class VerifyAuditTrailHandler(IVisitRepository repository, ICurrentUser currentUser)
{
    public async Task<AuditVerificationView> HandleAsync(Guid visitId, CancellationToken cancellationToken)
    {
        var visit = await repository.FindAsync(visitId, cancellationToken);

        if (visit is null || !currentUser.CanAccessTerminal(visit.TerminalId.Value))
        {
            throw new VisitNotFoundException(visitId);
        }

        return VisitMapper.ToView(visitId, visit.VerifyAuditTrail());
    }
}
