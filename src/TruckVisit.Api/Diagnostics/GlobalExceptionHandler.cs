using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TruckVisit.Application.Abstractions;
using TruckVisit.Domain.Common;

namespace TruckVisit.Api.Diagnostics;

/// <summary>
/// Translates every exception the application can raise into an RFC 9457 problem response.
/// </summary>
/// <remarks>
/// <para>
/// Endpoints contain no try/catch. Failure modes are expressed as exception types in the layer
/// that understands them, and the mapping from type to status code lives here, once — so the same
/// rule violation cannot come back as 400 from one endpoint and 500 from another.
/// </para>
/// <para>
/// Known failures are logged at information level with no stack trace: a rejected status
/// transition is the system working, not an incident, and logging it as an error would bury the
/// real ones. Anything unrecognised is logged in full and answered with a body that says nothing
/// about the internals.
/// </para>
/// </remarks>
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var correlationId = CorrelationIdMiddleware.GetCorrelationId(httpContext);
        var method = httpContext.Request.Method;
        var path = httpContext.Request.Path.Value ?? string.Empty;

        var problem = Map(exception, correlationId);

        if (problem is null)
        {
            ExceptionLog.Unhandled(logger, exception, method, path, correlationId);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                // No detail on purpose. An exception message can name a table, a column, a host
                // or a connection string, and this body goes to the client.
                Detail = "The request could not be completed. "
                    + "Quote the correlation id below when reporting this.",
            };

            problem.Extensions["correlationId"] = correlationId;
        }
        else
        {
            ExceptionLog.Rejected(logger, exception.Message, method, path, correlationId);
        }

        httpContext.Response.StatusCode =
            problem.Status ?? StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails? Map(Exception exception, string correlationId)
    {
        ProblemDetails problem;

        switch (exception)
        {
            // A value cannot form a valid domain concept: blank plate, oversized unit number.
            case DomainValidationException domainValidation:
                problem = Build(
                    StatusCodes.Status400BadRequest,
                    "The request could not be validated.",
                    domainValidation.Message);
                problem.Extensions["field"] = domainValidation.Field;
                break;

            // A request argument is unusable before the domain is reached: bad page size.
            case RequestValidationException requestValidation:
                problem = Build(
                    StatusCodes.Status400BadRequest,
                    "The request could not be validated.",
                    requestValidation.Message);
                problem.Extensions["field"] = requestValidation.Field;
                break;

            // Well-formed request, incompatible resource state.
            case InvalidStatusTransitionException transition:
                problem = Build(
                    StatusCodes.Status409Conflict,
                    "The visit is not in a state that allows this change.",
                    transition.Message);
                problem.Extensions["from"] = transition.From;
                problem.Extensions["to"] = transition.To;
                break;

            // Someone else changed the visit first.
            case ConcurrencyConflictException concurrency:
                problem = Build(
                    StatusCodes.Status409Conflict,
                    "The visit was modified by another request.",
                    concurrency.Message);
                problem.Extensions["visitId"] = concurrency.VisitId;
                break;

            // Authenticated, but holds no claim for this terminal.
            case TerminalAccessDeniedException accessDenied:
                problem = Build(
                    StatusCodes.Status403Forbidden,
                    "Access to this terminal is not permitted.",
                    accessDenied.Message);
                break;

            // Absent, or at a terminal the caller cannot see. Both answer 404 by design — see
            // VisitNotFoundException.
            case VisitNotFoundException notFound:
                problem = Build(
                    StatusCodes.Status404NotFound,
                    "The visit was not found.",
                    notFound.Message);
                break;

            case BadHttpRequestException badRequest:
                problem = Build(
                    StatusCodes.Status400BadRequest,
                    "The request could not be read.",
                    badRequest.Message);
                break;

            default:
                return null;
        }

        problem.Extensions["correlationId"] = correlationId;

        return problem;
    }

    private static ProblemDetails Build(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };
}

/// <summary>
/// Source-generated log methods. Using the generator rather than interpolated strings keeps the
/// message template intact for structured log queries and avoids boxing on every call.
/// </summary>
internal static partial class ExceptionLog
{
    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Unhandled exception handling {Method} {Path} [correlation {CorrelationId}]")]
    public static partial void Unhandled(
        ILogger logger,
        Exception exception,
        string method,
        string path,
        string correlationId);

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Request rejected handling {Method} {Path}: {Reason} [correlation {CorrelationId}]")]
    public static partial void Rejected(
        ILogger logger,
        string reason,
        string method,
        string path,
        string correlationId);
}
