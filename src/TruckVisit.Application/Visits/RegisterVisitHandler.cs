using TruckVisit.Application.Abstractions;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Application.Visits;

/// <summary>Outcome of a register request.</summary>
/// <param name="Visit">The stored visit.</param>
/// <param name="WasReplayed">
/// True when an <c>Idempotency-Key</c> matched an earlier request and nothing new was created.
/// The API turns this into 200 rather than 201, so a retrying client can tell the difference.
/// </param>
public sealed record RegisterVisitResult(VisitDetailView Visit, bool WasReplayed);

/// <summary>Use case: register a truck visit (POST /api/visits).</summary>
public sealed class RegisterVisitHandler(
    IVisitRepository repository,
    IIdempotencyStore idempotencyStore,
    ICurrentUser currentUser,
    TimeProvider timeProvider)
{
    public const int MaxIdempotencyKeyLength = 128;

    public async Task<RegisterVisitResult> HandleAsync(
        RegisterVisitCommand command,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Normalising first means authorization compares the same canonical value that will be
        // persisted — " dover " and "DOVER" cannot resolve differently here and there.
        var terminal = TerminalCode.Create(command.TerminalId);

        if (!currentUser.CanAccessTerminal(terminal.Value))
        {
            throw new TerminalAccessDeniedException(terminal.Value);
        }

        var key = NormalizeIdempotencyKey(idempotencyKey);

        if (key is not null)
        {
            var existingId = await idempotencyStore
                .FindVisitIdAsync(key, currentUser.UserId, cancellationToken);

            if (existingId is { } visitId)
            {
                var existing = await repository.FindAsync(visitId, cancellationToken)
                    ?? throw new VisitNotFoundException(visitId);

                return new RegisterVisitResult(VisitMapper.ToDetail(existing), WasReplayed: true);
            }
        }

        var truck = Truck.Create(command.Truck?.UnitNumber, command.Truck?.LicensePlate);

        var driver = Driver.Create(
            command.Driver?.FullName,
            command.Driver?.DocumentId,
            command.Driver?.PhoneNumber);

        var movements = (command.Movements ?? [])
            .Select(input => Movement.Create(input.Type, input.UnitNumber, input.From, input.To))
            .ToArray();

        var visit = Visit.Register(
            terminal.Value,
            truck,
            driver,
            movements,
            // Never from the request body: the audit trail has to name the authenticated caller.
            createdBy: currentUser.UserId,
            createdTime: timeProvider.GetUtcNow());

        await repository.AddAsync(visit, cancellationToken);

        if (key is not null)
        {
            await idempotencyStore.RememberAsync(key, currentUser.UserId, visit.Id, cancellationToken);
        }

        // One SaveChanges for the visit, its history and the idempotency record: either the retry
        // guard and the data both land, or neither does.
        await repository.SaveChangesAsync(cancellationToken);

        return new RegisterVisitResult(VisitMapper.ToDetail(visit), WasReplayed: false);
    }

    private static string? NormalizeIdempotencyKey(string? raw)
    {
        var trimmed = raw?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > MaxIdempotencyKeyLength
            ? throw new RequestValidationException(
                "Idempotency-Key",
                $"Idempotency-Key cannot exceed {MaxIdempotencyKeyLength} characters.")
            : trimmed;
    }
}
