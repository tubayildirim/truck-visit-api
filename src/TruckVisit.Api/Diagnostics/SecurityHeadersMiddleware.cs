namespace TruckVisit.Api.Diagnostics;

/// <summary>
/// Applies the response headers required by the security acceptance criteria.
/// </summary>
/// <remarks>
/// This service returns JSON to machines, so the browser-oriented headers are deliberately strict:
/// nothing here is ever meant to be rendered, framed, or sniffed. Strict-Transport-Security is not
/// set here — it belongs to <c>UseHsts</c>, which correctly omits it outside HTTPS.
/// </remarks>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            // Stop a browser second-guessing the declared content type.
            headers["X-Content-Type-Options"] = "nosniff";

            // No page here should ever be embedded.
            headers["X-Frame-Options"] = "DENY";

            // Do not leak API paths, which contain identifiers, to third-party sites.
            headers["Referrer-Policy"] = "no-referrer";

            // An API response has no scripts, styles or frames of its own to permit.
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            // Deny device permissions outright; nothing served here needs them.
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

            // The server version is not information a client needs.
            headers.Remove("Server");

            return Task.CompletedTask;
        });

        return next(context);
    }
}
