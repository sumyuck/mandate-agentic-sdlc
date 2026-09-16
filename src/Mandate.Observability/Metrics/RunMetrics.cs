using System.Collections.Immutable;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;

namespace Mandate.Observability.Metrics;

/// <summary>How long a stage took, and how many attempts it needed.</summary>
/// <param name="NodeId">The stage.</param>
/// <param name="Attempts">How many execution attempts were started.</param>
/// <param name="Duration">Time from its first attempt starting to its last finishing.</param>
/// <param name="State">Where it ended up.</param>
public sealed record StageTiming(
    NodeId NodeId, int Attempts, TimeSpan Duration, NodeState State);

/// <summary>A failure and the recovery that followed it, if there was one.</summary>
/// <param name="NodeId">The stage that failed.</param>
/// <param name="FailedAt">When it failed.</param>
/// <param name="RecoveredAt">When it next succeeded, or <see langword="null"/> if it never did.</param>
public sealed record FailureRecovery(NodeId NodeId, DateTimeOffset FailedAt, DateTimeOffset? RecoveredAt)
{
    /// <summary>How long recovery took, where it happened.</summary>
    public TimeSpan? TimeToRecovery => RecoveredAt - FailedAt;
}

/// <summary>
/// The reliability picture of one run, derived entirely from its audit log.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is stored. Every figure is computed by folding over the recorded events,
/// which is the point: a metric that is written down separately can drift from what happened,
/// and a metric that flatters the system is worse than no metric. These cannot be set — only
/// caused.
/// </para>
/// <para>
/// The consequence is that the numbers are only as good as the log, and the log is
/// hash-chained. "Our success rate is 94%" and "here is the tamper-evident record it was
/// computed from" are different claims, and this system can make the second one.
/// </para>
/// </remarks>
public sealed record RunMetrics
{
    /// <summary>The run these figures describe.</summary>
    public required RunId RunId { get; init; }

    /// <summary>Where the run ended up.</summary>
    public required RunStatus Status { get; init; }

    /// <summary>When the run was planned.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When its last event was recorded.</summary>
    public required DateTimeOffset EndedAt { get; init; }

    /// <summary>How many events the run produced.</summary>
    public required int EventCount { get; init; }

    // ---- stages ----

    /// <summary>Timing and attempt counts per stage, in execution order.</summary>
    public required ImmutableArray<StageTiming> Stages { get; init; }

    /// <summary>Stages that reached a successful outcome.</summary>
    public required int Succeeded { get; init; }

    /// <summary>Stages not required on the path the run took.</summary>
    public required int Skipped { get; init; }

    /// <summary>Stages that failed and were not recovered.</summary>
    public required int Failed { get; init; }

    /// <summary>Stages waiting on a human.</summary>
    public required int AwaitingHuman { get; init; }

    /// <summary>Stages whose effects were undone.</summary>
    public required int RolledBack { get; init; }

    // ---- reliability ----

    /// <summary>Execution attempts started across all stages.</summary>
    public required int Attempts { get; init; }

    /// <summary>Attempts that were retries of a previous one.</summary>
    public required int Retries { get; init; }

    /// <summary>Stages that spent their whole retry budget.</summary>
    public required int RetryBudgetsExhausted { get; init; }

    /// <summary>Compensating actions that ran.</summary>
    public required int Compensations { get; init; }

    /// <summary>Each failure and the recovery that followed it.</summary>
    public required ImmutableArray<FailureRecovery> Failures { get; init; }

    // ---- governance ----

    /// <summary>Gate conditions evaluated.</summary>
    public required int GateConditionsEvaluated { get; init; }

    /// <summary>Gate conditions that did not hold.</summary>
    public required int GateConditionsFailed { get; init; }

    /// <summary>Approvals asked for.</summary>
    public required int ApprovalsRequested { get; init; }

    /// <summary>Approvals granted.</summary>
    public required int ApprovalsGranted { get; init; }

    /// <summary>Approvals refused.</summary>
    public required int ApprovalsDenied { get; init; }

    /// <summary>How long each approval waited, from request to decision.</summary>
    public required ImmutableArray<TimeSpan> ApprovalWaits { get; init; }

    /// <summary>Policy packs evaluated.</summary>
    public required int PolicyEvaluations { get; init; }

    /// <summary>Policy violations that stopped the run.</summary>
    public required int PolicyViolationsBlocked { get; init; }

    /// <summary>Policy violations a human allowed through.</summary>
    public required int PolicyWaiversGranted { get; init; }

    /// <summary>Times the plan was recomputed.</summary>
    public required int ReplansPerformed { get; init; }

    /// <summary>Times a re-plan was called for but refused because the budget was spent.</summary>
    public required int ReplansRefused { get; init; }

    // ---- derived ----

    /// <summary>Wall-clock time from the run being planned to its last event.</summary>
    public TimeSpan EndToEnd => EndedAt - StartedAt;

    /// <summary>
    /// Stages that succeeded, as a share of those that were actually attempted.
    /// </summary>
    /// <remarks>
    /// Skipped stages are excluded. Counting a stage the run correctly declined to take as a
    /// success would make a narrow path look more reliable than a thorough one.
    /// </remarks>
    public double SuccessRate
    {
        get
        {
            int attempted = Succeeded + Failed + RolledBack;
            return attempted == 0 ? 1d : (double)Succeeded / attempted;
        }
    }

    /// <summary>Retries as a share of all attempts.</summary>
    public double RetryRate => Attempts == 0 ? 0d : (double)Retries / Attempts;

    /// <summary>Compensations as a share of stages that were attempted.</summary>
    public double RollbackRate
    {
        get
        {
            int attempted = Succeeded + Failed + RolledBack;
            return attempted == 0 ? 0d : (double)Compensations / attempted;
        }
    }

    /// <summary>
    /// Mean time to recovery across failures that were recovered.
    /// </summary>
    /// <remarks>
    /// Unrecovered failures are excluded rather than counted as infinite, and the count of
    /// them is reported separately. Folding them in would produce a single number that hides
    /// the difference between slow recovery and none.
    /// </remarks>
    public TimeSpan? MeanTimeToRecovery
    {
        get
        {
            ImmutableArray<TimeSpan> recovered =
                [.. Failures.Where(f => f.TimeToRecovery is not null).Select(f => f.TimeToRecovery!.Value)];

            return recovered.IsEmpty
                ? null
                : TimeSpan.FromTicks((long)recovered.Average(span => span.Ticks));
        }
    }

    /// <summary>Failures from which the run never recovered.</summary>
    public int UnrecoveredFailures => Failures.Count(failure => failure.RecoveredAt is null);

    /// <summary>Gate conditions that did not hold, as a share of those evaluated.</summary>
    public double GateBlockRate =>
        GateConditionsEvaluated == 0
            ? 0d
            : (double)GateConditionsFailed / GateConditionsEvaluated;

    /// <summary>Mean time an approval waited for a decision.</summary>
    public TimeSpan? MeanApprovalWait =>
        ApprovalWaits.IsEmpty
            ? null
            : TimeSpan.FromTicks((long)ApprovalWaits.Average(span => span.Ticks));

    /// <summary>Median stage duration.</summary>
    public TimeSpan MedianStageDuration => Percentile(0.50);

    /// <summary>Ninety-fifth percentile stage duration.</summary>
    public TimeSpan NinetyFifthStageDuration => Percentile(0.95);

    /// <summary>
    /// Work the agents did unattended, as a share of all recorded actions.
    /// </summary>
    /// <remarks>
    /// The number the governing principle is about. A ratio near one means the humans are
    /// rubber-stamping; a ratio near zero means the automation is not earning its place. It
    /// is reported rather than targeted, because the right value depends on what is at stake.
    /// </remarks>
    public double AutonomyRatio
    {
        get
        {
            int humanDecisions = ApprovalsGranted + ApprovalsDenied + PolicyWaiversGranted;
            int total = Attempts + humanDecisions;

            return total == 0 ? 0d : (double)Attempts / total;
        }
    }

    private TimeSpan Percentile(double fraction)
    {
        ImmutableArray<TimeSpan> durations =
        [
            .. Stages
                .Where(stage => stage.Duration > TimeSpan.Zero)
                .Select(stage => stage.Duration)
                .Order(),
        ];

        if (durations.IsEmpty)
        {
            return TimeSpan.Zero;
        }

        int index = (int)Math.Ceiling(fraction * durations.Length) - 1;
        return durations[Math.Clamp(index, 0, durations.Length - 1)];
    }
}
