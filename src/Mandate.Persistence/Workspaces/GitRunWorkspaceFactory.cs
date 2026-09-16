using Mandate.Core.Execution;
using Mandate.Core.Identifiers;

namespace Mandate.Persistence.Workspaces;

/// <summary>
/// Creates a git-backed workspace per run, under a shared root.
/// </summary>
/// <remarks>
/// One directory per run, named by run id, so two runs can never write over each other and a
/// run's tree can be found again afterwards from its id alone.
/// </remarks>
public sealed class GitRunWorkspaceFactory(string root, string? templatePath)
    : IRunWorkspaceFactory
{
    /// <summary>Where run workspaces live by default.</summary>
    public const string DefaultRoot = ".mandate/workspaces";

    /// <summary>The tree new workspaces are seeded from by default.</summary>
    public const string DefaultTemplate = "templates/service";

    /// <inheritdoc />
    /// <remarks>
    /// An existing workspace is reopened rather than recreated. A resumed run must continue
    /// in the tree its earlier stages wrote into — seeding a fresh one would silently discard
    /// their work while the audit log went on claiming it existed.
    /// </remarks>
    public async Task<IRunWorkspace> CreateAsync(
        RunId runId, CancellationToken cancellationToken)
    {
        string path = Path.Combine(root, runId.Value);

        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            return GitRunWorkspace.Open(path, runId);
        }

        return await GitRunWorkspace
            .CreateAsync(path, runId, templatePath, cancellationToken)
            .ConfigureAwait(false);
    }
}
