using System.Diagnostics;
using Mandate.Core.Identifiers;

namespace Mandate.Orchestrator.Execution;

/// <summary>
/// Trace spans for a run: the run is a trace, each stage attempt a span beneath it.
/// </summary>
/// <remarks>
/// <para>
/// That shape is the one an operator already knows how to read, and it makes a run's parallel
/// section visible as overlapping spans rather than as a claim in a document.
/// </para>
/// <para>
/// Built on <see cref="ActivitySource"/> from the base library rather than a vendor SDK, so
/// the engine produces the signal without taking a tracing dependency — which would also
/// have broken the rule that the engine references only the domain. Whoever runs it decides
/// where the spans go; with no listener attached the calls cost almost nothing.
/// </para>
/// </remarks>
internal static class RunActivity
{
    /// <summary>The name to subscribe to when collecting this system's traces.</summary>
    public const string SourceName = "Mandate.Orchestrator";

    private static readonly ActivitySource Source = new(SourceName, "0.1.0");

    public static Activity? StartRun(RunId runId, string workflow, string scenario)
    {
        Activity? activity = Source.StartActivity("run", ActivityKind.Internal);

        activity?.SetTag("mandate.run_id", runId.Value);
        activity?.SetTag("mandate.workflow", workflow);
        activity?.SetTag("mandate.scenario", scenario);

        return activity;
    }

    public static Activity? StartAttempt(
        RunId runId,
        string nodeId,
        string stage,
        string agent,
        string? model,
        string autonomy,
        int attempt)
    {
        Activity? activity = Source.StartActivity($"stage {nodeId}", ActivityKind.Internal);

        activity?.SetTag("mandate.run_id", runId.Value);
        activity?.SetTag("mandate.node_id", nodeId);
        activity?.SetTag("mandate.stage", stage);
        activity?.SetTag("mandate.agent", agent);
        activity?.SetTag("mandate.model", model);
        activity?.SetTag("mandate.autonomy", autonomy);
        activity?.SetTag("mandate.attempt", attempt);

        return activity;
    }

    public static void RecordOutcome(Activity? activity, bool succeeded, string? failure)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag("mandate.succeeded", succeeded);

        if (!succeeded)
        {
            activity.SetTag("mandate.failure", failure);
            activity.SetStatus(ActivityStatusCode.Error, failure);
        }
    }
}
