using Mandate.Persistence.Workspaces;

namespace Mandate.Persistence.Tests;

/// <summary>Reads git metadata directly, to confirm what the workspace actually wrote.</summary>
internal static class GitCommandProbe
{
    public static Task<string> RunAsync(
        string workingDirectory, string[] arguments, CancellationToken cancellationToken) =>
        GitCommand.RunAsync(workingDirectory, arguments, cancellationToken);
}
