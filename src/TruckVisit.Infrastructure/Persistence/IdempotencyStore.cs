using Microsoft.EntityFrameworkCore;
using TruckVisit.Application.Abstractions;

namespace TruckVisit.Infrastructure.Persistence;

/// <summary>EF Core implementation of the idempotency port.</summary>
internal sealed class IdempotencyStore(TruckVisitDbContext context, TimeProvider timeProvider)
    : IIdempotencyStore
{
    public async Task<Guid?> FindVisitIdAsync(
        string key,
        string userId,
        CancellationToken cancellationToken)
    {
        var record = await context.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(
                entry => entry.Key == key && entry.UserId == userId,
                cancellationToken);

        return record?.VisitId;
    }

    public Task RememberAsync(
        string key,
        string userId,
        Guid visitId,
        CancellationToken cancellationToken)
    {
        // Staged, not saved: the caller commits this together with the visit, so a crash between
        // the two cannot leave a key pointing at a visit that was never written.
        context.IdempotencyRecords.Add(
            new IdempotencyRecord(key, userId, visitId, timeProvider.GetUtcNow()));

        return Task.CompletedTask;
    }
}
