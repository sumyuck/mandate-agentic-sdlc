namespace Mandate.Workflows.Tests.Support;

/// <summary>Locates repository files from a test assembly's output directory.</summary>
internal static class Repository
{
    private const string SolutionFileName = "Mandate.sln";

    public static DirectoryInfo Root { get; } = FindRoot();

    public static string ShippedWorkflow => Path.Combine(Root.FullName, "workflows", "sdlc.v1.yaml");

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
