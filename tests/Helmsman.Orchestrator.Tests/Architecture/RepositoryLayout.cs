namespace Helmsman.Orchestrator.Tests.Architecture;

/// <summary>
/// Locates the repository root from a test assembly's output directory.
/// </summary>
internal static class RepositoryLayout
{
    private const string SolutionFileName = "Helmsman.sln";

    public static DirectoryInfo Root { get; } = FindRoot();

    public static string ReadProject(string relativePath) =>
        File.ReadAllText(Path.Combine(Root.FullName, relativePath));

    public static IReadOnlyList<string> ProjectFiles(params string[] relativeDirectories) =>
        relativeDirectories
            .Select(directory => new DirectoryInfo(Path.Combine(Root.FullName, directory)))
            .Where(directory => directory.Exists)
            .SelectMany(directory => directory.GetFiles("*.csproj", SearchOption.AllDirectories))
            .Select(file => Path.GetRelativePath(Root.FullName, file.FullName))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static DirectoryInfo FindRoot()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);

        while (candidate is not null)
        {
            if (File.Exists(Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {SolutionFileName} above {AppContext.BaseDirectory}.");
    }
}
