using System.Collections.Immutable;
using System.Diagnostics;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Decisions;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Llm;
using Mandate.Core.Policies;
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
    RunRequest request,
    ICompensationRegistry compensations,
    IRunWorkspaceFactory workspaces,
    IDelay delay,
    ISafeStopMonitor safeStop,
    IPolicyEngine? policies = null,
    ResumePoint? resumeFrom = null) : IDisposable
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

    private RunState _state = resumeFrom?.State ?? RunState.Empty(request.Id);
    private RunEvent? _tail = resumeFrom?.Tail;
    private IRunWorkspace _workspace = null!;
    private readonly Dictionary<NodeId, ImmutableHashSet<Sha256Hash>> _lastOutputs = [];

    // Re-plans are queued while stages are executing and applied between scheduling passes.
    // Invalidating a node's downstream work while that work might be running would race with
    // it; the boundary between passes is the only point where the run's state is coherent.
    private readonly List<ReplanRequest> _pendingReplans = [];
    private NodeId? _rollbackTrigger;
    private bool _safeStopped;

    public async Task<RunOutcome> ExecuteAsync(CancellationToken cancellationToken)
    {
        using Activity? runSpan = RunActivity.StartRun(
            request.Id, graph.Definition.Identity, request.Scenario.ToString());

        bool resuming = resumeFrom is not null;

        if (!resuming)
        {
            await RecordPlanAsync(cancellationToken).ConfigureAwait(false);
            await SeedRunContextAsync(cancellationToken).ConfigureAwait(false);
        }

        await AppendAsync(
            RunEventKind.RunStarted, null, Actor.Engine,
            new RunStartedPayload(resuming), cancellationToken).ConfigureAwait(false);

        _workspace = await workspaces.CreateAsync(request.Id, cancellationToken)
            .ConfigureAwait(false);

        if (resuming)
        {
            // What each node produced before is read back from the recorded artifacts. Without
            // it a resumed run could not tell a re-run that changed something from one that
            // changed nothing, and would either redo everything or nothing.
            foreach (IGrouping<NodeId, Artifact> produced in
                     _state.Artifacts.GroupBy(artifact => artifact.ProducedByNode))
            {
                _lastOutputs[produced.Key] =
                    [.. produced.Select(artifact => artifact.Hash)];
            }

            // A node blocked on an unmet precondition gets another look. The precondition may
            // be exactly what the human just resolved, and leaving it blocked would make the
            // resume pointless for the stage it was meant to unblock.
            await ReconsiderBlockedPreconditionsAsync(cancellationToken).ConfigureAwait(false);

            // An amendment is a statement that work already done was based on the wrong
            // input, so it is applied before anything else: approving or building on that
            // work first would be acting on a premise the requester has withdrawn.
            await ApplyAmendmentsAsync(cancellationToken).ConfigureAwait(false);

            // An approval that arrived while the run was parked has to be acted on before any
            // scheduling: the nodes it unblocks are the reason the run is being resumed.
            await ReconcileApprovalsAsync(cancellationToken).ConfigureAwait(false);
        }

        await ScheduleUntilQuiescentAsync(cancellationToken).ConfigureAwait(false);

        if (_rollbackTrigger is { } trigger)
        {
            await RollBackRunAsync(trigger, cancellationToken).ConfigureAwait(false);
        }

        await BlockUnmetPreconditionsAsync(cancellationToken).ConfigureAwait(false);

        (RunStatus status, string reason) = Conclude();

        await AppendAsync(
            RunEventKind.RunCompleted, null, Actor.Engine,
            new RunCompletedPayload(status.ToString(), reason), cancellationToken)
            .ConfigureAwait(false);

        return new RunOutcome(request.Id, _state.Status, reason, _state, _state.LastSequence);
    }

    /// <summary>
    /// Returns nodes blocked on an unmet precondition to the plan, so their gates run again.
    /// </summary>
    private async Task ReconsiderBlockedPreconditionsAsync(CancellationToken cancellationToken)
    {
        foreach (NodeId nodeId in graph.TopologicalOrder)
        {
            if (_state.StateOf(nodeId) != NodeState.Blocked)
            {
                continue;
            }

            string? detail = _state.Nodes[nodeId].Detail;

            // Only precondition blocks. A node blocked by a policy violation or a refused
            // approval needs the underlying decision changed, not another attempt.
            if (detail is null || !detail.Contains("Entry gate on", StringComparison.Ordinal))
            {
                continue;
            }

            await TransitionAsync(
                nodeId, NodeState.Ready, Actor.Engine,
                "Resumed; the entry gate is evaluated again.", cancellationToken)
                .ConfigureAwait(false);

            await TransitionAsync(
                nodeId, NodeState.Invalidated, Actor.Engine,
                "Returned to the plan so its precondition is re-checked.", cancellationToken)
                .ConfigureAwait(false);

            await TransitionAsync(
                nodeId, NodeState.Pending, Actor.Engine,
                "Awaiting re-evaluation.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies amendments a human recorded while the run was stopped.
    /// </summary>
    /// <remarks>
    /// An amendment is applied once. Whether it already has been is read from the log: a
    /// re-plan recorded after the amendment, for the same stage, is the evidence that it was
    /// acted on. Checking the node's current state would not do — by the time the run
    /// finishes, the stage has been redone and is settled again, and a second resume would
    /// redo it a second time.
    /// </remarks>
    private async Task ApplyAmendmentsAsync(CancellationToken cancellationToken)
    {
        if (resumeFrom is not { } resume)
        {
            return;
        }

        foreach (RunEvent amendment in resume.Events.Where(
                     @event => @event.Kind == RunEventKind.RunAmended))
        {
            if (amendment.NodeId is not { } nodeId || !IsSettled(_state.StateOf(nodeId)))
            {
                continue;
            }

            bool alreadyApplied = resume.Events.Any(@event =>
                @event.Kind == RunEventKind.ReplanPerformed
                && @event.Sequence > amendment.Sequence
                && @event.Payload<ReplanPayload>().Trigger == nodeId.Value);

            if (alreadyApplied)
            {
                continue;
            }

            AmendmentPayload payload = amendment.Payload<AmendmentPayload>();

            // Contributed as a run-level fact so the stage being redone can see what changed.
            // Redoing a stage while withholding the reason it is being redone would produce
            // the same output and make the whole exercise pointless.
            await AppendAsync(
                RunEventKind.ContextFactAdded, EngineNode, amendment.Actor,
                new ContextFactAddedPayload(
                    WorkflowContextKeys.Amendment, payload.Reason, []),
                cancellationToken).ConfigureAwait(false);

            await ReplanAsync(
                trigger: nodeId,
                reason: $"{amendment.Actor} amended the input to '{nodeId}': {payload.Reason}",
                toInvalidate: [nodeId],
                by: amendment.Actor,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Completes or blocks nodes whose approval arrived while the run was parked.
    /// </summary>
    /// <remarks>
    /// The exit gate is re-evaluated, but the stage is not re-run. The work was finished
    /// before the approval was sought, and re-executing on the strength of a signature would
    /// mean the human approved something other than what ships.
    /// </remarks>
    /// <summary>
    /// Puts what an approving human said into the run's context.
    /// </summary>
    /// <remarks>
    /// An approval note is the human's answer, and the clarification stage exists to ask
    /// for one. Recorded in the audit log it satisfies accountability; put into the context
    /// it also reaches the stage that re-runs on the loop-back, which is the only way the
    /// answer can change anything. Accumulated rather than replaced, because a second round
    /// of questions does not retract the first round's answer.
    /// </remarks>
    private async Task RecordApprovalNotesAsync(
        WorkflowNode node, CancellationToken cancellationToken)
    {
        ImmutableArray<RunEvent> history =
            await journal.ReadAsync(request.Id, cancellationToken).ConfigureAwait(false);

        foreach (RunEvent granted in history
            .Where(entry => entry.Kind == RunEventKind.ApprovalGranted)
            .Where(entry => entry.NodeId == node.Id))
        {
            ApprovalDecidedPayload payload = granted.Payload<ApprovalDecidedPayload>();

            if (string.IsNullOrWhiteSpace(payload.Note))
            {
                continue;
            }

            string line = $"{granted.Actor} on '{node.Id}' ({payload.Role}): {payload.Note.Trim()}";
            string existing = _state.Context.Latest(WorkflowContextKeys.ApprovalNotes)?.Value ?? string.Empty;

            if (existing.Contains(line, StringComparison.Ordinal))
            {
                continue;
            }

            await AppendAsync(
                RunEventKind.ContextFactAdded, EngineNode, Actor.Engine,
                new ContextFactAddedPayload(
                    WorkflowContextKeys.ApprovalNotes,
                    existing.Length == 0 ? line : existing + "\n" + line,
                    []),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReconcileApprovalsAsync(CancellationToken cancellationToken)
    {
        foreach (NodeId nodeId in graph.TopologicalOrder)
        {
            if (_state.StateOf(nodeId) != NodeState.AwaitingApproval)
            {
                continue;
            }

            WorkflowNode node = graph.Node(nodeId);

            GateResult gate = await EvaluateGateAsync(
                node, GatePosition.Exit, node.ExitGate, cancellationToken).ConfigureAwait(false);

            await AppendAsync(
                RunEventKind.ExitGateEvaluated, nodeId, Actor.Engine,
                ToPayload(gate), cancellationToken).ConfigureAwait(false);

            if (gate.Passed)
            {
                await RecordApprovalNotesAsync(node, cancellationToken).ConfigureAwait(false);

                Actor producer = _state.ProducerOf(nodeId) ?? Actor.Agent(node.Agent);

                await TransitionAsync(
                    nodeId, NodeState.Succeeded, producer,
                    "Approved; exit gate passed without re-running the stage.", cancellationToken)
                    .ConfigureAwait(false);

                // A stage completing on resume has the same consequences as one completing
                // during a run. Returning control along a loop-back is the whole point of the
                // clarification stage, and it must not depend on which code path finished it.
                QueueCascadeIfOutputChanged(graph.Node(nodeId));
                QueueLoopBacks(graph.Node(nodeId), LoopBackTrigger.OnSuccess);

                continue;
            }

            bool denied = node.Approvals.Any(approval =>
                _state.DeniedApprovals.ContainsKey(approval.Role));

            if (denied)
            {
                await TransitionAsync(
                    nodeId, NodeState.Blocked, Actor.Engine, gate.Summary, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Otherwise the node stays parked: the approval it needs has not arrived, and
            // resuming is not the same as approving.
        }
    }

    // ---- scheduling ----

    private async Task ScheduleUntilQuiescentAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Checked between passes, not mid-stage: stopping cleanly means stopping where the
            // run's state is coherent. Interrupting a stage half-way would leave the workspace
            // in a condition no node declared.
            if (await safeStop.IsStopRequestedAsync(request.Id, cancellationToken)
                    .ConfigureAwait(false))
            {
                await SafeStopAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (_rollbackTrigger is not null)
            {
                // A node has asked for the run to be undone. Stop scheduling new work rather
                // than racing compensation against stages still starting.
                return;
            }

            bool progressed = await DrainReplansAsync(cancellationToken).ConfigureAwait(false);
            progressed |= await ResolveGuardsAsync(cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Runs one node, retrying within its declared budget and applying its fallback when the
    /// budget is spent.
    /// </summary>
    /// <remarks>
    /// Retries loop here rather than going back through the scheduler, so a node's attempts
    /// stay a property of that node. Each attempt is a real state transition — Running to
    /// Failed to Ready to Running — so the audit log shows the retries as retries, and the
    /// state machine refuses any path the governance model does not allow.
    /// </remarks>
    private async Task ExecuteNodeAsync(WorkflowNode node, CancellationToken cancellationToken)
    {
        Actor actor = Actor.Agent(node.Agent);

        // Carried into the next attempt so a retry can act on what went wrong. Scoped to
        // this node's loop: a failure belongs to the stage that produced it.
        string? previousFailure = null;

        while (true)
        {
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

            using Activity? span = RunActivity.StartAttempt(
                request.Id, node.Id.Value, node.Stage.ToString(), node.Agent, node.Model,
                node.Autonomy.ToString(), attempt);

            StageResult result = await InvokeAgentAsync(
                node, attempt, actor, previousFailure, cancellationToken).ConfigureAwait(false);
            long elapsedMilliseconds = (long)Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;

            RunActivity.RecordOutcome(span, result.Succeeded, result.Failure);

            await AppendAsync(
                RunEventKind.NodeAttemptFinished, node.Id, Actor.Engine,
                new NodeAttemptFinishedPayload(
                    attempt, result.Succeeded, result.Failure, elapsedMilliseconds),
                cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                string? refusal = await TryApplyWorkspaceChangesAsync(
                    node, attempt, actor, result, cancellationToken).ConfigureAwait(false);

                if (refusal is null)
                {
                    await RecordProductionAsync(node, actor, result, cancellationToken)
                        .ConfigureAwait(false);

                    await ApplyExitGateAsync(node, actor, cancellationToken).ConfigureAwait(false);
                    return;
                }

                // The stage said it succeeded, but what it proposed could not be applied. That
                // is a failed stage, not a failed engine: it belongs in the same retry,
                // fallback and compensation machinery as any other failure.
                result = StageResult.Failed(
                    refusal, result.Artifacts, result.Decisions, result.ModelCalls);
            }

            // A failed attempt may have left half-written files. They are not a change any
            // node declared, so they are discarded before anything else happens.
            await _workspace.DiscardUncommittedAsync(cancellationToken).ConfigureAwait(false);

            await RecordProductionAsync(node, actor, result, cancellationToken)
                .ConfigureAwait(false);

            string failure = result.Failure ?? "The stage reported failure without a reason.";
            previousFailure = failure;

            await TransitionAsync(node.Id, NodeState.Failed, Actor.Engine, failure, cancellationToken)
                .ConfigureAwait(false);

            if (!node.Retry.PermitsRetryAfter(attempt))
            {
                await ApplyFallbackAsync(node, attempt, failure, cancellationToken)
                    .ConfigureAwait(false);

                return;
            }

            TimeSpan backoff = WithJitter(node.Retry, node.Retry.BackoffBefore(attempt + 1));

            await AppendAsync(
                RunEventKind.NodeRetryScheduled, node.Id, Actor.Engine,
                new RetryPayload(
                    attempt + 1, node.Retry.MaxAttempts, backoff.TotalSeconds, failure),
                cancellationToken).ConfigureAwait(false);

            await delay.WaitAsync(backoff, cancellationToken).ConfigureAwait(false);

            // Back through Ready, so the retry is visible as one in the log and the state
            // machine gets to refuse it if the governance model ever says it should.
            await TransitionAsync(
                node.Id, NodeState.Ready, Actor.Engine,
                $"Retrying after attempt {attempt}.", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Adds jitter to a backoff.
    /// </summary>
    /// <remarks>
    /// Without it, several stages that failed together would retry together, which is how a
    /// transient upstream problem becomes a repeated thundering herd against the thing that
    /// was already struggling.
    /// </remarks>
    private static TimeSpan WithJitter(RetryPolicy policy, TimeSpan backoff)
    {
        if (backoff <= TimeSpan.Zero || policy.JitterRatio <= 0)
        {
            return backoff;
        }

        double spread = backoff.TotalSeconds * policy.JitterRatio;
        double offset = (Random.Shared.NextDouble() * 2 - 1) * spread;

        return TimeSpan.FromSeconds(Math.Max(0, backoff.TotalSeconds + offset));
    }

    /// <summary>Applies the node's declared fallback once its retry budget is spent.</summary>
    private async Task ApplyFallbackAsync(
        WorkflowNode node, int attempts, string failure, CancellationToken cancellationToken)
    {
        await AppendAsync(
            RunEventKind.NodeRetryBudgetExhausted, node.Id, Actor.Engine,
            new RetryPayload(attempts, node.Retry.MaxAttempts, 0, failure),
            cancellationToken).ConfigureAwait(false);

        // A failure loop-back says the problem is upstream, not here: the stage did its job
        // and reported that the work it was checking does not hold. Redoing that work is a
        // different recovery from compensating this stage, and it is tried first while the
        // re-plan budget allows.
        ImmutableArray<WorkflowEdge> failureLoops =
            graph.LoopBacksFrom(node.Id, LoopBackTrigger.OnFailure);

        if (!failureLoops.IsEmpty && _state.ReplanCount < options.MaxReplans)
        {
            foreach (WorkflowEdge loop in failureLoops)
            {
                lock (_pendingReplans)
                {
                    _pendingReplans.Add(new ReplanRequest(
                        node.Id,
                        $"'{node.Id}' failed after {attempts} attempt(s), so '{loop.To}' is "
                        + $"redone: {failure}",
                        [loop.To],
                        Actor.Engine));
                }
            }

            return;
        }

        FallbackStrategy strategy = node.Retry.OnExhaustion;

        await AppendAsync(
            RunEventKind.NodeFallbackSelected, node.Id, Actor.Engine,
            new FallbackSelectedPayload(
                strategy.ToString(),
                $"'{node.Id}' failed {attempts} attempt(s): {failure}"),
            cancellationToken).ConfigureAwait(false);

        switch (strategy)
        {
            case FallbackStrategy.HumanHandoff:
                // Blocked rather than failed: the run is not beyond saving, it needs someone
                // to look at it. The distinction matters to whoever reads the outcome.
                await TransitionAsync(
                    node.Id, NodeState.Blocked, Actor.Engine,
                    $"Exhausted {attempts} attempt(s) and handed off to a human: {failure}",
                    cancellationToken).ConfigureAwait(false);
                break;

            case FallbackStrategy.Compensate:
                _rollbackTrigger = node.Id;
                break;

            case FallbackStrategy.FailNode:
            default:
                // Already Failed; the run's conclusion reports it.
                break;
        }
    }

    /// <summary>
    /// Undoes the run's effects, newest work first.
    /// </summary>
    /// <remarks>
    /// Reverse topological order because later work was built on earlier work: undoing a
    /// design before the implementation that depends on it would leave the tree in a state
    /// that never existed. Nodes with nothing to undo are left as they are — marking them
    /// rolled back would claim an action that never happened.
    /// </remarks>
    private async Task RollBackRunAsync(NodeId trigger, CancellationToken cancellationToken)
    {
        await AppendAsync(
            RunEventKind.NodeCompensationStarted, trigger, Actor.Engine,
            new FallbackSelectedPayload(
                FallbackStrategy.Compensate.ToString(),
                $"'{trigger}' exhausted its retries; undoing the run's effects."),
            cancellationToken).ConfigureAwait(false);

        foreach (NodeId nodeId in graph.TopologicalOrder.Reverse())
        {
            NodeState state = _state.StateOf(nodeId);

            if (state is not (NodeState.Succeeded or NodeState.Failed))
            {
                continue;
            }

            WorkflowNode node = graph.Node(nodeId);

            if (!node.IsCompensable)
            {
                continue;
            }

            ICompensationAction action = compensations.Resolve(node.Compensation!)
                                         ?? throw new EngineConfigurationException(
                                             $"Compensating action '{node.Compensation}' "
                                             + "disappeared from the registry mid-run.");

            await TransitionAsync(
                nodeId, NodeState.Compensating, Actor.Engine,
                $"Undoing with '{action.Id}'.", cancellationToken).ConfigureAwait(false);

            CompensationResult result;

            try
            {
                result = await action
                    .ExecuteAsync(
                        new CompensationContext(request.Id, node, _workspace), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A rollback that cannot complete is a real outcome, not an engine fault.
                // A sibling stage running in parallel may have touched the same files, in
                // which case a clean revert of this node's work does not exist and a person
                // has to decide what the tree should contain. Recorded as an unsuccessful
                // compensation so the run reports it and the node keeps a state the
                // governance model allows — letting it escape would end the run on a stack
                // trace and leave the audit log claiming the rollback was still in progress.
                result = new CompensationResult(
                    Undone: false,
                    Detail: $"Compensation failed: {exception.Message}");
            }

            WorkspaceStatus status = await _workspace.StatusAsync(cancellationToken)
                .ConfigureAwait(false);

            await AppendAsync(
                RunEventKind.WorkspaceReverted, nodeId, Actor.Engine,
                new WorkspaceRevertedPayload(
                    0, status.HeadSha, status.IsClean, result.Detail),
                cancellationToken).ConfigureAwait(false);

            await AppendAsync(
                RunEventKind.NodeCompensationCompleted, nodeId, Actor.Engine,
                new CompensationPayload(action.Id, result.Undone, result.Detail),
                cancellationToken).ConfigureAwait(false);

            await TransitionAsync(
                nodeId,
                result.Undone ? NodeState.RolledBack : NodeState.Failed,
                Actor.Engine,
                result.Detail,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Halts the run at a coherent boundary, preserving what has been done.</summary>
    private async Task SafeStopAsync(CancellationToken cancellationToken)
    {
        await AppendAsync(
            RunEventKind.SafeStopRequested, null, Actor.Engine,
            new SafeStopPayload("operator", 0, "A stop was requested for this run."),
            cancellationToken).ConfigureAwait(false);

        int cancelled = 0;

        foreach (NodeId nodeId in graph.TopologicalOrder)
        {
            // Only work that is still outstanding. A stage that already succeeded did
            // succeed, and relabelling it would make the log state something untrue.
            if (!NodeStateMachine.Cancellable.Contains(_state.StateOf(nodeId)))
            {
                continue;
            }

            await TransitionAsync(
                nodeId, NodeState.Cancelled, Actor.Engine,
                "Cancelled by a safe stop.", cancellationToken).ConfigureAwait(false);

            cancelled++;
        }

        await AppendAsync(
            RunEventKind.SafeStopCompleted, null, Actor.Engine,
            new SafeStopPayload(
                "operator",
                cancelled,
                $"Halted at a safe boundary with {cancelled} node(s) cancelled. Completed work "
                + "is preserved and the run can be inspected."),
            cancellationToken).ConfigureAwait(false);

        _safeStopped = true;
    }

    /// <summary>Applies and commits the files a stage proposed.</summary>
    /// <remarks>
    /// The engine writes, not the agent. Every path is validated against the workspace
    /// boundary before anything touches the disk, which is what makes "the agent may act in
    /// the run workspace" a constraint rather than a description.
    /// </remarks>
    private async Task<string?> TryApplyWorkspaceChangesAsync(
        WorkflowNode node,
        int attempt,
        Actor actor,
        StageResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await ApplyWorkspaceChangesAsync(node, attempt, actor, result, cancellationToken)
                .ConfigureAwait(false);

            return null;
        }
        catch (InvalidOperationException refusal)
        {
            return refusal.Message;
        }
    }

    private async Task ApplyWorkspaceChangesAsync(
        WorkflowNode node,
        int attempt,
        Actor actor,
        StageResult result,
        CancellationToken cancellationToken)
    {
        if (result.Files.IsEmpty)
        {
            return;
        }

        WorkspaceCommit? commit = await _workspace.CommitAsync(
            node.Id,
            attempt,
            result.Files,
            $"{node.Id}: {node.Stage}",
            cancellationToken).ConfigureAwait(false);

        if (commit is null)
        {
            return;
        }

        await AppendAsync(
            RunEventKind.WorkspaceCommitted, node.Id, actor,
            new WorkspaceCommittedPayload(
                commit.Sha,
                attempt,
                commit.FilesChanged,
                [.. result.Files.Select(file => file.RelativePath).Order(StringComparer.Ordinal)]),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<StageResult> InvokeAgentAsync(
        WorkflowNode node,
        int attempt,
        Actor actor,
        string? previousFailure,
        CancellationToken cancellationToken)
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
            actor,
            _workspace.Reader,
            previousFailure);

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

            QueueCascadeIfOutputChanged(node);
            QueueLoopBacks(node, LoopBackTrigger.OnSuccess);

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

    // ---- re-planning ----

    /// <summary>
    /// Invalidates work built on a node whose output has actually changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what makes re-planning incremental rather than a restart. A node that re-runs
    /// and produces byte-identical output has changed nothing, so everything downstream of it
    /// is still valid and is deliberately left alone. Content addressing makes that an exact
    /// comparison rather than a guess.
    /// </para>
    /// <para>
    /// Cascading lazily — after the re-run, not before — is the reason it can be exact. At
    /// the moment someone amends an input, nobody yet knows whether redoing the stage will
    /// change anything.
    /// </para>
    /// </remarks>
    private void QueueCascadeIfOutputChanged(WorkflowNode node)
    {
        ImmutableHashSet<Sha256Hash> produced =
        [
            .. _state.Artifacts
                .Where(artifact => artifact.ProducedByNode == node.Id)
                .Select(artifact => artifact.Hash),
        ];

        if (!_lastOutputs.TryGetValue(node.Id, out ImmutableHashSet<Sha256Hash>? previous))
        {
            _lastOutputs[node.Id] = produced;
            return;
        }

        _lastOutputs[node.Id] = produced;

        if (previous.SetEquals(produced))
        {
            // Re-ran, produced the same bytes. Nothing downstream needs redoing.
            return;
        }

        ImmutableArray<NodeId> dependents =
        [
            .. graph.TransitiveDependentsOf(node.Id)
                .Where(id => IsSettled(_state.StateOf(id)))
                .OrderBy(id => id.Value, StringComparer.Ordinal),
        ];

        if (dependents.IsEmpty)
        {
            return;
        }

        lock (_pendingReplans)
        {
            _pendingReplans.Add(new ReplanRequest(
                node.Id,
                $"'{node.Id}' re-ran and produced different output, so work built on it is no "
                + "longer valid.",
                dependents,
                Actor.Engine));
        }
    }

    /// <summary>
    /// Returns control to an earlier stage along a declared loop-back edge.
    /// </summary>
    /// <remarks>
    /// This is how a clarification re-enters requirements: the answer is in the context, so
    /// the stage that interpreted the requirement is redone against it. Bounded by the run's
    /// re-plan budget, which is what keeps a declared loop from becoming an unbounded one.
    /// </remarks>
    private void QueueLoopBacks(WorkflowNode node, LoopBackTrigger trigger)
    {
        foreach (WorkflowEdge loop in graph.LoopBacksFrom(node.Id, trigger))
        {
            if (!IsSettled(_state.StateOf(loop.To)))
            {
                continue;
            }

            lock (_pendingReplans)
            {
                _pendingReplans.Add(new ReplanRequest(
                    node.Id,
                    $"'{node.Id}' completed and returns control to '{loop.To}', which is re-run "
                    + "against what it produced.",
                    [loop.To],
                    Actor.Engine));
            }
        }
    }

    /// <summary>Applies any re-plans queued while stages were executing.</summary>
    private async Task<bool> DrainReplansAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<ReplanRequest> queued;

        lock (_pendingReplans)
        {
            if (_pendingReplans.Count == 0)
            {
                return false;
            }

            queued = [.. _pendingReplans];
            _pendingReplans.Clear();
        }

        bool changed = false;

        foreach (ReplanRequest request in queued)
        {
            changed |= await ReplanAsync(
                request.Trigger, request.Reason, request.ToInvalidate, request.By, cancellationToken)
                .ConfigureAwait(false);
        }

        return changed;
    }

    /// <summary>A queued request to recompute the plan.</summary>
    private sealed record ReplanRequest(
        NodeId Trigger, string Reason, ImmutableArray<NodeId> ToInvalidate, Actor By);

    /// <summary>
    /// Invalidates a set of nodes and recomputes the plan.
    /// </summary>
    /// <remarks>
    /// Governance is re-applied rather than carried over. An invalidated node's approvals are
    /// withdrawn, because a signature was given for work that is now being redone; letting it
    /// stand would put a human's name against output they never saw.
    /// </remarks>
    private async Task<bool> ReplanAsync(
        NodeId trigger,
        string reason,
        ImmutableArray<NodeId> toInvalidate,
        Actor by,
        CancellationToken cancellationToken)
    {
        if (toInvalidate.IsEmpty)
        {
            return false;
        }

        if (_state.ReplanCount >= options.MaxReplans)
        {
            // Nothing is transitioned. The nodes that would have been redone are not broken —
            // their results stand — it is the loop that has stopped turning, and marking
            // finished work as blocked would misdescribe which thing needs attention.
            await AppendAsync(
                RunEventKind.ReplanRefused, trigger, Actor.Engine,
                new ReplanPayload(
                    _state.ReplanCount,
                    trigger.Value,
                    $"The run's budget of {options.MaxReplans} re-plan(s) is spent, so this one "
                    + $"was not performed: {reason}",
                    [],
                    [.. toInvalidate.Select(id => id.Value)],
                    []),
                cancellationToken).ConfigureAwait(false);

            return false;
        }

        // Only what the caller named. Downstream work is invalidated later, and only if the
        // re-run actually produces different output — that is what makes a re-plan
        // incremental rather than a restart wearing a different word.
        ImmutableArray<NodeId> invalidating =
        [
            .. toInvalidate
                .Distinct()
                .Where(id => IsSettled(_state.StateOf(id)))
                .OrderBy(id => id.Value, StringComparer.Ordinal),
        ];

        if (invalidating.IsEmpty)
        {
            return false;
        }

        List<string> revokedApprovals = [];

        foreach (NodeId nodeId in invalidating)
        {
            WorkflowNode node = graph.Node(nodeId);

            ImmutableArray<string> revoked =
            [
                .. node.Approvals
                    .Select(approval => approval.Role)
                    .Where(role => _state.HeldApprovals.ContainsKey(role)),
            ];

            revokedApprovals.AddRange(revoked);

            await AppendAsync(
                RunEventKind.NodeInvalidated, nodeId, by,
                new NodeInvalidatedPayload(reason, trigger.Value, revoked),
                cancellationToken).ConfigureAwait(false);

            await TransitionAsync(
                nodeId, NodeState.Invalidated, by, reason, cancellationToken).ConfigureAwait(false);

            // Straight back to Pending: the scheduler treats it as unstarted work and applies
            // every gate and guard again from scratch.
            await TransitionAsync(
                nodeId, NodeState.Pending, Actor.Engine,
                "Returned to the plan for re-execution.", cancellationToken).ConfigureAwait(false);

            _unmetPreconditions.Remove(nodeId);
        }

        ImmutableArray<NodeId> unaffected =
        [
            .. graph.TopologicalOrder
                .Where(id => _state.StateOf(id) == NodeState.Succeeded)
                .OrderBy(id => id.Value, StringComparer.Ordinal),
        ];

        await AppendAsync(
            RunEventKind.ReplanPerformed, trigger, by,
            new ReplanPayload(
                _state.ReplanCount + 1,
                trigger.Value,
                reason,
                [.. invalidating.Select(id => id.Value)],
                [.. unaffected.Select(id => id.Value)],
                [.. revokedApprovals.Distinct(StringComparer.OrdinalIgnoreCase)]),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>True when a node has reached an outcome that a re-plan would undo.</summary>
    private static bool IsSettled(NodeState state) =>
        state is NodeState.Succeeded or NodeState.Skipped or NodeState.Failed
            or NodeState.Blocked or NodeState.AwaitingApproval;

    // ---- gates ----

    private const string ApprovalHeldKind = "approval-held";

    private async Task<GateResult> EvaluateGateAsync(
        WorkflowNode node,
        GatePosition position,
        ImmutableArray<GateCondition> conditions,
        CancellationToken cancellationToken)
    {
        ImmutableDictionary<string, PolicyEvaluation> policyResults =
            await EvaluatePoliciesForAsync(node, conditions, cancellationToken)
                .ConfigureAwait(false);

        ImmutableArray<GateConditionVerdict>.Builder verdicts =
            ImmutableArray.CreateBuilder<GateConditionVerdict>(conditions.Length);

        foreach (GateCondition condition in conditions)
        {
            IGateEvaluator evaluator = gates.Resolve(condition.Kind)
                                       ?? throw new EngineConfigurationException(
                                           $"Gate kind '{condition.Kind}' disappeared from the "
                                           + "registry mid-run.");

            GateEvaluation evaluation = new(node, position, condition, _state, policyResults);

            verdicts.Add(await evaluator.EvaluateAsync(evaluation, cancellationToken)
                .ConfigureAwait(false));
        }

        return new GateResult(position, node.Id, verdicts.ToImmutable());
    }

    /// <summary>
    /// Evaluates the policy packs a gate refers to, recording each evaluation.
    /// </summary>
    /// <remarks>
    /// Done here rather than inside the gate evaluator so the verdict the gate acts on and
    /// the evidence written to the log come from the same evaluation. Every rule is recorded,
    /// including ones a waiver lets through: a waiver stops a violation blocking, it does not
    /// stop it being true, and an auditor came for exactly that distinction.
    /// </remarks>
    private async Task<ImmutableDictionary<string, PolicyEvaluation>> EvaluatePoliciesForAsync(
        WorkflowNode node,
        ImmutableArray<GateCondition> conditions,
        CancellationToken cancellationToken)
    {
        ImmutableArray<string> packs =
        [
            .. conditions
                .Where(condition => string.Equals(
                    condition.Kind, PolicyCleanKind, StringComparison.Ordinal))
                .Select(condition => condition.Expression.Trim())
                .Where(pack => pack.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        if (packs.IsEmpty || policies is null)
        {
            return ImmutableDictionary<string, PolicyEvaluation>.Empty;
        }

        ImmutableDictionary<string, PolicyEvaluation>.Builder results =
            ImmutableDictionary.CreateBuilder<string, PolicyEvaluation>(
                StringComparer.OrdinalIgnoreCase);

        foreach (string pack in packs)
        {
            PolicyEvaluation evaluation;

            try
            {
                evaluation = await policies
                    .EvaluateAsync(pack, _state, graph, _workspace, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (KeyNotFoundException missing)
            {
                // A gate naming a pack nothing loaded must not quietly pass. Recording the
                // absence is what lets the gate fail closed with a reason.
                await AppendAsync(
                    RunEventKind.PolicyEvaluated, node.Id, Actor.Engine,
                    new PolicyEvaluatedPayload(pack, Clean: false, []),
                    cancellationToken).ConfigureAwait(false);

                await AppendAsync(
                    RunEventKind.PolicyViolationBlocked, node.Id, Actor.Engine,
                    new PolicyWaiverPayload(pack, pack, missing.Message),
                    cancellationToken).ConfigureAwait(false);

                continue;
            }

            results[pack] = evaluation;

            await AppendAsync(
                RunEventKind.PolicyEvaluated, node.Id, Actor.Engine,
                new PolicyEvaluatedPayload(
                    evaluation.Pack,
                    evaluation.IsClean,
                    [
                        .. evaluation.Verdicts.Select(verdict => new PolicyVerdictPayload(
                            verdict.Rule.Id,
                            verdict.Rule.Category.ToString(),
                            verdict.Rule.Severity.ToString(),
                            verdict.Satisfied,
                            verdict.IsWaived,
                            verdict.Explanation)),
                    ]),
                cancellationToken).ConfigureAwait(false);

            foreach (PolicyVerdict blocking in evaluation.Blocking)
            {
                await AppendAsync(
                    RunEventKind.PolicyViolationBlocked, node.Id, Actor.Policy(blocking.Rule.Id),
                    new PolicyWaiverPayload(
                        blocking.Rule.Id, evaluation.Pack, blocking.Explanation),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return results.ToImmutable();
    }

    private const string PolicyCleanKind = "policy-clean";

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

        // A loop-back is a declared information flow, so it grants scope in the direction it
        // flows. Without this a stage re-run by a loop-back could not see the answer that
        // caused it to be re-run — requirements would be redone without the clarification
        // that prompted the redo, which is worse than not looping back at all.
        IEnumerable<string> returned = graph.Definition.Edges
            .Where(edge => edge.Kind == EdgeKind.LoopBack && edge.To == node.Id)
            .SelectMany(edge => graph.Node(edge.From).ProducesContext);

        return
        [
            "run.*",
            .. upstream.Concat(returned).Distinct().OrderBy(key => key, StringComparer.Ordinal),
        ];
    }

    private ImmutableArray<Artifact> InputsFor(WorkflowNode node)
    {
        ImmutableHashSet<NodeId> upstream = graph.TransitiveDependenciesOf(node.Id)
            .Union(graph.Definition.Edges
                .Where(edge => edge.Kind == EdgeKind.LoopBack && edge.To == node.Id)
                .Select(edge => edge.From));

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
            (WorkflowContextKeys.Request, request.Request),
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

        // Recorded on both the success and the failure path, because this method is called
        // on both: what a run spent is not conditional on whether the stage worked.
        foreach (ModelCall call in result.ModelCalls)
        {
            await AppendAsync(
                RunEventKind.ModelCalled, node.Id, actor,
                new ModelCalledPayload(
                    call.PromptId,
                    call.PromptVersion,
                    call.Model,
                    call.Source.ToString(),
                    call.Fingerprint.Hex,
                    call.Usage.InputTokens,
                    call.Usage.OutputTokens,
                    call.Usage.CacheReadTokens,
                    call.Usage.CacheWriteTokens,
                    call.CostNanoUsd,
                    call.StopReason,
                    call.DurationMilliseconds),
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
                request.Id,
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

        // An operator stop and a rollback are statements about the run as a whole, so they
        // outrank anything an individual node ended up in: reporting "failed" for a run the
        // operator deliberately halted, or for one whose effects were undone on purpose,
        // would misdescribe what happened.
        if (_safeStopped)
        {
            ImmutableArray<NodeId> cancelled =
                [.. nodes.Where(node => node.State == NodeState.Cancelled).Select(node => node.Id)];

            return (
                RunStatus.SafeStopped,
                $"Halted at a safe boundary with {cancelled.Length} node(s) cancelled. "
                + "Completed work is preserved.");
        }

        if (_rollbackTrigger is { } trigger)
        {
            ImmutableArray<NodeId> undone =
                [.. nodes.Where(node => node.State == NodeState.RolledBack).Select(node => node.Id)];

            return (
                RunStatus.RolledBack,
                $"Rolled back after '{trigger}' exhausted its retries. "
                + (undone.IsEmpty
                    ? "No stage had effects to undo."
                    : $"Undone, newest first: {Name(undone)}.")
                + " Both the changes and their reversals remain in the workspace history.");
        }

        // Status is otherwise the most serious thing that happened; the reason names
        // everything a human would need to act on, because a run stopped by a policy block
        // may also have a signature waiting, and reporting only one wastes a round trip.
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

        ImmutableArray<NodeId> rolledBack =
        [
            .. nodes.Where(node => node.State == NodeState.RolledBack).Select(node => node.Id),
        ];

        if (!rolledBack.IsEmpty)
        {
            // A rollback is a conclusion, not a stall. The node's fallback was to compensate,
            // the compensation ran, and the run ended there — deliberately. Resuming must not
            // quietly re-run the stage whose effects a human can see were undone; whoever
            // decides the work should be attempted again starts a run that says so.
            return (
                RunStatus.RolledBack,
                $"The run ended in rollback: {Name(rolledBack)} was undone and the tree "
                + "returned to its prior state. A rolled-back run is not resumed — start a "
                + "new run once the cause is addressed.");
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
