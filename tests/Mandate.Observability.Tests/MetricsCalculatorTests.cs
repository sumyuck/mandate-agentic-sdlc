using Mandate.Core.Runs;
using Mandate.Observability.Metrics;

namespace Mandate.Observability.Tests;

/// <summary>
/// The figures a run reports about itself.
/// </summary>
/// <remarks>
/// Every one is derived from the event log, so each test here is really a statement about
/// what the log can be made to say — and, in a few cases, about what it must not be allowed
/// to say. A metric that flatters the system is worse than no metric.
/// </remarks>
public sealed class MetricsCalculatorTests
{
    private static RunMetrics Measure(EventLogBuilder log) =>
        MetricsCalculator.Compute(log.RunId, log.Events);

    [Fact]
    public void An_empty_log_measures_to_nothing_rather_than_throwing()
    {
        RunMetrics metrics = MetricsCalculator.Compute(
            Core.Identifiers.RunId.New(DateTimeOffset.UnixEpoch, "none01"), []);

        metrics.EventCount.ShouldBe(0);
        metrics.SuccessRate.ShouldBe(1d);
        metrics.MeanTimeToRecovery.ShouldBeNull();
    }

    [Fact]
    public void A_clean_run_reports_full_success()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b").Started()
            .Attempt("a").Moves("a", NodeState.Running, NodeState.Succeeded)
            .Attempt("b").Moves("b", NodeState.Running, NodeState.Succeeded)
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.SuccessRate.ShouldBe(1d);
        metrics.Succeeded.ShouldBe(2);
        metrics.Attempts.ShouldBe(2);
        metrics.Retries.ShouldBe(0);
    }

    [Fact]
    public void A_skipped_stage_is_not_counted_as_a_success()
    {
        // Counting a stage the run correctly declined to take as a success would make a
        // narrow path look more reliable than a thorough one.
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b").Started()
            .Attempt("a").Moves("a", NodeState.Running, NodeState.Succeeded)
            .Moves("b", NodeState.Pending, NodeState.Skipped)
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.Succeeded.ShouldBe(1);
        metrics.Skipped.ShouldBe(1);
        metrics.SuccessRate.ShouldBe(1d, "one of one attempted, not one of two.");
    }

    [Fact]
    public void A_failed_stage_lowers_the_success_rate()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b").Started()
            .Attempt("a").Moves("a", NodeState.Running, NodeState.Succeeded)
            .Attempt("b").Moves("b", NodeState.Running, NodeState.Failed)
            .Completed(RunStatus.Failed);

        Measure(log).SuccessRate.ShouldBe(0.5d);
    }

    // ---- retries ----

    [Fact]
    public void A_second_attempt_counts_as_a_retry()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Attempt("a", attempt: 1)
            .Retries("a", 2)
            .Attempt("a", attempt: 2)
            .Moves("a", NodeState.Running, NodeState.Succeeded)
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.Attempts.ShouldBe(2);
        metrics.Retries.ShouldBe(1);
        metrics.RetryRate.ShouldBe(0.5d);
    }

    [Fact]
    public void A_stage_re_run_by_a_re_plan_is_not_mistaken_for_a_retry()
    {
        // A re-run starts its attempt numbering again. Counting by subtracting stage counts
        // would call that a retry; a retry and a redo have different causes.
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Attempt("a", attempt: 1)
            .Moves("a", NodeState.Running, NodeState.Succeeded)
            .Replanned("a")
            .Attempt("a", attempt: 1)
            .Moves("a", NodeState.Running, NodeState.Succeeded)
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.Attempts.ShouldBe(2);
        metrics.Retries.ShouldBe(0);
        metrics.ReplansPerformed.ShouldBe(1);
    }

    // ---- recovery ----

    [Fact]
    public void Mean_time_to_recovery_measures_from_failure_to_the_next_success()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Attempt("a")
            .Moves("a", NodeState.Running, NodeState.Failed)
            .Advance(30)
            .Moves("a", NodeState.Failed, NodeState.Succeeded)
            .Completed(RunStatus.Succeeded);

        Measure(log).MeanTimeToRecovery.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_failure_that_never_recovered_is_reported_separately_not_averaged_away()
    {
        // Dropping it would improve the mean by removing exactly the cases that never
        // recovered; folding it in as infinity would hide the difference between slow
        // recovery and none.
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b").Started()
            .Attempt("a").Moves("a", NodeState.Running, NodeState.Failed)
            .Advance(10).Moves("a", NodeState.Failed, NodeState.Succeeded)
            .Attempt("b").Moves("b", NodeState.Running, NodeState.Failed)
            .Completed(RunStatus.Failed);

        RunMetrics metrics = Measure(log);

        metrics.MeanTimeToRecovery.ShouldBe(TimeSpan.FromSeconds(10));
        metrics.UnrecoveredFailures.ShouldBe(1);
        metrics.Failures.Length.ShouldBe(2);
    }

    // ---- governance ----

    [Fact]
    public void The_gate_block_rate_counts_conditions_not_gates()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Gate("a", passed: true, conditions: 3)
            .Gate("a", passed: false, conditions: 1)
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.GateConditionsEvaluated.ShouldBe(4);
        metrics.GateConditionsFailed.ShouldBe(1);
        metrics.GateBlockRate.ShouldBe(0.25d);
    }

    [Fact]
    public void Approval_wait_is_measured_from_the_request_to_the_decision()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .AsksApproval("a", "tech-lead")
            .Advance(120)
            .Approves("tech-lead", "alex")
            .Completed(RunStatus.Succeeded);

        Measure(log).MeanApprovalWait.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void A_refusal_counts_as_a_decision_for_the_purpose_of_waiting()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .AsksApproval("a", "tech-lead")
            .Advance(60)
            .Denies("tech-lead", "alex")
            .Completed(RunStatus.Blocked);

        RunMetrics metrics = Measure(log);

        metrics.ApprovalsDenied.ShouldBe(1);
        metrics.MeanApprovalWait.ShouldBe(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void An_approval_still_outstanding_contributes_no_wait_time()
    {
        // Including it would report a wait that has not finished, which would fall as the
        // run went on rather than rising.
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .AsksApproval("a", "tech-lead")
            .Advance(300)
            .Completed(RunStatus.AwaitingApproval);

        RunMetrics metrics = Measure(log);

        metrics.ApprovalsRequested.ShouldBe(1);
        metrics.MeanApprovalWait.ShouldBeNull();
    }

    // ---- autonomy ----

    [Fact]
    public void The_autonomy_ratio_weighs_agent_work_against_human_decisions()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b", "c").Started()
            .Attempt("a").Attempt("b").Attempt("c")
            .AsksApproval("c", "tech-lead")
            .Approves("tech-lead", "alex")
            .Completed(RunStatus.Succeeded);

        // Three agent attempts, one human decision.
        Measure(log).AutonomyRatio.ShouldBe(0.75d);
    }

    [Fact]
    public void A_waiver_counts_as_a_human_decision()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Attempt("a")
            .Waives("CHG-001", "alex")
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.PolicyWaiversGranted.ShouldBe(1);
        metrics.AutonomyRatio.ShouldBe(0.5d);
    }

    // ---- rollback ----

    [Fact]
    public void The_rollback_rate_counts_compensations_against_attempted_stages()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b").Started()
            .Attempt("a").Moves("a", NodeState.Running, NodeState.Succeeded)
            .Attempt("b").Moves("b", NodeState.Running, NodeState.Failed)
            .Compensated("a")
            .Completed(RunStatus.RolledBack);

        RunMetrics metrics = Measure(log);

        metrics.Compensations.ShouldBe(1);
        metrics.RollbackRate.ShouldBe(0.5d);
    }

    // ---- timings ----

    [Fact]
    public void Stage_duration_spans_the_first_attempt_to_the_last()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Attempt("a", attempt: 1, takesSeconds: 2)
            .Advance(5)
            .Attempt("a", attempt: 2, takesSeconds: 3)
            .Moves("a", NodeState.Running, NodeState.Succeeded)
            .Completed(RunStatus.Succeeded);

        StageTiming stage = Measure(log).Stages.ShouldHaveSingleItem();

        stage.Attempts.ShouldBe(2);
        stage.Duration.ShouldBe(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Percentiles_are_taken_over_the_stages_that_ran()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b", "c", "d").Started()
            .Attempt("a", takesSeconds: 1)
            .Attempt("b", takesSeconds: 2)
            .Attempt("c", takesSeconds: 3)
            .Attempt("d", takesSeconds: 40)
            .Completed(RunStatus.Succeeded);

        RunMetrics metrics = Measure(log);

        metrics.MedianStageDuration.ShouldBe(TimeSpan.FromSeconds(2));
        metrics.NinetyFifthStageDuration.ShouldBe(TimeSpan.FromSeconds(40));
    }

    [Fact]
    public void End_to_end_spans_the_whole_log()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Advance(90)
            .Completed(RunStatus.Succeeded);

        Measure(log).EndToEnd.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void The_status_comes_from_the_log_rather_than_from_the_caller()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a").Started()
            .Completed(RunStatus.AwaitingApproval);

        Measure(log).Status.ShouldBe(RunStatus.AwaitingApproval);
    }
}
