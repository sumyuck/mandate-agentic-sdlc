using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Decisions;
using Mandate.Core.Identifiers;
using Mandate.Core.Llm;
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
/// <param name="Workspace">
/// A read-only view of the tree the run is building. A stage that reviews code, scans it, or
/// extends it has to be able to read it; only the engine writes.
/// </param>
/// <param name="PreviousFailure">
/// Why the last attempt at this node failed, or <see langword="null"/> on the first.
/// </param>
/// <remarks>
/// A retry that cannot see why it failed asks an identical question and gets an identical
/// answer. Without this the retry budget is decorative against any deterministic failure —
/// a compiler error, a contract violation — and every attempt burns a prompt to be told the
/// same thing. It stays reproducible because the failure is itself deterministic: the same
/// history produces the same first failure, so it produces the same second prompt.
/// </remarks>
public sealed record StageExecution(
    RunId RunId,
    WorkflowNode Node,
    int Attempt,
    RunContext Context,
    ImmutableArray<Artifact> Inputs,
    Actor Actor,
    IWorkspaceReader Workspace,
    string? PreviousFailure = null)
{
    /// <summary>An execution with no workspace, for tests that do not exercise the tree.</summary>
    public StageExecution(
        RunId runId,
        WorkflowNode node,
        int attempt,
        RunContext context,
        ImmutableArray<Artifact> inputs,
        Actor actor)
        : this(runId, node, attempt, context, inputs, actor, IWorkspaceReader.Empty)
    {
    }
}

/// <summary>What a stage agent produced.</summary>
/// <param name="Succeeded">Whether the stage completed its work.</param>
/// <param name="Files">
/// Files the stage proposes to write into the run workspace. The stage does not write them:
/// the engine validates every path and applies them as that node's commit, which is what
/// makes "the agent may act in the workspace" a boundary the engine enforces rather than a
/// description of intended behaviour.
/// </param>
/// <param name="Artifacts">Artifacts produced, with provenance.</param>
/// <param name="Facts">Context facts contributed for later stages and for guards.</param>
/// <param name="Decisions">Choices made, with the options rejected and the rationale.</param>
/// <param name="Failure">Why the stage did not complete, when it did not.</param>
/// <param name="ModelCalls">
/// Every language-model call the stage made, in order. Reported by the stage rather than
/// observed by the engine, because the engine does not sit between an agent and its model —
/// and carried on failed results too, since the spend happened either way.
/// </param>
public sealed record StageResult(
    bool Succeeded,
    ImmutableArray<WorkspaceFile> Files,
    ImmutableArray<Artifact> Artifacts,
    ImmutableArray<ContextFact> Facts,
    ImmutableArray<Decision> Decisions,
    string? Failure,
    ImmutableArray<ModelCall> ModelCalls)
{
    /// <summary>A successful result.</summary>
    public static StageResult Success(
        IEnumerable<Artifact>? artifacts = null,
        IEnumerable<ContextFact>? facts = null,
        IEnumerable<Decision>? decisions = null,
        IEnumerable<WorkspaceFile>? files = null,
        IEnumerable<ModelCall>? modelCalls = null) =>
        new(
            Succeeded: true,
            Files: files is null ? [] : [.. files],
            Artifacts: artifacts is null ? [] : [.. artifacts],
            Facts: facts is null ? [] : [.. facts],
            Decisions: decisions is null ? [] : [.. decisions],
            Failure: null,
            ModelCalls: modelCalls is null ? [] : [.. modelCalls]);

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
        IEnumerable<Decision>? decisions = null,
        IEnumerable<ModelCall>? modelCalls = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failure);

        return new StageResult(
            Succeeded: false,
            // A failed stage proposes no files. Anything it half-wrote is not a change we
            // want applied, and discarding it keeps the workspace at a state some node
            // declared rather than one nobody did.
            Files: [],
            Artifacts: artifacts is null ? [] : [.. artifacts],
            Facts: [],
            Decisions: decisions is null ? [] : [.. decisions],
            Failure: failure,
            // Spend is not undone by failure. Dropping the calls here would make a run that
            // burned its retry budget look cheaper than one that succeeded first time.
            ModelCalls: modelCalls is null ? [] : [.. modelCalls]);
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
