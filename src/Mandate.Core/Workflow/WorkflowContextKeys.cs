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

    /// <summary>Which scenario the run is: <c>greenfield</c>, <c>brownfield</c> or <c>ambiguous</c>.</summary>
    public const string Scenario = "run.scenario";

    /// <summary>The workflow name and version being executed.</summary>
    public const string Workflow = "run.workflow";

    /// <summary>The human who initiated the run.</summary>
    public const string InitiatedBy = "run.initiated-by";

    /// <summary>Whether the target repository already contains the code under change.</summary>
    public const string HasExistingCode = "run.has-existing-code";

    /// <summary>Every key the engine guarantees is present.</summary>
    public static ImmutableHashSet<string> EngineProvided { get; } =
        [RunId, Scenario, Workflow, InitiatedBy, HasExistingCode];
}
