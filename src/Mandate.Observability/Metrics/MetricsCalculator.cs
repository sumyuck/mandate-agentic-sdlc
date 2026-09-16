using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;

namespace Mandate.Observability.Metrics;

/// <summary>
/// Derives a run's reliability figures from its audit log.
/// </summary>
/// <remarks>
/// Pure: events in, numbers out. No storage, no clock, no configuration. That is what makes
/// the figures reproducible — anyone holding the exported log can recompute them and get the
/// same answer, which is the difference between a metric and an assertion.
/// </remarks>
public static class MetricsCalculator
{
    /// <summary>Computes the figures for a run.</summary>
    public static RunMetrics Compute(RunId runId, ImmutableArray<RunEvent> events)
    {
        if (events.IsEmpty)
        {
            return Empty(runId);
        }

        RunState state = RunState.Rebuild(runId, events);

        return new RunMetrics
        {
            RunId = runId,
            Status = state.Status,
            StartedAt = events[0].OccurredAt,
            EndedAt = events[^1].OccurredAt,
            EventCount = events.Length,

            Stages = StageTimings(events, state),
            Succeeded = CountNodes(state, NodeState.Succeeded),
            Skipped = CountNodes(state, NodeState.Skipped),
            Failed = CountNodes(state, NodeState.Failed),
            AwaitingHuman = CountNodes(state, NodeState.AwaitingApproval)
                            + CountNodes(state, NodeState.Blocked),
            RolledBack = CountNodes(state, NodeState.RolledBack),

            Attempts = Count(events, RunEventKind.NodeAttemptStarted),
            Retries = Retries(events),
            RetryBudgetsExhausted = Count(events, RunEventKind.NodeRetryBudgetExhausted),
            Compensations = Count(events, RunEventKind.NodeCompensationCompleted),
            Failures = Recoveries(events),

            GateConditionsEvaluated = GateConditions(events, onlyFailed: false),
            GateConditionsFailed = GateConditions(events, onlyFailed: true),

            ApprovalsRequested = Count(events, RunEventKind.ApprovalRequested),
            ApprovalsGranted = Count(events, RunEventKind.ApprovalGranted),
            ApprovalsDenied = Count(events, RunEventKind.ApprovalDenied),
            ApprovalWaits = ApprovalWaits(events),

            PolicyEvaluations = Count(events, RunEventKind.PolicyEvaluated),
            PolicyViolationsBlocked = Count(events, RunEventKind.PolicyViolationBlocked),
            PolicyWaiversGranted = Count(events, RunEventKind.PolicyWaiverGranted),

            ReplansPerformed = Count(events, RunEventKind.ReplanPerformed),
            ReplansRefused = Count(events, RunEventKind.ReplanRefused),
        };
    }

    private static RunMetrics Empty(RunId runId) => new()
    {
        RunId = runId,
        Status = RunStatus.Unknown,
        StartedAt = DateTimeOffset.UnixEpoch,
        EndedAt = DateTimeOffset.UnixEpoch,
        EventCount = 0,
        Stages = [],
        Succeeded = 0,
        Skipped = 0,
        Failed = 0,
        AwaitingHuman = 0,
        RolledBack = 0,
        Attempts = 0,
        Retries = 0,
        RetryBudgetsExhausted = 0,
        Compensations = 0,
        Failures = [],
        GateConditionsEvaluated = 0,
        GateConditionsFailed = 0,
        ApprovalsRequested = 0,
        ApprovalsGranted = 0,
        ApprovalsDenied = 0,
        ApprovalWaits = [],
        PolicyEvaluations = 0,
        PolicyViolationsBlocked = 0,
        PolicyWaiversGranted = 0,
        ReplansPerformed = 0,
        ReplansRefused = 0,
    };

    private static int Count(ImmutableArray<RunEvent> events, RunEventKind kind) =>
        events.Count(@event => @event.Kind == kind);

    private static int CountNodes(RunState state, NodeState nodeState) =>
        state.Nodes.Values.Count(node => node.State == nodeState);

    /// <summary>
    /// Attempts that were not a stage's first.
    /// </summary>
    /// <remarks>
    /// Counted from the recorded attempt number rather than by subtracting stage counts,
    /// because a stage re-run by a re-plan starts its numbering again and would otherwise be
    /// mistaken for a retry. A retry and a redo are different events with different causes.
    /// </remarks>
    private static int Retries(ImmutableArray<RunEvent> events) =>
        events.Count(@event =>
            @event.Kind == RunEventKind.NodeAttemptStarted
            && @event.Payload<NodeAttemptStartedPayload>().Attempt > 1);

    private static ImmutableArray<StageTiming> StageTimings(
        ImmutableArray<RunEvent> events, RunState state)
    {
        Dictionary<NodeId, (DateTimeOffset First, DateTimeOffset Last, int Attempts)> spans = [];

        foreach (RunEvent @event in events)
        {
            if (@event.NodeId is not { } nodeId)
            {
                continue;
            }

            if (@event.Kind == RunEventKind.NodeAttemptStarted)
            {
                if (spans.TryGetValue(nodeId, out (DateTimeOffset First, DateTimeOffset Last, int Attempts) existing))
                {
                    spans[nodeId] = (existing.First, @event.OccurredAt, existing.Attempts + 1);
                }
                else
                {
                    spans[nodeId] = (@event.OccurredAt, @event.OccurredAt, 1);
                }
            }
            else if (@event.Kind == RunEventKind.NodeAttemptFinished
                     && spans.TryGetValue(nodeId, out (DateTimeOffset First, DateTimeOffset Last, int Attempts) running))
            {
                spans[nodeId] = (running.First, @event.OccurredAt, running.Attempts);
            }
        }

        return
        [
            .. spans
                .OrderBy(entry => entry.Value.First)
                .Select(entry => new StageTiming(
                    entry.Key,
                    entry.Value.Attempts,
                    entry.Value.Last - entry.Value.First,
                    state.StateOf(entry.Key))),
        ];
    }

    private static int GateConditions(ImmutableArray<RunEvent> events, bool onlyFailed) =>
        events
            .Where(@event => @event.Kind is RunEventKind.EntryGateEvaluated
                or RunEventKind.ExitGateEvaluated)
            .Select(@event => @event.Payload<GateEvaluatedPayload>())
            .SelectMany(payload => payload.Conditions)
            .Count(condition => !onlyFailed || !condition.Passed);

    /// <summary>
    /// Pairs each failure with the next success of the same stage.
    /// </summary>
    /// <remarks>
    /// A failure with no later success is kept with no recovery time rather than dropped.
    /// Dropping it would improve the mean time to recovery by removing exactly the cases that
    /// never recovered.
    /// </remarks>
    private static ImmutableArray<FailureRecovery> Recoveries(ImmutableArray<RunEvent> events)
    {
        ImmutableArray<FailureRecovery>.Builder recoveries =
            ImmutableArray.CreateBuilder<FailureRecovery>();

        Dictionary<NodeId, DateTimeOffset> openFailures = [];

        foreach (RunEvent @event in events)
        {
            if (@event.Kind != RunEventKind.NodeStateChanged || @event.NodeId is not { } nodeId)
            {
                continue;
            }

            NodeStateChangedPayload payload = @event.Payload<NodeStateChangedPayload>();

            if (payload.To == nameof(NodeState.Failed))
            {
                openFailures.TryAdd(nodeId, @event.OccurredAt);
            }
            else if (payload.To == nameof(NodeState.Succeeded)
                     && openFailures.Remove(nodeId, out DateTimeOffset failedAt))
            {
                recoveries.Add(new FailureRecovery(nodeId, failedAt, @event.OccurredAt));
            }
        }

        foreach ((NodeId nodeId, DateTimeOffset failedAt) in openFailures)
        {
            recoveries.Add(new FailureRecovery(nodeId, failedAt, null));
        }

        return [.. recoveries.OrderBy(recovery => recovery.FailedAt)];
    }

    private static ImmutableArray<TimeSpan> ApprovalWaits(ImmutableArray<RunEvent> events)
    {
        Dictionary<string, DateTimeOffset> requested = new(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<TimeSpan>.Builder waits = ImmutableArray.CreateBuilder<TimeSpan>();

        foreach (RunEvent @event in events)
        {
            switch (@event.Kind)
            {
                case RunEventKind.ApprovalRequested:
                    requested.TryAdd(
                        @event.Payload<ApprovalRequestedPayload>().Role, @event.OccurredAt);
                    break;

                case RunEventKind.ApprovalGranted:
                case RunEventKind.ApprovalDenied:
                    string role = @event.Payload<ApprovalDecidedPayload>().Role;

                    if (requested.Remove(role, out DateTimeOffset askedAt))
                    {
                        waits.Add(@event.OccurredAt - askedAt);
                    }

                    break;

                default:
                    break;
            }
        }

        return waits.ToImmutable();
    }
}
