using Microsoft.AspNetCore.Mvc;
using TruckVisit.Api.Diagnostics;
using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Visits;

namespace TruckVisit.Api.Endpoints;

/// <summary>Body of a status change request.</summary>
/// <param name="Status">The status to move to.</param>
/// <param name="Reason">Optional operator note, e.g. why a truck was held at the gate.</param>
public sealed record ChangeVisitStatusRequest(VisitStatus Status, string? Reason);

/// <summary>
/// HTTP surface for the visits module.
/// </summary>
/// <remarks>
/// The endpoints are thin on purpose: bind, delegate, shape the response. There is no try/catch
/// and no validation here — failures travel as exceptions to <see cref="GlobalExceptionHandler"/>,
/// which owns the single mapping from failure type to status code. That is what keeps the same
/// rule violation from returning 400 on one route and 500 on another.
/// </remarks>
internal static class VisitEndpoints
{
    public static IEndpointRouteBuilder MapVisitEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes
            .MapGroup("/api/visits")
            .WithTags("Visits")
            .RequireAuthorization();

        group.MapPost("/", RegisterVisitAsync)
            .WithName("RegisterVisit")
            .WithSummary("Registers a truck visit.")
            .Produces<VisitDetailView>(StatusCodes.Status201Created)
            .Produces<VisitDetailView>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:guid}", GetVisitAsync)
            .WithName("GetVisit")
            .WithSummary("Returns a visit and its full audit trail.")
            .Produces<VisitDetailView>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/", SearchVisitsAsync)
            .WithName("SearchVisits")
            .WithSummary("Searches visits with filtering and pagination.")
            .Produces<PagedResult<VisitSummaryView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPatch("/{id:guid}/status", ChangeStatusAsync)
            .WithName("ChangeVisitStatus")
            .WithSummary("Advances a visit to the next lifecycle status.")
            .Produces<VisitDetailView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> RegisterVisitAsync(
        RegisterVisitCommand command,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        RegisterVisitHandler handler,
        VisitMetrics metrics,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(command, idempotencyKey, cancellationToken);

        if (result.WasReplayed)
        {
            // 200, not 201: a retry did not create anything. A client that keys off the status
            // code to decide "did my request land?" gets an honest answer either way.
            return TypedResults.Ok(result.Visit);
        }

        metrics.VisitRegistered(result.Visit.TerminalId);

        return TypedResults.Created($"/api/visits/{result.Visit.Id}", result.Visit);
    }

    private static async Task<IResult> GetVisitAsync(
        Guid id,
        GetVisitByIdHandler handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(id, cancellationToken));

    private static async Task<IResult> SearchVisitsAsync(
        string? terminalId,
        VisitStatus? currentStatus,
        string? movementFrom,
        string? movementTo,
        DateTimeOffset? createdTimeFrom,
        DateTimeOffset? createdTimeTo,
        string? createdBy,
        int? page,
        int? pageSize,
        SearchVisitsHandler handler,
        CancellationToken cancellationToken)
    {
        var query = new SearchVisitsQuery(
            terminalId,
            currentStatus,
            movementFrom,
            movementTo,
            createdTimeFrom,
            createdTimeTo,
            createdBy,
            page,
            pageSize);

        return TypedResults.Ok(await handler.HandleAsync(query, cancellationToken));
    }

    private static async Task<IResult> ChangeStatusAsync(
        Guid id,
        ChangeVisitStatusRequest request,
        ChangeVisitStatusHandler handler,
        VisitMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var visit = await handler.HandleAsync(
                new ChangeVisitStatusCommand(id, request.Status, request.Reason),
                cancellationToken);

            metrics.StatusChanged(visit.TerminalId, visit.CurrentStatus);

            return TypedResults.Ok(visit);
        }
        catch (Domain.Common.InvalidStatusTransitionException exception)
        {
            // The only catch in the HTTP layer, and it does not swallow anything: the counter is
            // recorded and the exception continues to the global handler, which still answers 409.
            // A spike here is how an out-of-step gate device gets noticed.
            metrics.TransitionRejected(exception.From, exception.To);
            throw;
        }
    }
}
