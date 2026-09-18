using TruckVisit.Application.Abstractions;

namespace TruckVisit.Application.Visits;

/// <summary>
/// Use case: advance a visit's status (PATCH /api/visits/{id}/status).
/// </summary>
/// <remarks>
/// This endpoint is not in the case's task list, but the acceptance criteria state that a visit's
/// current status may be updated while all transitions are retained. Without a way to change the
/// status, the audit requirement — the point of the feature — could never be exercised. The gap is
/// recorded in the assumptions document.
/// </remarks>
public sealed class ChangeVisitStatusHandler(
    IVisitRepository repository,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public async Task<VisitDetailView> HandleAsync(
        ChangeVisitStatusCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var visit = await repository.FindAsync(command.VisitId, cancellationToken)
            ?? throw new VisitNotFoundException(command.VisitId);

        if (!currentUser.CanAccessTerminal(visit.TerminalId.Value))
        {
            throw new VisitNotFoundException(command.VisitId);
        }

        // The domain owns the decision. If the transition is illegal this throws and nothing is
        // written — the audit trail never records an event that was refused.
        visit.ChangeStatus(
            command.TargetStatus,
            changedBy: currentUser.UserId,
            occurredAt: timeProvider.GetUtcNow(),
            reason: command.Reason);

        await repository.SaveChangesAsync(cancellationToken);

        return VisitMapper.ToDetail(visit);
    }
}
