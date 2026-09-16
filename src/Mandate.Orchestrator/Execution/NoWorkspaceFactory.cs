using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;

namespace Mandate.Orchestrator.Execution;

/// <summary>
/// The workspace used when a run has no tree to write into.
/// </summary>
/// <remarks>
/// Not a silent no-op: a stage that proposes files while no workspace is configured is a
/// configuration mistake, and pretending to accept the write would leave the run claiming
/// output that does not exist anywhere. Stages that write nothing are unaffected.
/// </remarks>
internal sealed class NoWorkspaceFactory : IRunWorkspaceFactory
{
    public static NoWorkspaceFactory Instance { get; } = new();

    private NoWorkspaceFactory()
    {
    }

    public Task<IRunWorkspace> CreateAsync(RunId runId, CancellationToken cancellationToken) =>
        Task.FromResult<IRunWorkspace>(new AbsentWorkspace());

    private sealed class AbsentWorkspace : IRunWorkspace
    {
        public string Root => string.Empty;

        public Task<WorkspaceCommit?> CommitAsync(
            NodeId nodeId,
            int attempt,
            IReadOnlyCollection<WorkspaceFile> files,
            string message,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(files);

            if (files.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Node '{nodeId}' proposed {files.Count} file(s), but this run has no "
                    + "workspace configured. Accepting the write silently would leave the run "
                    + "claiming output that exists nowhere.");
            }

            return Task.FromResult<WorkspaceCommit?>(null);
        }

        public Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<WorkspaceStatus> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceStatus(string.Empty, IsClean: true, CommitCount: 0));

        public Task<ImmutableArray<WorkspaceCommit>> CommitsForAsync(
            NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<WorkspaceCommit>.Empty);
    }
}
