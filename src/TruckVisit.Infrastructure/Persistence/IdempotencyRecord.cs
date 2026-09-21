namespace TruckVisit.Infrastructure.Persistence;

/// <summary>
/// Remembers that a given <c>Idempotency-Key</c>, from a given caller, already produced a visit.
/// </summary>
/// <remarks>
/// Scoped by user as well as by key: two clients must never be able to collide on, or read each
/// other's, keys just by choosing the same string.
/// </remarks>
internal sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
        Key = null!;
        UserId = null!;
    }

    public IdempotencyRecord(string key, string userId, Guid visitId, DateTimeOffset createdAt)
    {
        Key = key;
        UserId = userId;
        VisitId = visitId;
        CreatedAt = createdAt;
    }

    public string Key { get; private set; }

    public string UserId { get; private set; }

    public Guid VisitId { get; private set; }

    /// <summary>Used by the retention job that prunes keys once retries are no longer plausible.</summary>
    public DateTimeOffset CreatedAt { get; private set; }
}
