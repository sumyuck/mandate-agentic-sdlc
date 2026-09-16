using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Workflow;

/// <summary>
/// One stage of the lifecycle as a unit of governed execution.
/// </summary>
/// <param name="Id">Unique identifier within the workflow.</param>
/// <param name="Stage">Which lifecycle stage this node performs.</param>
/// <param name="Agent">Identifier of the agent that executes it.</param>
/// <param name="Description">Reviewer-facing statement of what the node is for.</param>
/// <param name="EntryGate">Conditions that must hold before the node may start.</param>
/// <param name="ExitGate">Conditions that must hold before its output is accepted.</param>
/// <param name="Retry">Bounded retry configuration and fallback.</param>
/// <param name="Autonomy">How much the agent may do without a human.</param>
/// <param name="Approvals">Human sign-offs this node requires.</param>
/// <param name="Join">How many inbound paths must succeed for the node to become eligible.</param>
/// <param name="QuorumSize">Required inbound successes when <paramref name="Join"/> is a quorum.</param>
/// <param name="Timeout">Wall-clock limit for one attempt.</param>
/// <param name="Compensation">
/// Identifier of the action that undoes this node's effects, or <see langword="null"/> when
/// the node has no external effect to undo.
/// </param>
/// <param name="Model">
/// Identifier of the model the agent should use, or <see langword="null"/> to take the
/// workflow's default. Declared per node so capability can be matched to the risk of the
/// stage: a stage that interprets an ambiguous requirement warrants a stronger model than one
/// that generates a test file, and the choice is recorded in the audit log either way.
/// </param>
/// <param name="Produces">Artifact kinds the node is expected to produce.</param>
/// <param name="ProducesContext">
/// Context keys this node contributes. Declared so that a guard referring to a key no stage
/// produces is caught when the workflow is loaded, rather than as a stage mysteriously
/// skipped part-way through a run.
/// </param>
public sealed record WorkflowNode(
    NodeId Id,
    SdlcStage Stage,
    string Agent,
    string Description,
    ImmutableArray<GateCondition> EntryGate,
    ImmutableArray<GateCondition> ExitGate,
    RetryPolicy Retry,
    AutonomyLevel Autonomy,
    ImmutableArray<ApprovalRequirement> Approvals,
    JoinPolicy Join,
    int QuorumSize,
    TimeSpan Timeout,
    string? Compensation,
    string? Model,
    ImmutableArray<Artifacts.ArtifactKind> Produces,
    ImmutableArray<string> ProducesContext)
{
    /// <summary>True when the node cannot complete without a human sign-off.</summary>
    public bool RequiresApproval => !Approvals.IsEmpty;

    /// <summary>True when the node declares a way to undo its effects.</summary>
    public bool IsCompensable => !string.IsNullOrWhiteSpace(Compensation);
}

/// <summary>
/// A directed dependency between two nodes.
/// </summary>
/// <param name="From">The upstream node.</param>
/// <param name="To">The downstream node.</param>
/// <param name="Kind">Whether this is a forward dependency or a deliberate loop-back.</param>
/// <param name="Guard">
/// Optional condition controlling whether this path is taken. A node all of whose inbound
/// guards evaluate false is skipped rather than blocked, which is how the brownfield-only
/// branch of the lifecycle is expressed without a second workflow file.
/// </param>
public sealed record WorkflowEdge(NodeId From, NodeId To, EdgeKind Kind, string? Guard)
{
    /// <summary>An unconditional forward dependency.</summary>
    public static WorkflowEdge Forward(NodeId from, NodeId to) =>
        new(from, to, EdgeKind.Forward, Guard: null);

    /// <summary>A forward dependency taken only when <paramref name="guard"/> holds.</summary>
    public static WorkflowEdge Guarded(NodeId from, NodeId to, string guard) =>
        new(from, to, EdgeKind.Forward, guard);

    /// <summary>A deliberate return to an earlier node.</summary>
    public static WorkflowEdge LoopBack(NodeId from, NodeId to) =>
        new(from, to, EdgeKind.LoopBack, Guard: null);

    /// <summary>True when this path is conditional.</summary>
    public bool IsConditional => !string.IsNullOrWhiteSpace(Guard);
}

/// <summary>
/// A versioned, declarative lifecycle definition.
/// </summary>
/// <remarks>
/// This is the plan as data. It is loaded, validated and then interpreted; the engine contains
/// no lifecycle control flow of its own. See docs/adr/0004.
/// </remarks>
/// <param name="Name">Workflow name, such as <c>sdlc</c>.</param>
/// <param name="Version">Version of this definition; part of every run record.</param>
/// <param name="Description">What this lifecycle is for.</param>
/// <param name="Nodes">The stages.</param>
/// <param name="Edges">The dependencies between them.</param>
public sealed record WorkflowDefinition(
    string Name,
    string Version,
    string Description,
    ImmutableArray<WorkflowNode> Nodes,
    ImmutableArray<WorkflowEdge> Edges)
{
    /// <summary>Name and version, as recorded on runs and in reports.</summary>
    public string Identity => $"{Name}@{Version}";
}
