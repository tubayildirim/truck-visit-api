using System.Reflection;
using TruckVisit.Application.Visits;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.ArchitectureTests;

/// <summary>
/// Turns the architecture document's central claim into something the build can check.
/// </summary>
/// <remarks>
/// <para>
/// Every codebase's README says it follows clean architecture. Six months and forty pull requests
/// later, the domain references the ORM "just for this one query" and nobody remembers agreeing to
/// it. A rule that lives only in a document is a rule that erodes; a rule that fails the build is
/// a rule that holds.
/// </para>
/// <para>
/// These tests are the standing guard a Lead leaves behind for a team, which is why they are worth
/// more than the fifteen minutes they took to write.
/// </para>
/// </remarks>
public sealed class LayerBoundaryTests
{
    private static readonly Assembly Domain = typeof(Visit).Assembly;
    private static readonly Assembly Application = typeof(RegisterVisitHandler).Assembly;

    private static readonly string[] FrameworkPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "Microsoft.Extensions.DependencyInjection",
    ];

    [Fact]
    public void The_domain_project_declares_no_package_references()
    {
        // ADR-002. A package reference here means a business rule has acquired a dependency on a
        // framework, which is the first step towards the rule living inside the framework.
        var packages = SolutionLayout.PackageReferencesOf("TruckVisit.Domain");

        Assert.True(
            packages.Count == 0,
            $"TruckVisit.Domain must stay free of NuGet packages, but declares: {string.Join(", ", packages)}");
    }

    [Fact]
    public void The_application_project_declares_no_package_references()
    {
        // The same rule, and the reason there is no mediator, mapper or validation library in this
        // solution (ADR-009). Adding one would fail here, which is the point: the decision gets
        // re-argued rather than made silently in a pull request.
        var packages = SolutionLayout.PackageReferencesOf("TruckVisit.Application");

        Assert.True(
            packages.Count == 0,
            $"TruckVisit.Application must stay free of NuGet packages, but declares: {string.Join(", ", packages)}");
    }

    [Fact]
    public void The_domain_depends_on_nothing_inside_this_solution()
    {
        var offenders = SolutionReferencesOf(Domain);

        Assert.True(
            offenders.Count == 0,
            $"The domain must be the innermost layer, but references: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void The_application_depends_on_the_domain_and_nothing_else_here()
    {
        var offenders = SolutionReferencesOf(Application)
            .Where(name => name != "TruckVisit.Domain")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"The application layer may only reach inwards, but references: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("TruckVisit.Domain")]
    [InlineData("TruckVisit.Application")]
    public void The_inner_layers_know_nothing_about_infrastructure_frameworks(string layer)
    {
        var assembly = layer == "TruckVisit.Domain" ? Domain : Application;

        var offenders = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => FrameworkPrefixes.Any(
                prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{layer} must not reference infrastructure frameworks, but references: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Persistence_details_do_not_leak_out_of_the_infrastructure_layer()
    {
        // The repository and the DbContext configuration are internal by design. If one became
        // public, the API layer could start talking to the database directly and the port would
        // stop being the only way in.
        var leaked = typeof(Infrastructure.InfrastructureServiceCollectionExtensions).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.Contains(".Persistence", StringComparison.Ordinal) == true)
            .Where(type => type.Name.EndsWith("Repository", StringComparison.Ordinal)
                || type.Name.EndsWith("Configuration", StringComparison.Ordinal)
                || type.Name.EndsWith("Store", StringComparison.Ordinal))
            .Select(type => type.Name)
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            $"These persistence types should be internal: {string.Join(", ", leaked)}");
    }

    private static IReadOnlyList<string> SolutionReferencesOf(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => name.StartsWith("TruckVisit.", StringComparison.Ordinal))
            .Where(name => name != assembly.GetName().Name)];
}
