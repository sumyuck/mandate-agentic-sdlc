using System.Collections.Immutable;
using Mandate.Agents;
using Mandate.Core.Execution;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Gates;
using Mandate.Orchestrator.Tests.Support;
using Mandate.Persistence;

namespace Mandate.Orchestrator.Tests.Execution;

/// <summary>
/// The engine refuses to start a run it cannot finish.
/// </summary>
/// <remarks>
/// A gate condition nothing can judge is the dangerous case: if it were skipped it would
/// appear in the audit log as a check that passed.
/// </remarks>
public sealed class EngineConfigurationTests
{
    private static WorkflowEngine Build(
        WorkflowDefinition definition,
        IStageAgentRegistry? agents = null,
        IGateEvaluatorRegistry? gates = null) =>
        new(
            WorkflowGraph.Build(definition),
            agents ?? new StageAgentRegistry([]),
            gates ?? BuiltInGateEvaluators.CreateRegistry(),
            new InMemoryRunJournal(),
            new TestClock());

    [Fact]
    public void An_unregistered_agent_is_refused_at_construction()
    {
        EngineConfigurationException error = Should.Throw<EngineConfigurationException>(
            () => Build(WorkflowFixtures.Definition([WorkflowFixtures.Node("a")], [])));

        error.Problems.ShouldContain(problem => problem.Contains("a-agent", StringComparison.Ordinal));
        error.Message.ShouldContain("Registered agents: none");
    }

    [Fact]
    public void A_gate_condition_nothing_can_judge_is_refused_at_construction()
    {
        WorkflowDefinition definition = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node(
                    "a",
                    exitGate: [new GateCondition("vibes-acceptable", "", "Feels right.")]),
            ],
            []);

        EngineConfigurationException error = Should.Throw<EngineConfigurationException>(
            () => Build(definition, agents: new StageAgentRegistry(
                [new Agents.Scripted.ScriptedStageAgent("a-agent")])));

        error.Problems.ShouldContain(problem =>
            problem.Contains("vibes-acceptable", StringComparison.Ordinal));
        error.Message.ShouldContain("nothing can judge");
    }

    [Fact]
    public void Every_unmet_requirement_is_reported_at_once()
    {
        WorkflowDefinition definition = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("a", exitGate: [new GateCondition("nope", "", "x")]),
                WorkflowFixtures.Node("b", exitGate: [new GateCondition("also-nope", "", "y")]),
            ],
            [WorkflowEdge.Forward(Core.Identifiers.NodeId.Parse("a"), Core.Identifiers.NodeId.Parse("b"))]);

        EngineConfigurationException error =
            Should.Throw<EngineConfigurationException>(() => Build(definition));

        // Two missing agents and two unjudgeable conditions: one pass should fix all of them.
        error.Problems.Length.ShouldBe(4);
    }

    [Fact]
    public void A_fully_configured_workflow_constructs()
    {
        Should.NotThrow(() => Build(
            WorkflowFixtures.Definition([WorkflowFixtures.Node("a")], []),
            agents: new StageAgentRegistry([new Agents.Scripted.ScriptedStageAgent("a-agent")])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void An_unusable_concurrency_limit_is_refused(int limit) =>
        Should.Throw<ArgumentOutOfRangeException>(() => new EngineOptions(limit).Validated());

    [Fact]
    public void Two_evaluators_claiming_the_same_kind_are_refused()
    {
        // Which one judged a gate would depend on registration order.
        Should.Throw<ArgumentException>(() => new GateEvaluatorRegistry(
            [new ArtifactExistsGateEvaluator(), new ArtifactExistsGateEvaluator()]));
    }

    [Fact]
    public void Two_agents_claiming_the_same_id_are_refused()
    {
        Should.Throw<ArgumentException>(() => new StageAgentRegistry(
        [
            new Agents.Scripted.ScriptedStageAgent("duplicate"),
            new Agents.Scripted.ScriptedStageAgent("duplicate"),
        ]));
    }

    [Fact]
    public void The_built_in_registry_covers_every_condition_the_shipped_lifecycle_uses()
    {
        WorkflowGraph shipped = Workflows.WorkflowYamlLoader.LoadGraph(
            Path.Combine(Architecture.RepositoryLayout.Root.FullName, "workflows", "sdlc.v1.yaml"));

        ImmutableArray<string> unmet = WorkflowEngine.FindUnmetRequirements(
            shipped,
            Agents.Scripted.ScriptedAgents.CoveringGraph(shipped),
            BuiltInGateEvaluators.CreateRegistry());

        unmet.ShouldBeEmpty();
    }

    [Fact]
    public void A_node_naming_an_unregistered_compensating_action_is_refused()
    {
        // A node that cannot be undone must not claim it can — least of all discovered at the
        // moment the run most needs to undo something.
        WorkflowDefinition definition = WorkflowFixtures.Definition(
            [WorkflowFixtures.Node("a") with { Compensation = "wave-a-wand" }], []);

        EngineConfigurationException error = Should.Throw<EngineConfigurationException>(
            () => Build(definition, agents: new StageAgentRegistry(
                [new Agents.Scripted.ScriptedStageAgent("a-agent")])));

        error.Problems.ShouldContain(problem =>
            problem.Contains("wave-a-wand", StringComparison.Ordinal));
        error.Message.ShouldContain("must not claim it can");
    }

    [Theory]
    [InlineData(FallbackStrategy.DegradedAgent)]
    [InlineData(FallbackStrategy.SkipWithWaiver)]
    public void A_fallback_this_build_cannot_perform_is_refused_at_construction(
        FallbackStrategy unsupported)
    {
        // Declarable in the schema, not yet performable. Refusing here beats discovering it
        // when the fallback is actually needed.
        WorkflowDefinition definition = WorkflowFixtures.Definition(
            [
                WorkflowFixtures.Node("a") with
                {
                    Retry = RetryPolicy.Default with { OnExhaustion = unsupported },
                },
            ],
            []);

        EngineConfigurationException error = Should.Throw<EngineConfigurationException>(
            () => Build(definition, agents: new StageAgentRegistry(
                [new Agents.Scripted.ScriptedStageAgent("a-agent")])));

        error.Message.ShouldContain("cannot perform");
        error.Message.ShouldContain("fail-node, human-handoff, compensate");
    }

    [Fact]
    public void Two_compensating_actions_claiming_the_same_id_are_refused() =>
        Should.Throw<ArgumentException>(() => new Compensation.CompensationRegistry(
            [new Compensation.RevertNodeCommitAction(), new Compensation.RevertNodeCommitAction()]));

    [Fact]
    public void A_run_must_be_initiated_by_a_named_human()
    {
        // A run with no accountable requester is not auditable.
        Should.Throw<ArgumentException>(() => RunRequest.Create(
            Core.Identifiers.RunId.New(DateTimeOffset.UnixEpoch, "abc123"),
            "Do the thing",
            ScenarioKind.Greenfield,
            Core.Identifiers.Actor.Agent("not-a-person"),
            hasExistingCode: false));
    }

    [Fact]
    public void A_run_must_declare_its_scenario() =>
        Should.Throw<ArgumentException>(() => RunRequest.Create(
            Core.Identifiers.RunId.New(DateTimeOffset.UnixEpoch, "abc123"),
            "Do the thing",
            ScenarioKind.Unknown,
            Core.Identifiers.Actor.Human("tester"),
            hasExistingCode: false));
}
