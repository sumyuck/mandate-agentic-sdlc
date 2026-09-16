using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow.Guards;

namespace Mandate.Core.Runs;

/// <summary>Execution state of a single node.</summary>
/// <param name="Id">The node.</param>
/// <param name="State">Its current state.</param>
/// <param name="AttemptsMade">How many execution attempts have been started.</param>
/// <param name="FirstStartedAt">When the node first began executing.</param>
/// <param name="LastTransitionAt">When it last changed state.</param>
/// <param name="Detail">Why it is in this state, where that needs saying.</param>
public sealed record NodeExecutionState(
    NodeId Id,
    NodeState State,
    int AttemptsMade,
    DateTimeOffset? FirstStartedAt,
    DateTimeOffset? LastTransitionAt,
    string? Detail)
{
    /// <summary>A node that has not yet been considered.</summary>
    public static NodeExecutionState Pending(NodeId id) =>
        new(id, NodeState.Pending, 0, null, null, null);

    /// <summary>How long the node has been executing or took to execute.</summary>
    public TimeSpan? Elapsed => FirstStartedAt is null || LastTransitionAt is null
        ? null
        : LastTransitionAt - FirstStartedAt;
}

/// <summary>
/// The current state of a run, as a projection over its audit log.
/// </summary>
/// <remarks>
/// <para>
/// This type is the only place run state is derived, and <see cref="Apply"/> is the only way
/// to change it. The engine never assigns state directly: it appends an event and applies the
/// event that came back. That is what makes resume and replay free — rebuilding a run is
/// folding <see cref="Apply"/> over its persisted events, and it cannot diverge from live
/// execution because it is the same code path.
/// </para>
/// <para>
/// Unrecognised event kinds are ignored rather than rejected, so a log written by a later
/// engine version can still be read by an earlier one. Kinds that carry no state change —
/// approval requests, guard evaluations, retry scheduling — are recorded for the audit trail
/// and correctly change nothing here.
/// </para>
/// </remarks>
public sealed class RunState : IRunView
{
    private RunState(
        RunId runId,
        RunStatus status,
        ImmutableDictionary<NodeId, NodeExecutionState> nodes,
        RunContext context,
        ImmutableArray<Artifact> artifacts,
        ImmutableDictionary<string, Actor> heldApprovals,
        ImmutableDictionary<string, Actor> deniedApprovals,
        ImmutableDictionary<NodeId, Actor> producers,
        ImmutableDictionary<string, Policies.PolicyWaiver> waivers,
        long lastSequence)
    {
        RunId = runId;
        Status = status;
        Nodes = nodes;
        Context = context;
        Artifacts = artifacts;
        HeldApprovals = heldApprovals;
        DeniedApprovals = deniedApprovals;
        Producers = producers;
        Waivers = waivers;
        LastSequence = lastSequence;
    }

    /// <inheritdoc />
    public RunId RunId { get; }

    /// <inheritdoc />
    public RunStatus Status { get; }

    /// <summary>State of every planned node.</summary>
    public ImmutableDictionary<NodeId, NodeExecutionState> Nodes { get; }

    /// <inheritdoc />
    public RunContext Context { get; }

    /// <inheritdoc />
    public ImmutableArray<Artifact> Artifacts { get; }

    /// <inheritdoc />
    public ImmutableDictionary<string, Actor> HeldApprovals { get; }

    /// <inheritdoc />
    public ImmutableDictionary<string, Actor> DeniedApprovals { get; }

    /// <summary>The actor that produced each node's output.</summary>
    public ImmutableDictionary<NodeId, Actor> Producers { get; }

    /// <inheritdoc />
    public ImmutableDictionary<string, Policies.PolicyWaiver> Waivers { get; }

    /// <summary>Sequence number of the last event applied.</summary>
    public long LastSequence { get; }

    /// <summary>An empty state for a run that has no events yet.</summary>
    public static RunState Empty(RunId runId) => new(
        runId,
        RunStatus.Planned,
        ImmutableDictionary<NodeId, NodeExecutionState>.Empty,
        RunContext.Empty,
        [],
        ImmutableDictionary<string, Actor>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<string, Actor>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary<NodeId, Actor>.Empty,
        ImmutableDictionary<string, Policies.PolicyWaiver>.Empty.WithComparers(
            StringComparer.OrdinalIgnoreCase),
        0);

    /// <summary>Rebuilds a run's state by folding over its events.</summary>
    public static RunState Rebuild(RunId runId, IEnumerable<RunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events.Aggregate(Empty(runId), (state, @event) => state.Apply(@event));
    }

    /// <inheritdoc />
    public NodeState StateOf(NodeId nodeId) =>
        Nodes.TryGetValue(nodeId, out NodeExecutionState? node) ? node.State : NodeState.Unknown;

    /// <inheritdoc />
    public Actor? ProducerOf(NodeId nodeId) =>
        Producers.TryGetValue(nodeId, out Actor producer) ? producer : null;

    /// <summary>A guard resolver over this run's current context.</summary>
    public IGuardValueResolver GuardValues => new ContextGuardResolver(Context);

    /// <summary>Artifacts of a given kind.</summary>
    public IEnumerable<Artifact> ArtifactsOfKind(ArtifactKind kind) =>
        Artifacts.Where(artifact => artifact.Kind == kind);

    /// <summary>Applies one event, returning the resulting state.</summary>
    public RunState Apply(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        RunState next = @event.Kind switch
        {
            RunEventKind.RunPlanned => ApplyRunPlanned(@event),
            RunEventKind.RunStarted => With(status: RunStatus.Running),
            RunEventKind.RunCompleted => ApplyRunCompleted(@event),
            RunEventKind.SafeStopCompleted => With(status: RunStatus.SafeStopped),
            RunEventKind.NodeStateChanged => ApplyNodeStateChanged(@event),
            RunEventKind.NodeAttemptStarted => ApplyAttemptStarted(@event),
            RunEventKind.ArtifactProduced => ApplyArtifactProduced(@event),
            RunEventKind.ContextFactAdded => ApplyContextFactAdded(@event),
            RunEventKind.ApprovalGranted => ApplyApprovalGranted(@event),
            RunEventKind.ApprovalDenied => ApplyApprovalDenied(@event),
            RunEventKind.PolicyWaiverGranted => ApplyWaiverGranted(@event),
            RunEventKind.PolicyWaiverDenied => ApplyWaiverDenied(@event),

            // Recorded for the audit trail; they carry no state change of their own.
            _ => this,
        };

        return next.With(lastSequence: @event.Sequence);
    }

    private RunState ApplyRunPlanned(RunEvent @event)
    {
        RunPlannedPayload payload = @event.Payload<RunPlannedPayload>();

        ImmutableDictionary<NodeId, NodeExecutionState> nodes = payload.PlannedNodes
            .Select(NodeId.Parse)
            .ToImmutableDictionary(id => id, NodeExecutionState.Pending);

        RunContext context = RunContext.Empty;
        return With(status: RunStatus.Planned, nodes: nodes, context: context);
    }

    private RunState ApplyRunCompleted(RunEvent @event)
    {
        RunCompletedPayload payload = @event.Payload<RunCompletedPayload>();

        return Enum.TryParse(payload.Status, out RunStatus status)
            ? With(status: status)
            : this;
    }

    private RunState ApplyNodeStateChanged(RunEvent @event)
    {
        if (@event.NodeId is not { } nodeId)
        {
            return this;
        }

        NodeStateChangedPayload payload = @event.Payload<NodeStateChangedPayload>();

        if (!Enum.TryParse(payload.To, out NodeState to))
        {
            return this;
        }

        NodeExecutionState current = Nodes.TryGetValue(nodeId, out NodeExecutionState? existing)
            ? existing
            : NodeExecutionState.Pending(nodeId);

        NodeExecutionState updated = current with
        {
            State = to,
            LastTransitionAt = @event.OccurredAt,
            Detail = string.IsNullOrWhiteSpace(payload.Reason) ? null : payload.Reason,
        };

        ImmutableDictionary<NodeId, Actor> producers = Producers;

        // The actor that took a node to Succeeded is the one whose work an approver must not
        // be, which is what makes the segregation-of-duties rule checkable.
        if (to == NodeState.Succeeded && @event.Actor.Kind == ActorKind.Agent)
        {
            producers = producers.SetItem(nodeId, @event.Actor);
        }

        return With(nodes: Nodes.SetItem(nodeId, updated), producers: producers);
    }

    private RunState ApplyAttemptStarted(RunEvent @event)
    {
        if (@event.NodeId is not { } nodeId)
        {
            return this;
        }

        NodeAttemptStartedPayload payload = @event.Payload<NodeAttemptStartedPayload>();

        NodeExecutionState current = Nodes.TryGetValue(nodeId, out NodeExecutionState? existing)
            ? existing
            : NodeExecutionState.Pending(nodeId);

        NodeExecutionState updated = current with
        {
            AttemptsMade = payload.Attempt,
            FirstStartedAt = current.FirstStartedAt ?? @event.OccurredAt,
            LastTransitionAt = @event.OccurredAt,
        };

        // The producer is recorded when the work starts, not when it succeeds. A node parked
        // for approval has not succeeded yet, and that is exactly when the segregation-of-
        // duties check needs to know who produced the work it is being asked to sign off.
        return With(
            nodes: Nodes.SetItem(nodeId, updated),
            producers: Producers.SetItem(nodeId, Actor.Agent(payload.Agent)));
    }

    private RunState ApplyArtifactProduced(RunEvent @event)
    {
        ArtifactProducedPayload payload = @event.Payload<ArtifactProducedPayload>();

        if (@event.NodeId is not { } nodeId
            || !Sha256Hash.TryParse(payload.Hash, out Sha256Hash hash)
            || !Enum.TryParse(payload.Kind, out ArtifactKind kind))
        {
            return this;
        }

        // Content addressing makes re-applying the same artifact a no-op, which is what lets a
        // replay be idempotent.
        if (Artifacts.Any(existing => existing.Hash == hash))
        {
            return this;
        }

        Artifact artifact = new(
            hash,
            kind,
            payload.Name,
            payload.MediaType,
            payload.SizeBytes,
            nodeId,
            @event.Actor,
            @event.OccurredAt,
            [.. payload.DerivedFrom.Select(Sha256Hash.Parse)]);

        return With(artifacts: Artifacts.Add(artifact));
    }

    private RunState ApplyContextFactAdded(RunEvent @event)
    {
        if (@event.NodeId is not { } nodeId)
        {
            return this;
        }

        ContextFactAddedPayload payload = @event.Payload<ContextFactAddedPayload>();

        ContextFact fact = ContextFact.Create(
            payload.Key,
            payload.Value,
            nodeId,
            @event.Actor,
            @event.OccurredAt,
            payload.Evidence.Select(Sha256Hash.Parse));

        return With(context: Context.Contribute(fact));
    }

    private RunState ApplyApprovalGranted(RunEvent @event)
    {
        ApprovalDecidedPayload payload = @event.Payload<ApprovalDecidedPayload>();

        // Granting clears any earlier refusal: a human who changes their mind should not have
        // to contend with their own previous answer.
        return With(
            heldApprovals: HeldApprovals.SetItem(payload.Role, @event.Actor),
            deniedApprovals: DeniedApprovals.Remove(payload.Role));
    }

    private RunState ApplyWaiverGranted(RunEvent @event)
    {
        PolicyWaiverPayload payload = @event.Payload<PolicyWaiverPayload>();

        return With(waivers: Waivers.SetItem(
            payload.RuleId,
            new Policies.PolicyWaiver(
                payload.RuleId, @event.Actor, payload.Reason, @event.OccurredAt)));
    }

    private RunState ApplyWaiverDenied(RunEvent @event)
    {
        PolicyWaiverPayload payload = @event.Payload<PolicyWaiverPayload>();

        // Refusing a waiver withdraws any earlier one for the same rule, so a later refusal
        // cannot be silently outranked by an older permission.
        return With(waivers: Waivers.Remove(payload.RuleId));
    }

    private RunState ApplyApprovalDenied(RunEvent @event)
    {
        ApprovalDecidedPayload payload = @event.Payload<ApprovalDecidedPayload>();

        return With(
            heldApprovals: HeldApprovals.Remove(payload.Role),
            deniedApprovals: DeniedApprovals.SetItem(payload.Role, @event.Actor));
    }

    private RunState With(
        RunStatus? status = null,
        ImmutableDictionary<NodeId, NodeExecutionState>? nodes = null,
        RunContext? context = null,
        ImmutableArray<Artifact>? artifacts = null,
        ImmutableDictionary<string, Actor>? heldApprovals = null,
        ImmutableDictionary<string, Actor>? deniedApprovals = null,
        ImmutableDictionary<NodeId, Actor>? producers = null,
        ImmutableDictionary<string, Policies.PolicyWaiver>? waivers = null,
        long? lastSequence = null) =>
        new(
            RunId,
            status ?? Status,
            nodes ?? Nodes,
            context ?? Context,
            artifacts ?? Artifacts,
            heldApprovals ?? HeldApprovals,
            deniedApprovals ?? DeniedApprovals,
            producers ?? Producers,
            waivers ?? Waivers,
            lastSequence ?? LastSequence);

    private sealed class ContextGuardResolver(RunContext context) : IGuardValueResolver
    {
        public bool TryResolve(string key, out string? value)
        {
            ContextFact? fact = context.Latest(key);
            value = fact?.Value;
            return fact is not null;
        }
    }
}
