namespace Mandate.Agents.Tests.Support;

/// <summary>Locates the repository root from the test assembly's output directory.</summary>
internal static class RepositoryRoot
{
    private const string SolutionFileName = "Mandate.sln";

    public static string Path { get; } = Find();

    private static string Find()
    {
        DirectoryInfo? candidate = new(AppContext.BaseDirectory);

        while (candidate is not null)
        {
            if (File.Exists(System.IO.Path.Combine(candidate.FullName, SolutionFileName)))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate {SolutionFileName} above {AppContext.BaseDirectory}.");
    }
}
