using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Execution;

/// <summary>A file a stage proposes to write.</summary>
/// <param name="RelativePath">Path within the workspace, using forward slashes.</param>
/// <param name="Content">The file's full contents.</param>
/// <remarks>
/// Agents describe changes; they do not perform them. The engine validates and applies every
/// write, which is what keeps "the agent may act in the run workspace" an enforceable
/// boundary rather than a description of what we hope it does.
/// </remarks>
public sealed record WorkspaceFile(string RelativePath, string Content)
{
    /// <summary>
    /// Rejects a path that would write outside the workspace.
    /// </summary>
    /// <remarks>
    /// Absolute paths, drive roots, parent traversal and paths into the workspace's own git
    /// directory are all refused. This runs on output the engine did not author, so it is a
    /// trust boundary and not a convenience check.
    /// </remarks>
    public static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalised = path.Replace('\\', '/').Trim();

        if (normalised.StartsWith('/') || normalised.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        string[] segments = normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return false;
        }

        foreach (string segment in segments)
        {
            if (segment is "." or ".." || string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A commit made in the workspace.</summary>
/// <param name="Sha">The commit's identifier.</param>
/// <param name="NodeId">The node whose output it records.</param>
/// <param name="Attempt">Which attempt produced it.</param>
/// <param name="FilesChanged">How many files the commit touched.</param>
public sealed record WorkspaceCommit(string Sha, NodeId NodeId, int Attempt, int FilesChanged);

/// <summary>The workspace's current condition.</summary>
/// <param name="HeadSha">The commit the tree currently sits at.</param>
/// <param name="IsClean">Whether the working tree has uncommitted changes.</param>
/// <param name="CommitCount">How many commits the workspace holds.</param>
public sealed record WorkspaceStatus(string HeadSha, bool IsClean, int CommitCount);

/// <summary>
/// The version-controlled tree a run's stages write into.
/// </summary>
/// <remarks>
/// <para>
/// Each node's output is its own commit, which is what makes a change reviewable per stage
/// and genuinely revertible. Compensation is a real revert of a real commit, so after it runs
/// the tree can be inspected and shown to match its prior state — rather than a log line
/// asserting that a rollback happened. See docs/adr/0006.
/// </para>
/// <para>
/// The engine is the only writer. Agents return the files they propose and the engine
/// validates the paths, applies them and commits.
/// </para>
/// </remarks>
public interface IRunWorkspace
{
    /// <summary>Absolute path to the working tree.</summary>
    string Root { get; }

    /// <summary>
    /// A read-only view of the tree, handed to stages so they can read what they are
    /// working on without being able to write it.
    /// </summary>
    IWorkspaceReader Reader { get; }

    /// <summary>Commits a node's proposed files as that node's contribution.</summary>
    /// <returns>The commit, or <see langword="null"/> when the node changed nothing.</returns>
    Task<WorkspaceCommit?> CommitAsync(
        NodeId nodeId,
        int attempt,
        IReadOnlyCollection<WorkspaceFile> files,
        string message,
        CancellationToken cancellationToken);

    /// <summary>Undoes a node's commits, newest first.</summary>
    /// <returns>How many commits were reverted.</returns>
    Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken);

    /// <summary>Discards any uncommitted changes, returning the tree to its last commit.</summary>
    Task DiscardUncommittedAsync(CancellationToken cancellationToken);

    /// <summary>Reports the tree's current condition.</summary>
    Task<WorkspaceStatus> StatusAsync(CancellationToken cancellationToken);

    /// <summary>Commits a node has made, oldest first.</summary>
    Task<ImmutableArray<WorkspaceCommit>> CommitsForAsync(
        NodeId nodeId, CancellationToken cancellationToken);
}

/// <summary>
/// Creates the workspace a run writes into.
/// </summary>
/// <remarks>
/// One workspace per run, created by the engine rather than handed to it, because the run's
/// identity determines where it lives and nothing outside the run should be writing there.
/// </remarks>
public interface IRunWorkspaceFactory
{
    /// <summary>Creates a workspace for a run.</summary>
    Task<IRunWorkspace> CreateAsync(RunId runId, CancellationToken cancellationToken);
}
