using System.Collections.Immutable;

namespace Mandate.Core.Execution;

/// <summary>
/// A stage's read-only view of the run workspace.
/// </summary>
/// <remarks>
/// <para>
/// A reviewer cannot review code it has never seen, and an implementer working on existing
/// code has to read that code first. Artifacts carry a digest and provenance, not content,
/// so they cannot serve as the channel — and duplicating every document into a second store
/// would give the run two sources of truth that could disagree. The workspace is the tree
/// the run is actually building; this is a window onto it.
/// </para>
/// <para>
/// Read-only on purpose, and it is the whole of the asymmetry in ADR-0009: a stage may look
/// at anything in the tree and may <em>propose</em> changes to it, but only the engine
/// writes. Handing agents an <see cref="IRunWorkspace"/> would make "the agent may act in
/// the workspace" a description of intent rather than a boundary anything enforces.
/// </para>
/// </remarks>
public interface IWorkspaceReader
{
    /// <summary>
    /// Every file in the tree, as workspace-relative paths with forward slashes, sorted.
    /// </summary>
    /// <remarks>
    /// Git's own directory is excluded. A stage has no business reading the object store,
    /// and a prompt built from it would be noise measured in megabytes.
    /// </remarks>
    ImmutableArray<string> Files { get; }

    /// <summary>
    /// A file's contents, or <see langword="null"/> when it is absent or unreadable as text.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing for a missing file: a stage asking whether something exists
    /// yet is normal, and an exception would turn a question into a failure.
    /// </remarks>
    string? TryRead(string relativePath);

    /// <summary>An empty tree, for stages that run before anything has been written.</summary>
    public static IWorkspaceReader Empty { get; } = new EmptyWorkspaceReader();

    private sealed class EmptyWorkspaceReader : IWorkspaceReader
    {
        public ImmutableArray<string> Files => [];

        public string? TryRead(string relativePath) => null;
    }
}
