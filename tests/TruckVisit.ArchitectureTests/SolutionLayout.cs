using System.Xml.Linq;

namespace TruckVisit.ArchitectureTests;

/// <summary>
/// Locates the solution on disk so the architecture rules can be checked against the project files
/// themselves, not only against compiled metadata.
/// </summary>
/// <remarks>
/// Reading the .csproj matters because the compiler elides references an assembly does not
/// actually use. A project could declare a dependency on Entity Framework, never touch it, and
/// still look clean in assembly metadata — while every future developer sees the reference sitting
/// there as an invitation.
/// </remarks>
internal static class SolutionLayout
{
    public static readonly string Root = FindRoot();

    public static IReadOnlyList<string> PackageReferencesOf(string projectName)
    {
        var path = Path.Combine(Root, "src", projectName, $"{projectName}.csproj");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Project file not found for '{projectName}'.", path);
        }

        return [.. XDocument.Load(path)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Where(value => value.Length > 0)];
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("TruckVisit.sln").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate TruckVisit.sln above the test output directory.");
    }
}
