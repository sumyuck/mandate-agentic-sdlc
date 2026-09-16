using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Tests.Architecture;
using Mandate.Orchestrator.Tests.Support;
using Mandate.Workflows;

namespace Mandate.Orchestrator.Tests.Execution;

/// <summary>
/// The shipped lifecycle, executed end to end for each scenario.
/// </summary>
/// <remarks>
/// These are the tests that show the orchestration is non-linear in practice and not only on
/// paper: the same workflow takes three materially different paths, and stops in three
/// different places, according to the run's own context.
/// </remarks>
public sealed class ShippedLifecycleRunTests
{
    private static NodeId Id(string value) => NodeId.Parse(value);

    private static EngineHarness Harness() => EngineHarness.For(
        WorkflowYamlLoader.LoadFile(
            Path.Combine(RepositoryLayout.Root.FullName, "workflows", "sdlc.v1.yaml")),
        options: new EngineOptions(MaxConcurrency: 4));

    [Fact]
    public async Task Greenfield_skips_impact_analysis_and_clarification()
    {
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(
            ScenarioKind.Greenfield, hasExistingCode: false,
            request: "Build a URL shortener with create and redirect APIs.");

        EngineHarness.StateOf(outcome, "requirements").ShouldBe(NodeState.Succeeded);
        EngineHarness.StateOf(outcome, "impact-analysis").ShouldBe(NodeState.Skipped);
        EngineHarness.StateOf(outcome, "clarification").ShouldBe(NodeState.Skipped);
    }

    [Fact]
    public async Task Brownfield_earns_the_impact_analysis_stage()
    {
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(
            ScenarioKind.Brownfield, hasExistingCode: true,
            request: "Add per-link click analytics and per-API-key rate limiting.");

        EngineHarness.StateOf(outcome, "impact-analysis").ShouldBe(NodeState.Succeeded);
        EngineHarness.StateOf(outcome, "clarification").ShouldBe(NodeState.Skipped);

        outcome.State.Context.Latest("impact.blast-radius").ShouldNotBeNull();
    }

    [Fact]
    public async Task An_ambiguous_requirement_diverts_to_a_human()
    {
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(
            ScenarioKind.Ambiguous, hasExistingCode: false,
            request: "Make our links more secure.");

        EngineHarness.StateOf(outcome, "clarification").ShouldBe(NodeState.AwaitingApproval);
    }

    [Fact]
    public async Task Design_refuses_to_proceed_on_a_requirement_flagged_as_ambiguous()
    {
        // The failure this precondition exists to prevent: designing against a requirement the
        // system has just reported it does not understand. The stage waits rather than guessing.
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(
            ScenarioKind.Ambiguous, hasExistingCode: false, request: "Make our links more secure.");

        EngineHarness.StateOf(outcome, "architecture").ShouldBe(NodeState.Blocked);
        string? detail = outcome.State.Nodes[Id("architecture")].Detail;
        detail.ShouldNotBeNull();
        detail.ShouldContain("requirements.ambiguity-score");
    }

    [Fact]
    public async Task Implementation_does_not_begin_before_the_design_is_approved()
    {
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(ScenarioKind.Greenfield);

        EngineHarness.StateOf(outcome, "architecture").ShouldBe(NodeState.AwaitingApproval);
        EngineHarness.StateOf(outcome, "implement").ShouldBe(NodeState.Pending);
        outcome.IsWaitingOnHuman.ShouldBeTrue();
    }

    [Fact]
    public async Task The_run_stops_at_the_first_human_checkpoint_with_its_work_preserved()
    {
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(ScenarioKind.Greenfield);

        outcome.Status.ShouldBe(RunStatus.AwaitingApproval);
        outcome.Reason.ShouldContain("complete and preserved");

        // The design was produced and is on the record; it is the sign-off that is missing.
        outcome.State.Artifacts.ShouldContain(artifact =>
            artifact.Kind == Core.Artifacts.ArtifactKind.DesignDoc);
    }

    [Fact]
    public async Task The_approval_request_names_the_role_and_the_producer()
    {
        EngineHarness harness = Harness();

        await harness.RunAsync(ScenarioKind.Greenfield);

        ApprovalRequestedPayload payload = harness
            .EventsOfKind(RunEventKind.ApprovalRequested)
            .Select(@event => @event.Payload<ApprovalRequestedPayload>())
            .Single(request => request.Role == "tech-lead");

        payload.SegregationOfDuties.ShouldBeTrue();
        payload.ProducedBy.ShouldBe("agent:architect");
        payload.Reason.ShouldContain("downstream");
    }

    [Fact]
    public async Task Decisions_are_recorded_with_the_option_that_was_rejected()
    {
        EngineHarness harness = Harness();

        await harness.RunAsync(ScenarioKind.Greenfield);

        DecisionRecordedPayload payload = harness
            .EventsOfKind(RunEventKind.DecisionRecorded)
            .Select(@event => @event.Payload<DecisionRecordedPayload>())
            .First();

        payload.Options.Length.ShouldBeGreaterThanOrEqualTo(2);
        payload.Options.ShouldContain(option => option.RejectedBecause != null);
        payload.Rationale.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_design_traces_back_to_the_original_request()
    {
        // Lineage without a trace document: the provenance is in the artifacts themselves.
        EngineHarness harness = Harness();

        RunOutcome outcome = await harness.RunAsync(ScenarioKind.Greenfield);

        Core.Artifacts.ArtifactProvenance provenance =
            Core.Artifacts.ArtifactProvenance.Build(outcome.State.Artifacts);

        Core.Artifacts.Artifact design = outcome.State.Artifacts
            .First(artifact => artifact.Kind == Core.Artifacts.ArtifactKind.DesignDoc);

        provenance.Lineage(design.Hash)
            .Select(artifact => artifact.Kind)
            .ShouldContain(Core.Artifacts.ArtifactKind.Request);
    }

    [Fact]
    public async Task Each_stage_records_the_model_that_executed_it()
    {
        EngineHarness harness = Harness();

        await harness.RunAsync(ScenarioKind.Greenfield);

        IEnumerable<NodeAttemptStartedPayload> attempts = harness
            .EventsOfKind(RunEventKind.NodeAttemptStarted)
            .Select(@event => @event.Payload<NodeAttemptStartedPayload>());

        attempts.ShouldAllBe(attempt => !string.IsNullOrWhiteSpace(attempt.Model));
        attempts.ShouldContain(attempt => attempt.Model == "claude-sonnet-5");
        attempts.ShouldContain(attempt => attempt.Model == "claude-haiku-4-5");
    }

    [Fact]
    public async Task Every_scenario_produces_an_intact_audit_chain()
    {
        foreach (ScenarioKind scenario in
                 new[] { ScenarioKind.Greenfield, ScenarioKind.Brownfield, ScenarioKind.Ambiguous })
        {
            EngineHarness harness = Harness();

            RunOutcome outcome = await harness.RunAsync(
                scenario, hasExistingCode: scenario == ScenarioKind.Brownfield);

            AuditChain.Verify(outcome.RunId, harness.Journal.Events)
                .IsIntact.ShouldBeTrue($"{scenario} produced a broken chain.");
        }
    }

    [Fact]
    public async Task The_three_scenarios_take_materially_different_paths()
    {
        Dictionary<ScenarioKind, HashSet<string>> executed = [];

        foreach (ScenarioKind scenario in
                 new[] { ScenarioKind.Greenfield, ScenarioKind.Brownfield, ScenarioKind.Ambiguous })
        {
            EngineHarness harness = Harness();

            await harness.RunAsync(scenario, hasExistingCode: scenario == ScenarioKind.Brownfield);

            executed[scenario] =
            [
                .. harness.EventsOfKind(RunEventKind.NodeAttemptStarted)
                    .Select(@event => @event.NodeId!.Value.Value),
            ];
        }

        executed[ScenarioKind.Brownfield].ShouldContain("impact-analysis");
        executed[ScenarioKind.Greenfield].ShouldNotContain("impact-analysis");
        executed[ScenarioKind.Ambiguous].ShouldContain("clarification");
        executed[ScenarioKind.Greenfield].ShouldNotContain("clarification");
        executed[ScenarioKind.Ambiguous].ShouldNotContain("architecture");
    }
}
