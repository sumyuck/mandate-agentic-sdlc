using System.Collections.Immutable;
using Helmsman.Core.Artifacts;
using Helmsman.Core.Identifiers;
using Helmsman.Core.Workflow;

namespace Helmsman.Core.Tests.Support;

internal static class WorkflowFactory
{
    public static WorkflowNode Node(
        string id,
        SdlcStage stage = SdlcStage.Implementation,
        AutonomyLevel autonomy = AutonomyLevel.ActInSandbox,
        RetryPolicy? retry = null,
        string? compensation = "revert-node-commit",
        JoinPolicy join = JoinPolicy.All,
        int quorumSize = 0,
        TimeSpan? timeout = null,
        IEnumerable<ApprovalRequirement>? approvals = null,
        IEnumerable<GateCondition>? exitGate = null) =>
        new(
            NodeId.Parse(id),
            stage,
            Agent: $"{id}-agent",
            Description: $"Performs {id}.",
            EntryGate: [],
            ExitGate: exitGate?.ToImmutableArray()
                      ?? [new GateCondition("artifact-exists", "*", "Produced something.")],
            Retry: retry ?? RetryPolicy.Default,
            Autonomy: autonomy,
            Approvals: approvals?.ToImmutableArray() ?? [],
            Join: join,
            QuorumSize: quorumSize,
            Timeout: timeout ?? TimeSpan.FromMinutes(5),
            Compensation: compensation,
            Produces: [ArtifactKind.SourcePatch]);

    public static WorkflowDefinition Definition(
        IEnumerable<WorkflowNode> nodes,
        IEnumerable<WorkflowEdge> edges,
        string name = "sdlc",
        string version = "v1") =>
        new(name, version, "Test lifecycle.", [.. nodes], [.. edges]);

    /// <summary>
    /// A lifecycle with a genuine fan-out and synchronising join:
    /// requirements -> implement -> (test | review | docs) -> release.
    /// </summary>
    public static WorkflowDefinition FanOutFanIn()
    {
        NodeId requirements = NodeId.Parse("requirements");
        NodeId implement = NodeId.Parse("implement");
        NodeId test = NodeId.Parse("test");
        NodeId review = NodeId.Parse("review");
        NodeId docs = NodeId.Parse("docs");
        NodeId release = NodeId.Parse("release");

        return Definition(
            [
                Node("requirements", SdlcStage.Requirements),
                Node("implement", SdlcStage.Implementation),
                Node("test", SdlcStage.Testing),
                Node("review", SdlcStage.CodeReview),
                Node("docs", SdlcStage.Documentation),
                Node(
                    "release",
                    SdlcStage.ReleaseReadiness,
                    autonomy: AutonomyLevel.ProposeOnly,
                    approvals: [new ApprovalRequirement("tech-lead", "Release is irreversible.", true)]),
            ],
            [
                WorkflowEdge.Forward(requirements, implement),
                WorkflowEdge.Forward(implement, test),
                WorkflowEdge.Forward(implement, review),
                WorkflowEdge.Forward(implement, docs),
                WorkflowEdge.Forward(test, release),
                WorkflowEdge.Forward(review, release),
                WorkflowEdge.Forward(docs, release),
                WorkflowEdge.LoopBack(test, implement),
            ]);
    }
}
