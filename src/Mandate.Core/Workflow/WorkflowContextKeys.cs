using System.Collections.Immutable;

namespace Mandate.Core.Workflow;

/// <summary>
/// Context keys the engine itself contributes, before any stage runs.
/// </summary>
/// <remarks>
/// Guards routinely branch on run-level facts — most importantly the scenario, which selects
/// the brownfield-only path through the lifecycle. Those facts come from the engine rather
/// than from a stage, so they have to be known to validation; otherwise a guard on
/// <c>run.scenario</c> would look like a reference to a key nothing produces.
/// </remarks>
public static class WorkflowContextKeys
{
    /// <summary>The run's identifier.</summary>
    public const string RunId = "run.id";

    /// <summary>
    /// The requirement as the requester wrote it, verbatim.
    /// </summary>
    /// <remarks>
    /// Seeded as a fact rather than reached for through the engine, because a stage agent
    /// is given a scoped view of the context and nothing else. Without this the intake
    /// stage — whose entire job is to record what was asked for — would have no way to see
    /// what was asked for.
    /// </remarks>
    public const string Request = "run.request";

    /// <summary>Which scenario the run is: <c>greenfield</c>, <c>brownfield</c> or <c>ambiguous</c>.</summary>
    public const string Scenario = "run.scenario";

    /// <summary>The workflow name and version being executed.</summary>
    public const string Workflow = "run.workflow";

    /// <summary>The human who initiated the run.</summary>
    public const string InitiatedBy = "run.initiated-by";

    /// <summary>Whether the target repository already contains the code under change.</summary>
    public const string HasExistingCode = "run.has-existing-code";

    /// <summary>
    /// The most recent amendment a human made to an input the run had already acted on.
    /// </summary>
    public const string Amendment = "run.amendment";

    /// <summary>Every key the engine guarantees is present.</summary>
    public static ImmutableHashSet<string> EngineProvided { get; } =
        [RunId, Request, Scenario, Workflow, InitiatedBy, HasExistingCode, Amendment];

    /// <summary>
    /// The node id the engine attributes run-level facts to.
    /// </summary>
    /// <remarks>
    /// Every context fact names the node that contributed it, so the facts the engine seeds
    /// before any stage runs need an attributable source. A reserved id keeps that honest
    /// rather than leaving those facts unattributed; workflow validation refuses to let a real
    /// node take the name.
    /// </remarks>
    public const string EngineNodeId = "run";
}
