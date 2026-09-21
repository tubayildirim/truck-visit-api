using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TruckVisit.Infrastructure.Persistence;
using Xunit;

namespace TruckVisit.Api.IntegrationTests;

/// <summary>
/// Provides a real PostgreSQL instance for the integration suite.
/// </summary>
/// <remarks>
/// <para>
/// A real database, not an in-memory provider. The guarantees these tests exist to prove —
/// the append-only trigger on the audit table, the unique index on the history sequence, the
/// optimistic concurrency token — are properties of PostgreSQL. An in-memory substitute would
/// report success without any of them being present, which is worse than having no test.
/// </para>
/// <para>
/// Two ways to get one. <c>TRUCKVISIT_TEST_DB</c> points at an existing database (a CI service
/// container, or a local install); otherwise Testcontainers starts a throwaway one. If neither is
/// available the suite skips with an explanation rather than failing, so a machine without Docker
/// still gets a green unit-test run instead of noise it cannot act on.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "TRUCKVISIT_TEST_DB";

    private PostgreSqlContainer? _container;

    /// <summary>Connection string, or <c>null</c> when no database could be provisioned.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Why the suite is skipping, or <c>null</c> when it is not.</summary>
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            ConnectionString = configured;
        }
        else
        {
            try
            {
                // The image is pinned in the constructor rather than by WithImage: the parameterless
                // overload is obsolete, and an unpinned image would let a test suite change
                // behaviour without a single line of code changing.
                _container = new PostgreSqlBuilder("postgres:17-alpine")
                    .WithDatabase("truckvisit")
                    .WithUsername("truckvisit")
                    .WithPassword("truckvisit")
                    .Build();

                await _container.StartAsync();
                ConnectionString = _container.GetConnectionString();
            }
            catch (Exception exception)
            {
                SkipReason =
                    $"No PostgreSQL available: Testcontainers could not start one ({exception.GetType().Name}: "
                    + $"{exception.Message}). Start Docker, or set {EnvironmentVariable} to a connection string.";
                return;
            }
        }

        try
        {
            // Migrations, not EnsureCreated. EnsureCreated builds the schema from the model and
            // silently skips everything the migrations add by raw SQL — including the trigger that
            // makes the audit table append-only, which is exactly what is under test here.
            await using var context = CreateContext();
            await context.Database.MigrateAsync();
        }
        catch (Exception exception)
        {
            SkipReason = $"Database reachable but migrations failed: {exception.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public TruckVisitDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TruckVisitDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        return new TruckVisitDbContext(options);
    }

    /// <summary>Skips the calling test when no database was available.</summary>
    public void SkipIfUnavailable()
    {
        if (SkipReason is not null)
        {
            Assert.Skip(SkipReason);
        }
    }
}

/// <summary>
/// Marker type that binds every test class in the collection to one shared database, so the
/// container starts once for the whole suite rather than once per class.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresDatabase : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
