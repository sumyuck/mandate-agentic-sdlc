using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;

namespace Mandate.Orchestrator.Tests.Support;

/// <summary>
/// Small workflows, each isolating one scheduler behaviour.
/// </summary>
/// <remarks>
/// Deliberately minimal rather than realistic. A test that fails against the shipped
/// eleven-node lifecycle tells you something is wrong; a test that fails against a two-node
/// fixture tells you what.
/// </remarks>
internal static class WorkflowFixtures
{
    private static NodeId Id(string value) => NodeId.Parse(value);

    public static WorkflowNode Node(
        string id,
        SdlcStage stage = SdlcStage.Implementation,
        AutonomyLevel autonomy = AutonomyLevel.ActInSandbox,
        JoinPolicy join = JoinPolicy.All,
        IEnumerable<GateCondition>? entryGate = null,
        IEnumerable<GateCondition>? exitGate = null,
        IEnumerable<ApprovalRequirement>? approvals = null,
        IEnumerable<ArtifactKind>? produces = null,
        IEnumerable<string>? producesContext = null,
        string? agent = null) =>
        new(
            Id(id),
            stage,
            Agent: agent ?? $"{id}-agent",
            Description: $"Fixture stage {id}.",
            EntryGate: entryGate?.ToImmutableArray() ?? [],
            ExitGate: exitGate?.ToImmutableArray()
                      ?? [new GateCondition("artifact-exists", "source-patch", "Produced a change.")],
            Retry: RetryPolicy.None,
            Autonomy: autonomy,
            Approvals: approvals?.ToImmutableArray() ?? [],
            Join: join,
            QuorumSize: 0,
            Timeout: TimeSpan.FromSeconds(30),
            Compensation: "revert-node-commit",
            Model: "claude-sonnet-5",
            Produces: produces?.ToImmutableArray() ?? [ArtifactKind.SourcePatch],
            ProducesContext: producesContext?.ToImmutableArray() ?? [$"{id}.done"]);

    public static WorkflowDefinition Definition(
        IEnumerable<WorkflowNode> nodes, IEnumerable<WorkflowEdge> edges) =>
        new("fixture", "v1", "Scheduler fixture.", [.. nodes], [.. edges]);

    /// <summary>start -> (left | right) -> join, with an 'all' barrier.</summary>
    public static WorkflowDefinition FanOutFanIn() => Definition(
        [Node("start"), Node("left"), Node("right"), Node("join")],
        [
            WorkflowEdge.Forward(Id("start"), Id("left")),
            WorkflowEdge.Forward(Id("start"), Id("right")),
            WorkflowEdge.Forward(Id("left"), Id("join")),
            WorkflowEdge.Forward(Id("right"), Id("join")),
        ]);

    /// <summary>start -> six independent stages, for exercising parallel appends.</summary>
    public static WorkflowDefinition WideFanOut()
    {
        IEnumerable<string> leaves = ["one", "two", "three", "four", "five", "six"];

        return Definition(
            [Node("start"), .. leaves.Select(leaf => Node(leaf))],
            [.. leaves.Select(leaf => WorkflowEdge.Forward(Id("start"), Id(leaf)))]);
    }

    /// <summary>A guarded path and a stage downstream of it, to show exclusion propagating.</summary>
    public static WorkflowDefinition GuardedChain() => Definition(
        [Node("start"), Node("conditional"), Node("after")],
        [
            WorkflowEdge.Guarded(
                Id("start"), Id("conditional"), "run.has-existing-code == true"),
            WorkflowEdge.Forward(Id("conditional"), Id("after")),
        ]);

    /// <summary>
    /// Two mutually exclusive inbound paths converging on a stage that joins on 'any'.
    /// </summary>
    public static WorkflowDefinition ExclusiveBranches() => Definition(
        [Node("start"), Node("via-analysis"), Node("direct"), Node("design", join: JoinPolicy.Any)],
        [
            WorkflowEdge.Guarded(Id("start"), Id("via-analysis"), "run.has-existing-code == true"),
            WorkflowEdge.Guarded(Id("start"), Id("direct"), "run.has-existing-code == false"),
            WorkflowEdge.Forward(Id("via-analysis"), Id("design")),
            WorkflowEdge.Forward(Id("direct"), Id("design")),
        ]);

    /// <summary>A stage whose entry gate demands evidence nothing produces.</summary>
    public static WorkflowDefinition ImpossibleEntryGate() => Definition(
        [
            Node("start"),
            Node(
                "gated",
                entryGate:
                [
                    new GateCondition(
                        "coverage-at-least", "0.9", "Coverage nothing has measured."),
                ]),
        ],
        [WorkflowEdge.Forward(Id("start"), Id("gated"))]);

    /// <summary>A stage gated on a coverage threshold it reports itself.</summary>
    public static WorkflowDefinition CoverageGated() => Definition(
        [
            Node("start"),
            Node(
                "tester",
                stage: SdlcStage.Testing,
                exitGate:
                [
                    new GateCondition("tests-pass", "", "Tests passed."),
                    new GateCondition("coverage-at-least", "0.75", "Coverage met."),
                ],
                produces: [ArtifactKind.TestReport],
                producesContext: ["test.failures", "test.coverage"]),
        ],
        [WorkflowEdge.Forward(Id("start"), Id("tester"))]);

    /// <summary>A stage whose exit gate requires a human approval.</summary>
    public static WorkflowDefinition RequiresApproval() => Definition(
        [
            Node("start"),
            Node(
                "signed-off",
                autonomy: AutonomyLevel.ProposeOnly,
                exitGate:
                [
                    new GateCondition("artifact-exists", "source-patch", "Produced a change."),
                    new GateCondition("approval-held", "tech-lead", "A human signed off."),
                ],
                approvals:
                [
                    new ApprovalRequirement("tech-lead", "High impact.", SegregationOfDuties: true),
                ]),
        ],
        [WorkflowEdge.Forward(Id("start"), Id("signed-off"))]);

    /// <summary>A stage whose agent throws rather than returning a failure.</summary>
    public static WorkflowDefinition ThrowingStage() => Definition(
        [Node("start", agent: ThrowingStageAgent.AgentId)],
        []);
}
