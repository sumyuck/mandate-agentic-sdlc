using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Decisions;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;

namespace Mandate.Core.Execution;

/// <summary>
/// What a stage agent is given when it runs.
/// </summary>
/// <remarks>
/// The context is a <em>scoped</em> view, not the whole run's. A stage that cannot see
/// unrelated facts cannot develop a hidden dependency on them, and a stage whose prompt is
/// assembled from context cannot leak information it was never entitled to.
/// </remarks>
/// <param name="RunId">The run being executed.</param>
/// <param name="Node">The node this agent is executing.</param>
/// <param name="Attempt">Which attempt this is, counting from 1.</param>
/// <param name="Context">The context the stage is entitled to read.</param>
/// <param name="Inputs">Artifacts produced upstream that this stage may build on.</param>
/// <param name="Actor">The identity this execution acts under, recorded on everything it produces.</param>
public sealed record StageExecution(
    RunId RunId,
    WorkflowNode Node,
    int Attempt,
    RunContext Context,
    ImmutableArray<Artifact> Inputs,
    Actor Actor);

/// <summary>What a stage agent produced.</summary>
/// <param name="Succeeded">Whether the stage completed its work.</param>
/// <param name="Artifacts">Artifacts produced, with provenance.</param>
/// <param name="Facts">Context facts contributed for later stages and for guards.</param>
/// <param name="Decisions">Choices made, with the options rejected and the rationale.</param>
/// <param name="Failure">Why the stage did not complete, when it did not.</param>
public sealed record StageResult(
    bool Succeeded,
    ImmutableArray<Artifact> Artifacts,
    ImmutableArray<ContextFact> Facts,
    ImmutableArray<Decision> Decisions,
    string? Failure)
{
    /// <summary>A successful result.</summary>
    public static StageResult Success(
        IEnumerable<Artifact>? artifacts = null,
        IEnumerable<ContextFact>? facts = null,
        IEnumerable<Decision>? decisions = null) =>
        new(
            Succeeded: true,
            Artifacts: artifacts is null ? [] : [.. artifacts],
            Facts: facts is null ? [] : [.. facts],
            Decisions: decisions is null ? [] : [.. decisions],
            Failure: null);

    /// <summary>
    /// A failed result.
    /// </summary>
    /// <remarks>
    /// Failures carry any artifacts and decisions produced before the failure, because a
    /// partial result is evidence about what went wrong and is needed to compensate cleanly.
    /// </remarks>
    public static StageResult Failed(
        string failure,
        IEnumerable<Artifact>? artifacts = null,
        IEnumerable<Decision>? decisions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);

        return new StageResult(
            Succeeded: false,
            Artifacts: artifacts is null ? [] : [.. artifacts],
            Facts: [],
            Decisions: decisions is null ? [] : [.. decisions],
            Failure: failure);
    }
}

/// <summary>Executes one lifecycle stage.</summary>
public interface IStageAgent
{
    /// <summary>The identifier a workflow node uses to select this agent.</summary>
    string Id { get; }

    /// <summary>Performs the stage's work.</summary>
    Task<StageResult> ExecuteAsync(StageExecution execution, CancellationToken cancellationToken);
}

/// <summary>Resolves the agent a node names.</summary>
public interface IStageAgentRegistry
{
    /// <summary>Every agent id this registry can resolve.</summary>
    IReadOnlySet<string> KnownAgents { get; }

    /// <summary>Resolves an agent, or returns <see langword="null"/> when it is unknown.</summary>
    IStageAgent? Resolve(string agentId);
}
