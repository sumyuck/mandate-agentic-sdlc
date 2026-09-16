using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Runs;

namespace Mandate.Orchestrator.Execution;

/// <summary>
/// Where a resumed run picks up from.
/// </summary>
/// <remarks>
/// Reconstructed entirely from the run's own log: the state by folding the engine's reducer
/// over the events, and the original request from the plan event. Nothing about a resumed run
/// comes from the caller, so a resume cannot quietly change what the run was asked to do.
/// </remarks>
/// <param name="State">The run's state, projected from its events.</param>
/// <param name="Tail">The last event, which the next append links to.</param>
/// <param name="Request">The original request, read back from the plan event.</param>
/// <param name="Events">The run's full log, for decisions that depend on its history.</param>
public sealed record ResumePoint(
    RunState State, RunEvent Tail, RunRequest Request, ImmutableArray<RunEvent> Events)
{
    /// <summary>
    /// Reads a resume point out of a run's persisted events.
    /// </summary>
    /// <exception cref="InvalidOperationException">The log does not describe a resumable run.</exception>
    public static ResumePoint FromEvents(
        Core.Identifiers.RunId runId, ImmutableArray<RunEvent> events)
    {
        if (events.IsEmpty)
        {
            throw new InvalidOperationException(
                $"There are no recorded events for {runId}, so there is nothing to resume.");
        }

        RunEvent plan = events.FirstOrDefault(@event => @event.Kind == RunEventKind.RunPlanned)
                        ?? throw new InvalidOperationException(
                            $"The log for {runId} has no plan event, so the run's original "
                            + "request cannot be recovered. Refusing to guess at it.");

        RunPlannedPayload planned = plan.Payload<RunPlannedPayload>();

        if (!Enum.TryParse(planned.Scenario, ignoreCase: true, out ScenarioKind scenario))
        {
            throw new InvalidOperationException(
                $"The plan event for {runId} records scenario '{planned.Scenario}', which this "
                + "build does not recognise.");
        }

        RunState state = RunState.Rebuild(runId, events);

        string initiatedBy = state.Context
            .Latest(Core.Workflow.WorkflowContextKeys.InitiatedBy)?.Value
            ?? "human:unknown";

        RunRequest request = new(
            runId,
            planned.Request,
            scenario,
            Core.Identifiers.Actor.TryParse(initiatedBy, out Core.Identifiers.Actor actor)
                ? actor
                : Core.Identifiers.Actor.Human("unknown"),
            planned.HasExistingCode,
            ImmutableDictionary<string, string>.Empty);

        return new ResumePoint(state, events[^1], request, events);
    }

    /// <summary>True when the run has work the engine could still do.</summary>
    public bool HasOutstandingWork =>
        State.Nodes.Values.Any(node =>
            node.State is NodeState.Pending or NodeState.Ready or NodeState.AwaitingApproval
                or NodeState.Blocked or NodeState.Running);
}
