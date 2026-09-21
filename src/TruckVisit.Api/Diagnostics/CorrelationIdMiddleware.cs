using System.Diagnostics;

namespace TruckVisit.Api.Diagnostics;

/// <summary>
/// Establishes a correlation identifier for every request and makes it visible to logs, to the
/// caller, and to anything downstream.
/// </summary>
/// <remarks>
/// A gate operator reporting "the barrier did not open at 07:42" is not going to quote a trace id.
/// The client's own correlation header is therefore honoured when present, so a problem can be
/// followed from the device that saw it all the way through this service's logs. When no header is
/// supplied we fall back to the trace id the platform already generates, rather than inventing a
/// second identifier that has to be reconciled with it later.
/// </remarks>
internal sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";

    private const int MaxLength = 128;

    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = ResolveCorrelationId(context);

        context.Items[HeaderName] = correlationId;
        Activity.Current?.SetBaggage("correlation.id", correlationId);

        // Echoed on the way out, including on error responses, so the caller can quote it.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        // Every log entry written while this scope is open carries the identifier.
        using (logger.BeginScope(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["CorrelationId"] = correlationId,
            ["TraceId"] = context.TraceIdentifier,
        }))
        {
            await next(context);
        }
    }

    internal static string GetCorrelationId(HttpContext context) =>
        context.Items.TryGetValue(HeaderName, out var value) && value is string correlationId
            ? correlationId
            : context.TraceIdentifier;

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var supplied))
        {
            return context.TraceIdentifier;
        }

        var candidate = supplied.ToString();

        // The value is echoed back in a response header, so it is bounded and stripped of control
        // characters before it is trusted. An unbounded client-supplied header is a header
        // injection waiting to happen.
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxLength)
        {
            return context.TraceIdentifier;
        }

        foreach (var character in candidate)
        {
            if (char.IsControl(character))
            {
                return context.TraceIdentifier;
            }
        }

        return candidate;
    }
}
