namespace TruckVisit.Domain.Common;

/// <summary>
/// Base type for every rule violation raised by the domain model.
/// The API layer translates these into RFC 9457 problem responses; nothing else catches them.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A supplied value cannot form a valid domain concept (empty unit number, oversized plate, ...).
/// Maps to HTTP 400.
/// </summary>
public sealed class DomainValidationException : DomainException
{
    public DomainValidationException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    /// <summary>Name of the offending concept, surfaced as the problem-details key.</summary>
    public string Field { get; }
}

/// <summary>
/// The requested state change is not reachable from the visit's current state.
/// Maps to HTTP 409 — the request was well formed, the resource was not in a compatible state.
/// </summary>
public sealed class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(string message, string from, string to)
        : base(message)
    {
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }
}
