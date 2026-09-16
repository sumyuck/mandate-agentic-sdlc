using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Tests.Support;

/// <summary>An agent that throws, to prove an exception is contained as a stage failure.</summary>
internal sealed class ThrowingStageAgent : IStageAgent
{
    public const string AgentId = "throwing-agent";

    public string Id => AgentId;

    public Task<StageResult> ExecuteAsync(
        StageExecution execution, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("the agent blew up");
}
