namespace TruckVisit.Application.Abstractions;

/// <summary>
/// A page of results plus the metadata the case asks the search endpoint to return.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount)
{
    /// <summary>Total number of pages available for the current page size.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPreviousPage => Page > 1;

    public bool HasNextPage => Page < TotalPages;
}

/// <summary>
/// Bounds applied to every paged query.
/// </summary>
/// <remarks>
/// <see cref="MaxPageSize"/> is a availability control, not a preference: without it a single
/// caller can ask for a million rows and take the API down for the gate operators who share it.
/// </remarks>
public static class PagingDefaults
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;
    public const int FirstPage = 1;
}
