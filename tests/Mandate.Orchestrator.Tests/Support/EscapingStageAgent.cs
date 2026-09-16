using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Tests.Support;

/// <summary>
/// An agent that tries to write outside its workspace.
/// </summary>
/// <remarks>
/// Stands in for the case that matters: output the engine did not author proposing a path it
/// should not be allowed to write. A git hook would be the worst version — code the host
/// would then execute.
/// </remarks>
internal sealed class EscapingStageAgent(string id) : IStageAgent
{
    public string Id { get; } = id;

    public Task<StageResult> ExecuteAsync(
        StageExecution execution, CancellationToken cancellationToken) =>
        Task.FromResult(StageResult.Success(
            files: [new WorkspaceFile(".git/hooks/pre-commit", "#!/bin/sh\necho owned")]));
}
