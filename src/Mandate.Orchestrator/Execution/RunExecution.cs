using System.Collections.Immutable;
using System.Diagnostics;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Decisions;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Core.Workflow.Guards;

namespace Mandate.Orchestrator.Execution;

/// <summary>
/// One run's execution. Created per run, never shared.
/// </summary>
/// <remarks>
/// State changes only ever happen by appending an event and applying the event that came
/// back. Nothing here assigns a node state directly, and every transition passes through
/// <see cref="NodeStateMachine"/>, so an illegal transition throws rather than being recorded.
/// </remarks>
internal sealed class RunExecution(
    WorkflowGraph graph,
    IStageAgentRegistry agents,
    IGateEvaluatorRegistry gates,
    IRunJournal journal,
    IClock clock,
    EngineOptions options,
    RunRequest request) : IDisposable
{
    private static readonly NodeId EngineNode = NodeId.Parse(WorkflowContextKeys.EngineNodeId);

    // Serialises appends so that concurrently executing nodes cannot interleave and break the
    // hash chain, and so the projected state is only ever advanced one event at a time.
    private readonly SemaphoreSlim _journalLock = new(1, 1);

    private readonly Dictionary<(NodeId From, NodeId To), bool> _guardVerdicts = [];

    // The last entry-gate verdict per node. An entry gate states a precondition, so failing
    // one means "not yet" rather than "broken": the node stays Pending and is reconsidered as
    // the run progresses. Keeping the last verdict lets the log record a change of verdict
    // without re-recording the same failure on every scheduling pass.
    private readonly Dictionary<NodeId, string> _unmetPreconditions = [];

    private RunState _state = RunState.Empty(request.Id);
    private RunEvent? _tail;

    public async Task<RunOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        await RecordPlanAsync(cancellationToken).ConfigureAwait(false);
        await SeedRunContextAsync(cancellationToken).ConfigureAwait(false);

        await AppendAsync(
            RunEventKind.RunStarted, null, Actor.Engine,
            new RunStartedPayload(Resumed: false), cancellationToken).ConfigureAwait(false);

        await ScheduleUntilQuiescentAsync(cancellationToken).ConfigureAwait(false);
        await BlockUnmetPreconditionsAsync(cancellationToken).ConfigureAwait(false);

        (RunStatus status, string reason) = Conclude();

        await AppendAsync(
            RunEventKind.RunCompleted, null, Actor.Engine,
            new RunCompletedPayload(status.ToString(), reason), cancellationToken)
            .ConfigureAwait(false);

        return new RunOutcome(request.Id, _state.Status, reason, _state, _state.LastSequence);
    }

    // ---- scheduling ----

    private async Task ScheduleUntilQuiescentAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool progressed = await ResolveGuardsAsync(cancellationToken).ConfigureAwait(false);

            (ImmutableArray<WorkflowNode> eligible, bool skipped) =
                await ClassifyPendingAsync(cancellationToken).ConfigureAwait(false);

            progressed |= skipped;

            if (eligible.IsEmpty)
            {
                // Nothing can start. If nothing changed either, the run has gone as far as it
                // can: every node is settled, or the rest are waiting on a human.
                if (!progressed)
                {
                    return;
                }

                continue;
            }

            (ImmutableArray<WorkflowNode> runnable, bool gateProgress) =
                await ApplyEntryGatesAsync(eligible, cancellationToken).ConfigureAwait(false);

            progressed |= gateProgress;

            if (runnable.IsEmpty)
            {
                // Every eligible node is waiting on a precondition. If nothing else changed,
                // nothing will: the run has gone as far as it can without a human.
                if (!progressed)
                {
                    return;
                }

                continue;
            }

            await ExecuteConcurrentlyAsync(runnable, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Evaluates the guard on any conditional path whose source has settled.
    /// </summary>
    /// <remarks>
    /// Deferred until the source settles, because a guard reads context and the context a
    /// stage contributes is not complete until that stage has finished. Each guard is judged
    /// once and the verdict recorded, so a branch not taken is explained in the audit log
    /// rather than simply absent from it.
    /// </remarks>
    private async Task<bool> ResolveGuardsAsync(CancellationToken cancellationToken)
    {
        bool recorded = false;

        foreach (WorkflowEdge edge in graph.Definition.Edges)
        {
            if (edge.Kind != EdgeKind.Forward || !edge.IsConditional)
            {
                continue;
            }

            if (_guardVerdicts.ContainsKey((edge.From, edge.To))
                || _state.StateOf(edge.From) != NodeState.Succeeded)
            {
                continue;
            }

            GuardEvaluation verdict = GuardExpression.Parse(edge.Guard!).Evaluate(_state.GuardValues);
            _guardVerdicts[(edge.From, edge.To)] = verdict.Value;

            await AppendAsync(
                RunEventKind.EdgeGuardEvaluated,
                edge.From,
                Actor.Engine,
                new EdgeGuardEvaluatedPayload(
                    edge.From.Value, edge.To.Value, edge.Guard!, verdict.Value, verdict.Explanation),
                cancellationToken).ConfigureAwait(false);

            recorded = true;
        }

        return recorded;
    }

    private async Task<(ImmutableArray<WorkflowNode> Eligible, bool Skipped)> ClassifyPendingAsync(
        CancellationToken cancellationToken)
    {
        ImmutableArray<WorkflowNode>.Builder eligible = ImmutableArray.CreateBuilder<WorkflowNode>();
        bool skipped = false;

        foreach (NodeId id in graph.TopologicalOrder)
        {
            if (_state.StateOf(id) != NodeState.Pending)
            {
                continue;
            }

            WorkflowNode node = graph.Node(id);

            ImmutableArray<(WorkflowEdge Edge, PathState State)> inbound =
            [
                .. graph.ForwardDependenciesOf(id).Select(edge => (
                    Edge: edge,
                    State: NodeEligibility.Classify(_state.StateOf(edge.From), GuardVerdict(edge)))),
            ];

            switch (NodeEligibility.Assess(node, inbound))
            {
                case Eligibility.Eligible:
                    eligible.Add(node);
                    break;

                case Eligibility.Skipped:
                    await TransitionAsync(
                        node.Id,
                        NodeState.Skipped,
                        Actor.Engine,
                        DescribeSkip(node, inbound),
                        cancellationToken).ConfigureAwait(false);
                    skipped = true;
                    break;

                case Eligibility.Waiting:
                default:
                    break;
            }
        }

        return (eligible.ToImmutable(), skipped);
    }

    private bool? GuardVerdict(WorkflowEdge edge) =>
        !edge.IsConditional
            ? null
            : _guardVerdicts.TryGetValue((edge.From, edge.To), out bool taken) ? taken : null;

    private static string DescribeSkip(
        WorkflowNode node, ImmutableArray<(WorkflowEdge Edge, PathState State)> inbound)
    {
        IEnumerable<string> excluded = inbound
            .Where(path => path.State == PathState.Excluded)
            .Select(path => path.Edge.IsConditional
                ? $"'{path.Edge.From}' (guard false)"
                : $"'{path.Edge.From}' (skipped)");

        return $"Join policy '{node.Join}' cannot be satisfied: "
               + string.Join(", ", excluded)
               + ". The stage is not required on this path.";
    }

    /// <summary>
    /// Evaluates entry gates, returning the nodes cleared to run.
    /// </summary>
    /// <remarks>
    /// An entry gate is a precondition, not a judgment on the node. A node whose entry gate
    /// does not hold stays <see cref="NodeState.Pending"/> and is reconsidered later, because
    /// the thing it is waiting for may still arrive — a clarification being answered, an
    /// upstream stage recording the evidence this one needs. Marking it blocked on the first
    /// pass would abandon work that was merely early.
    /// </remarks>
    private async Task<(ImmutableArray<WorkflowNode> Runnable, bool Progressed)> ApplyEntryGatesAsync(
        ImmutableArray<WorkflowNode> eligible, CancellationToken cancellationToken)
    {
        ImmutableArray<WorkflowNode>.Builder runnable = ImmutableArray.CreateBuilder<WorkflowNode>();
        bool progressed = false;

        foreach (WorkflowNode node in eligible)
        {
            GateResult gate = await EvaluateGateAsync(
                node, GatePosition.Entry, node.EntryGate, cancellationToken).ConfigureAwait(false);

            if (gate.Passed)
            {
                await AppendAsync(
                    RunEventKind.EntryGateEvaluated, node.Id, Actor.Engine,
                    ToPayload(gate), cancellationToken).ConfigureAwait(false);

                _unmetPreconditions.Remove(node.Id);

                await TransitionAsync(
                    node.Id, NodeState.Ready, Actor.Engine, "Entry gate passed.", cancellationToken)
                    .ConfigureAwait(false);

                runnable.Add(node);
                progressed = true;
                continue;
            }

            // Record the failure the first time, and again only if the reason changes. The
            // scheduler revisits pending nodes on every pass; re-recording an unchanged verdict
            // would bury the run's real history under repetition.
            bool changed = !_unmetPreconditions.TryGetValue(node.Id, out string? previous)
                           || !string.Equals(previous, gate.Summary, StringComparison.Ordinal);

            if (changed)
            {
                _unmetPreconditions[node.Id] = gate.Summary;

                await AppendAsync(
                    RunEventKind.EntryGateEvaluated, node.Id, Actor.Engine,
                    ToPayload(gate), cancellationToken).ConfigureAwait(false);

                progressed = true;
            }
        }

        return (runnable.ToImmutable(), progressed);
    }

    /// <summary>
    /// Converts nodes still waiting on an unmet precondition into blocked nodes.
    /// </summary>
    /// <remarks>
    /// Called once the run can make no further progress. At that point a precondition that has
    /// not been met will not be met without a human act, which is exactly what
    /// <see cref="NodeState.Blocked"/> means. Nodes still pending because an upstream stage has
    /// not finished are left alone: they were never early, they were simply not reached.
    /// </remarks>
    private async Task BlockUnmetPreconditionsAsync(CancellationToken cancellationToken)
    {
        foreach ((NodeId nodeId, string reason) in _unmetPreconditions
                     .OrderBy(entry => entry.Key.Value, StringComparer.Ordinal))
        {
            if (_state.StateOf(nodeId) != NodeState.Pending)
            {
                continue;
            }

            await TransitionAsync(
                nodeId, NodeState.Blocked, Actor.Engine, reason, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ExecuteConcurrentlyAsync(
        ImmutableArray<WorkflowNode> runnable, CancellationToken cancellationToken)
    {
        using SemaphoreSlim slots = new(options.MaxConcurrency, options.MaxConcurrency);

        IEnumerable<Task> executions = runnable.Select(async node =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await ExecuteNodeAsync(node, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        });

        await Task.WhenAll(executions).ConfigureAwait(false);
    }

    // ---- node execution ----

    private async Task ExecuteNodeAsync(WorkflowNode node, CancellationToken cancellationToken)
    {
        Actor actor = Actor.Agent(node.Agent);
        int attempt = _state.Nodes[node.Id].AttemptsMade + 1;

        await TransitionAsync(
            node.Id, NodeState.Running, Actor.Engine,
            $"Attempt {attempt} of {node.Retry.MaxAttempts}.", cancellationToken)
            .ConfigureAwait(false);

        await AppendAsync(
            RunEventKind.NodeAttemptStarted, node.Id, Actor.Engine,
            new NodeAttemptStartedPayload(
                attempt, node.Agent, node.Model, node.Autonomy.ToString()),
            cancellationToken).ConfigureAwait(false);

        long startedTicks = Stopwatch.GetTimestamp();
        StageResult result = await InvokeAgentAsync(node, attempt, actor, cancellationToken)
            .ConfigureAwait(false);
        long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;

        await AppendAsync(
            RunEventKind.NodeAttemptFinished, node.Id, Actor.Engine,
            new NodeAttemptFinishedPayload(
                attempt, result.Succeeded, result.Failure, elapsedMilliseconds),
            cancellationToken).ConfigureAwait(false);

        // Recorded even on failure: a partial result is evidence about what went wrong, and
        // is what compensation needs in order to undo cleanly.
        await RecordProductionAsync(node, actor, result, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await TransitionAsync(
                node.Id, NodeState.Failed, Actor.Engine,
                result.Failure ?? "The stage reported failure without a reason.",
                cancellationToken).ConfigureAwait(false);

            return;
        }

        await ApplyExitGateAsync(node, actor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StageResult> InvokeAgentAsync(
        WorkflowNode node, int attempt, Actor actor, CancellationToken cancellationToken)
    {
        IStageAgent agent = agents.Resolve(node.Agent)
                            ?? throw new EngineConfigurationException(
                                $"Agent '{node.Agent}' disappeared from the registry mid-run.");

        StageExecution execution = new(
            request.Id,
            node,
            attempt,
            _state.Context.ScopedTo(ContextScopeFor(node)),
            InputsFor(node),
            actor);

        try
        {
            using CancellationTokenSource timeout = new(node.Timeout);
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            return await agent.ExecuteAsync(execution, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The node's own timeout, not an operator stop: a bounded failure, not a crash.
            return StageResult.Failed(
                $"The stage exceeded its {node.Timeout.TotalMinutes:0.##} minute timeout.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An agent that throws is a failed stage, not a failed engine. Recording it as a
            // stage failure keeps it inside the lifecycle's own recovery machinery.
            return StageResult.Failed($"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task ApplyExitGateAsync(
        WorkflowNode node, Actor producer, CancellationToken cancellationToken)
    {
        GateResult gate = await EvaluateGateAsync(
            node, GatePosition.Exit, node.ExitGate, cancellationToken).ConfigureAwait(false);

        await AppendAsync(
            RunEventKind.ExitGateEvaluated, node.Id, Actor.Engine,
            ToPayload(gate), cancellationToken).ConfigureAwait(false);

        if (gate.Passed)
        {
            await TransitionAsync(
                node.Id, NodeState.Succeeded, producer, "Exit gate passed.", cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        // A gate that fails only because a human has not signed off is not a failure: the work
        // is done and waiting. Anything else is a genuine failure of the stage's output.
        ImmutableArray<GateConditionVerdict> failures = [.. gate.Failures];
        bool awaitingApprovalOnly = failures.All(failure =>
            string.Equals(failure.Condition.Kind, ApprovalHeldKind, StringComparison.Ordinal));

        if (awaitingApprovalOnly && node.RequiresApproval)
        {
            await RequestApprovalsAsync(node, producer, cancellationToken).ConfigureAwait(false);

            await TransitionAsync(
                node.Id, NodeState.AwaitingApproval, Actor.Engine,
                "Work complete; awaiting human approval.", cancellationToken).ConfigureAwait(false);

            return;
        }

        await TransitionAsync(
            node.Id, NodeState.Failed, Actor.Engine, gate.Summary, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RequestApprovalsAsync(
        WorkflowNode node, Actor producer, CancellationToken cancellationToken)
    {
        foreach (ApprovalRequirement approval in node.Approvals)
        {
            if (_state.HeldApprovals.ContainsKey(approval.Role))
            {
                continue;
            }

            await AppendAsync(
                RunEventKind.ApprovalRequested, node.Id, Actor.Engine,
                new ApprovalRequestedPayload(
                    approval.Role,
                    approval.Reason,
                    approval.SegregationOfDuties,
                    // Recorded so that whoever approves can be checked against whoever
                    // produced the work, rather than that check depending on memory.
                    producer.Value),
                cancellationToken).ConfigureAwait(false);
        }
    }

    // ---- gates ----

    private const string ApprovalHeldKind = "approval-held";

    private async Task<GateResult> EvaluateGateAsync(
        WorkflowNode node,
        GatePosition position,
        ImmutableArray<GateCondition> conditions,
        CancellationToken cancellationToken)
    {
        ImmutableArray<GateConditionVerdict>.Builder verdicts =
            ImmutableArray.CreateBuilder<GateConditionVerdict>(conditions.Length);

        foreach (GateCondition condition in conditions)
        {
            IGateEvaluator evaluator = gates.Resolve(condition.Kind)
                                       ?? throw new EngineConfigurationException(
                                           $"Gate kind '{condition.Kind}' disappeared from the "
                                           + "registry mid-run.");

            GateEvaluation evaluation = new(node, position, condition, _state);

            verdicts.Add(await evaluator.EvaluateAsync(evaluation, cancellationToken)
                .ConfigureAwait(false));
        }

        return new GateResult(position, node.Id, verdicts.ToImmutable());
    }

    private static GateEvaluatedPayload ToPayload(GateResult gate) => new(
        gate.Position.ToString(),
        gate.Passed,
        [
            .. gate.Verdicts.Select(verdict => new GateConditionPayload(
                verdict.Condition.Kind,
                verdict.Condition.Expression,
                verdict.Passed,
                verdict.Explanation)),
        ]);

    // ---- least privilege ----

    /// <summary>
    /// The context keys a stage may read: run-level facts, plus whatever the work it actually
    /// depends on contributed.
    /// </summary>
    /// <remarks>
    /// Derived from the declared graph rather than from a second list someone has to keep in
    /// step. A stage that cannot see unrelated facts cannot develop a hidden dependency on
    /// them, and cannot leak them into a prompt.
    /// </remarks>
    private ImmutableArray<string> ContextScopeFor(WorkflowNode node)
    {
        IEnumerable<string> upstream = graph.TransitiveDependenciesOf(node.Id)
            .SelectMany(id => graph.Node(id).ProducesContext);

        return ["run.*", .. upstream.Distinct().OrderBy(key => key, StringComparer.Ordinal)];
    }

    private ImmutableArray<Artifact> InputsFor(WorkflowNode node)
    {
        ImmutableHashSet<NodeId> upstream = graph.TransitiveDependenciesOf(node.Id);

        return
        [
            .. _state.Artifacts
                .Where(artifact => upstream.Contains(artifact.ProducedByNode)
                                   || artifact.ProducedByNode == EngineNode)
                .OrderBy(artifact => artifact.ProducedAt),
        ];
    }

    // ---- recording ----

    private async Task RecordPlanAsync(CancellationToken cancellationToken)
    {
        RunPlannedPayload payload = new(
            graph.Definition.Name,
            graph.Definition.Version,
            request.Scenario.ToString(),
            request.Request,
            request.HasExistingCode,
            WorkflowEngine.Fingerprint,
            options.MaxConcurrency,
            [.. graph.TopologicalOrder.Select(id => id.Value)]);

        await AppendAsync(RunEventKind.RunPlanned, null, Actor.Engine, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SeedRunContextAsync(CancellationToken cancellationToken)
    {
        (string Key, string Value)[] facts =
        [
            (WorkflowContextKeys.RunId, request.Id.Value),
            (WorkflowContextKeys.Scenario, request.Scenario.ToString().ToLowerInvariant()),
            (WorkflowContextKeys.Workflow, graph.Definition.Identity),
            (WorkflowContextKeys.InitiatedBy, request.InitiatedBy.Value),
            (WorkflowContextKeys.HasExistingCode,
                request.HasExistingCode ? "true" : "false"),
        ];

        foreach ((string key, string value) in facts)
        {
            await AppendAsync(
                RunEventKind.ContextFactAdded, EngineNode, Actor.Engine,
                new ContextFactAddedPayload(key, value, []), cancellationToken)
                .ConfigureAwait(false);
        }

        foreach ((string key, string value) in request.AdditionalContext)
        {
            await AppendAsync(
                RunEventKind.ContextFactAdded, EngineNode, Actor.Engine,
                new ContextFactAddedPayload(key, value, []), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordProductionAsync(
        WorkflowNode node, Actor actor, StageResult result, CancellationToken cancellationToken)
    {
        foreach (Artifact artifact in result.Artifacts)
        {
            await AppendAsync(
                RunEventKind.ArtifactProduced, node.Id, actor,
                new ArtifactProducedPayload(
                    artifact.Hash.Hex,
                    artifact.Kind.ToString(),
                    artifact.Name,
                    artifact.MediaType,
                    artifact.SizeBytes,
                    [.. artifact.DerivedFrom.Select(hash => hash.Hex)]),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (Decision decision in result.Decisions)
        {
            await AppendAsync(
                RunEventKind.DecisionRecorded, node.Id, actor,
                new DecisionRecordedPayload(
                    decision.Id,
                    decision.Question,
                    [
                        .. decision.Options.Select(option => new DecisionOptionPayload(
                            option.Name, option.Summary, option.RejectedBecause)),
                    ],
                    decision.Chosen.Name,
                    decision.Rationale,
                    decision.Confidence,
                    decision.Authority.ToString(),
                    [.. decision.Evidence.Select(hash => hash.Hex)]),
                cancellationToken).ConfigureAwait(false);
        }

        foreach (ContextFact fact in result.Facts)
        {
            await AppendAsync(
                RunEventKind.ContextFactAdded, node.Id, actor,
                new ContextFactAddedPayload(
                    fact.Key, fact.Value, [.. fact.Evidence.Select(hash => hash.Hex)]),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TransitionAsync(
        NodeId nodeId,
        NodeState to,
        Actor actor,
        string reason,
        CancellationToken cancellationToken)
    {
        NodeState from = _state.StateOf(nodeId);

        // Throws on an illegal transition rather than recording one. The audit log must not be
        // able to contain a state change the governance model forbids.
        NodeStateMachine.Transition(from, to);

        int attempt = _state.Nodes.TryGetValue(nodeId, out NodeExecutionState? node)
            ? node.AttemptsMade
            : 0;

        await AppendAsync(
            RunEventKind.NodeStateChanged, nodeId, actor,
            new NodeStateChangedPayload(from.ToString(), to.ToString(), attempt, reason),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task AppendAsync<TPayload>(
        RunEventKind kind,
        NodeId? nodeId,
        Actor actor,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        await _journalLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            RunEvent appended = await journal.AppendAsync(
                previous => RunEvent.Append(
                    previous ?? _tail,
                    request.Id,
                    clock.UtcNow,
                    kind,
                    nodeId,
                    actor,
                    payload),
                cancellationToken).ConfigureAwait(false);

            _tail = appended;
            _state = _state.Apply(appended);
        }
        finally
        {
            _journalLock.Release();
        }
    }

    // ---- conclusion ----

    private (RunStatus Status, string Reason) Conclude()
    {
        ImmutableArray<NodeExecutionState> nodes = [.. _state.Nodes.Values];

        ImmutableArray<NodeId> failed =
            [.. nodes.Where(node => node.State == NodeState.Failed).Select(node => node.Id)];
        ImmutableArray<NodeId> blocked =
            [.. nodes.Where(node => node.State == NodeState.Blocked).Select(node => node.Id)];
        ImmutableArray<NodeId> awaiting =
            [.. nodes.Where(node => node.State == NodeState.AwaitingApproval).Select(node => node.Id)];

        // Status is the most serious thing that happened; the reason names everything a human
        // would need to act on, because a run stopped by a policy block may also have a
        // signature waiting, and reporting only one of them wastes a round trip.
        ImmutableArray<string> notes =
        [
            .. new[]
            {
                failed.IsEmpty ? null : $"failed at {Name(failed)}: {Detail(failed)}",
                blocked.IsEmpty ? null : $"blocked at {Name(blocked)}: {Detail(blocked)}",
                awaiting.IsEmpty ? null : $"awaiting approval at {Name(awaiting)}",
            }.OfType<string>(),
        ];

        if (!failed.IsEmpty)
        {
            return (RunStatus.Failed, Join(notes));
        }

        if (!blocked.IsEmpty)
        {
            return (RunStatus.Blocked, Join(notes));
        }

        if (!awaiting.IsEmpty)
        {
            return (
                RunStatus.AwaitingApproval,
                Join(notes) + ". The work is complete and preserved; approve to continue.");
        }

        ImmutableArray<NodeId> unsettled =
        [
            .. nodes
                .Where(node => node.State is not (NodeState.Succeeded or NodeState.Skipped))
                .Select(node => node.Id),
        ];

        if (!unsettled.IsEmpty)
        {
            // Defensive: eligibility should always resolve to eligible or skipped. If this ever
            // fires it is an engine defect, and saying so is more useful than reporting success.
            return (
                RunStatus.Failed,
                $"The run stalled with unresolved node(s) {Name(unsettled)}. This is an engine "
                + "defect: every node should resolve to eligible or skipped.");
        }

        int executed = nodes.Count(node => node.State == NodeState.Succeeded);
        int skipped = nodes.Count(node => node.State == NodeState.Skipped);

        return (
            RunStatus.Succeeded,
            $"Completed {executed} stage(s)"
            + (skipped > 0 ? $"; {skipped} not required on this path." : "."));
    }

    /// <summary>Releases the append lock. One execution, one lifetime.</summary>
    public void Dispose() => _journalLock.Dispose();

    private static string Join(ImmutableArray<string> notes) =>
        string.Join("; ", notes);

    private static string Name(ImmutableArray<NodeId> nodes) =>
        string.Join(", ", nodes.Select(node => $"'{node.Value}'"));

    private string Detail(ImmutableArray<NodeId> nodes) =>
        string.Join(
            " | ",
            nodes.Select(node => _state.Nodes[node].Detail ?? "no detail recorded"));
}
