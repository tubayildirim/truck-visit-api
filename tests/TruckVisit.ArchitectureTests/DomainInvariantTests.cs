using System.Reflection;
using TruckVisit.Domain.Common;
using TruckVisit.Domain.Visits;
using Xunit;

namespace TruckVisit.ArchitectureTests;

/// <summary>
/// Checks the structural promises the domain model makes, rather than the behaviour it exhibits.
/// </summary>
/// <remarks>
/// The behavioural tests prove that today's code enforces the rules. These prove that tomorrow's
/// code cannot quietly stop enforcing them — by adding a public setter, exposing a mutable
/// collection, or making a value object inheritable. Each one of those would let a caller reach
/// around the aggregate without touching a single line that looks like a rule change.
/// </remarks>
public sealed class DomainInvariantTests
{
    private static readonly Assembly Domain = typeof(Visit).Assembly;

    [Fact]
    public void No_domain_entity_exposes_a_public_setter()
    {
        var offenders = new List<string>();

        foreach (var type in EntityTypes())
        {
            offenders.AddRange(type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.SetMethod?.IsPublic == true)
                .Select(property => $"{type.Name}.{property.Name}"));
        }

        // A public setter is a second way to change state, and the audit guarantee depends on
        // there being exactly one.
        Assert.True(
            offenders.Count == 0,
            $"Domain entities must only change through their own methods. Public setters found on: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void No_domain_type_exposes_a_mutable_collection()
    {
        var mutable = new[] { typeof(List<>), typeof(ICollection<>), typeof(IList<>), typeof(HashSet<>) };

        var offenders = new List<string>();

        foreach (var type in EntityTypes())
        {
            offenders.AddRange(type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType.IsGenericType)
                .Where(property => mutable.Contains(property.PropertyType.GetGenericTypeDefinition()))
                .Select(property => $"{type.Name}.{property.Name}"));
        }

        // Returning IReadOnlyList over a List is not enough on its own — the caller can cast it
        // back. The aggregate hands out AsReadOnly() views; this catches the day someone stops.
        Assert.True(
            offenders.Count == 0,
            $"Domain collections must be exposed read-only. Mutable collections found on: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Every_value_object_is_sealed()
    {
        var offenders = Domain.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.IsAssignableTo(typeof(NormalizedCode)))
            .Where(type => !type.IsSealed)
            .Select(type => type.Name)
            .ToArray();

        // An unsealed value object can be subclassed into something that overrides equality, and
        // two codes that compare equal in one place and not another is a bug nobody finds quickly.
        Assert.True(
            offenders.Length == 0,
            $"Value objects must be sealed: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Every_domain_failure_derives_from_the_shared_base_type()
    {
        // The API maps DomainException subtypes onto status codes in one place. An exception type
        // that sits outside that hierarchy would escape the mapping and surface as a 500.
        var offenders = Domain.GetTypes()
            .Where(type => type.IsAssignableTo(typeof(Exception)))
            .Where(type => !type.IsAssignableTo(typeof(DomainException)))
            .Select(type => type.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"Domain exceptions must derive from DomainException: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void The_audit_entry_type_has_no_public_way_to_change_itself()
    {
        // Stated separately from the sweep above because this is the one the whole feature rests
        // on, and a reviewer should be able to find it by name.
        var mutators = typeof(StatusChange)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .OfType<MethodInfo>()
            .Where(method => !method.IsSpecialName)
            .Where(method => method.DeclaringType == typeof(StatusChange))
            .Where(method => method.ReturnType == typeof(void))
            .Select(method => method.Name)
            .ToArray();

        Assert.True(
            mutators.Length == 0,
            $"StatusChange must expose no mutating members, but has: {string.Join(", ", mutators)}");
    }

    private static IEnumerable<Type> EntityTypes() =>
        Domain.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract)
            .Where(type => type.Namespace?.StartsWith("TruckVisit.Domain.Visits", StringComparison.Ordinal) == true)
            .Where(type => !type.Name.StartsWith('<'));
}
