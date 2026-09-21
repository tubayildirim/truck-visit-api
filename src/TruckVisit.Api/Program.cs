using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using TruckVisit.Api.Diagnostics;
using TruckVisit.Api.Endpoints;
using TruckVisit.Api.Security;
using TruckVisit.Application.Abstractions;
using TruckVisit.Application.Visits;
using TruckVisit.Infrastructure;
using TruckVisit.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------------------------
// Configuration
//
// No secret is committed. The connection string arrives from an environment variable
// (ConnectionStrings__TruckVisit) in every deployed environment, and from user-secrets or the
// local docker-compose defaults during development. Missing configuration fails at start-up
// rather than on the first request that touches the database.
// ---------------------------------------------------------------------------------------------
var connectionString = builder.Configuration.GetConnectionString("TruckVisit")
    ?? throw new InvalidOperationException(
        "Connection string 'TruckVisit' is not configured. Set ConnectionStrings__TruckVisit "
        + "in the environment, or run 'dotnet user-secrets set' for local development.");

// ---------------------------------------------------------------------------------------------
// Logging: JSON to stdout, with scopes.
//
// Structured from the start, because these logs are read by a query engine and not by a person
// scrolling a console. Scopes carry the correlation id onto every line written during a request.
// ---------------------------------------------------------------------------------------------
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

// ---------------------------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------------------------
builder.Services.AddTruckVisitInfrastructure(connectionString);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();

// Use cases are plain scoped classes — no mediator to register, and no runtime scan to explain.
builder.Services.AddScoped<RegisterVisitHandler>();
builder.Services.AddScoped<GetVisitByIdHandler>();
builder.Services.AddScoped<SearchVisitsHandler>();
builder.Services.AddScoped<ChangeVisitStatusHandler>();

builder.Services.AddMetrics();
builder.Services.AddSingleton<VisitMetrics>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddOpenApi();

// Authentication settings bind from "Authentication:Schemes:Bearer", which is also where
// `dotnet user-jwts` writes. That keeps local development working with real, signed tokens and
// no developer-only bypass in the pipeline — the shortcut that quietly ships to production.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep 'sub' as 'sub' instead of remapping it to a long WS-Federation URI. The audit trail
        // records this value, so it should be the claim the token actually carries.
        options.MapInboundClaims = false;
    });

builder.Services.AddAuthorizationBuilder()
    // Every endpoint requires authentication unless it opts out explicitly. A new route added
    // later is therefore protected by default rather than by the author remembering.
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // Behind an ingress or ALB, without this the client IP in the logs is the proxy's and the
    // scheme always reads as http, which breaks both auditing and the HTTPS redirect.
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

// ---------------------------------------------------------------------------------------------
// Pipeline
// ---------------------------------------------------------------------------------------------
app.UseForwardedHeaders();

// First, so that everything downstream — including the error response — can quote the id.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
    // TLS terminates at the ingress in a cluster; these two make the requirement explicit at the
    // application edge as well, so a misrouted plaintext request is refused rather than served.
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Development only. Production applies migrations as a separate, reviewable step in the
    // deployment pipeline: an application that migrates its own schema at start-up will race
    // itself the moment it runs with more than one replica, which this one always does.
    await using var scope = app.Services.CreateAsyncScope();
    var database = scope.ServiceProvider.GetRequiredService<TruckVisitDbContext>();
    await database.Database.MigrateAsync();
}

// Liveness: is the process up? Deliberately runs no checks — see DatabaseHealthCheck.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous();

// Readiness: should this instance receive traffic?
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.MapVisitEndpoints();

await app.RunAsync();

/// <summary>
/// Exposed so the integration tests can drive the real pipeline through WebApplicationFactory
/// rather than testing a differently-configured copy of it.
/// </summary>
public partial class Program;
