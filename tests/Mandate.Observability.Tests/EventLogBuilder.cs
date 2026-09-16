using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;

namespace Mandate.Observability.Tests;

/// <summary>
/// Builds an event log a test can reason about.
/// </summary>
/// <remarks>
/// Real events with real digests, appended through the same chain the engine uses, so what
/// the calculator reads here is shaped exactly like what it reads in production. A stubbed
/// log would let the calculator pass against a shape the engine never produces.
/// </remarks>
internal sealed class EventLogBuilder
{
    private static readonly DateTimeOffset Origin = new(2026, 9, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly List<RunEvent> _events = [];
    private RunEvent? _tail;
    private double _seconds;

    public RunId RunId { get; } = RunId.New(Origin, "abc123");

    public ImmutableArray<RunEvent> Events => [.. _events];

    public EventLogBuilder Planned(params string[] nodes) =>
        Append(RunEventKind.RunPlanned, null, Actor.Engine, new RunPlannedPayload(
            "sdlc", "v1", "Greenfield", "Build a URL shortener", false,
            "mandate/0.1.0", 4, [.. nodes]));

    public EventLogBuilder Started() =>
        Append(RunEventKind.RunStarted, null, Actor.Engine, new RunStartedPayload(false));

    public EventLogBuilder Completed(RunStatus status, string reason = "done") =>
        Append(RunEventKind.RunCompleted, null, Actor.Engine,
            new RunCompletedPayload(status.ToString(), reason));

    public EventLogBuilder Attempt(string node, int attempt = 1, double takesSeconds = 1) =>
        Append(RunEventKind.NodeAttemptStarted, node, Actor.Engine,
                new NodeAttemptStartedPayload(attempt, $"{node}-agent", "claude-sonnet-5", "ActInSandbox"))
            .Advance(takesSeconds)
            .Append(RunEventKind.NodeAttemptFinished, node, Actor.Engine,
                new NodeAttemptFinishedPayload(attempt, true, null, (long)(takesSeconds * 1000)));

    public EventLogBuilder Moves(string node, NodeState from, NodeState to, Actor? by = null) =>
        Append(RunEventKind.NodeStateChanged, node, by ?? Actor.Engine,
            new NodeStateChangedPayload(from.ToString(), to.ToString(), 1, "because"));

    public EventLogBuilder Gate(string node, bool passed, int conditions = 1) =>
        Append(RunEventKind.ExitGateEvaluated, node, Actor.Engine,
            new GateEvaluatedPayload(
                "Exit",
                passed,
                [
                    .. Enumerable.Range(0, conditions).Select(index =>
                        new GateConditionPayload("artifact-exists", "x", passed, "because")),
                ]));

    public EventLogBuilder AsksApproval(string node, string role) =>
        Append(RunEventKind.ApprovalRequested, node, Actor.Engine,
            new ApprovalRequestedPayload(role, "High impact.", true, "agent:architect"));

    public EventLogBuilder Approves(string role, string by) =>
        Append(RunEventKind.ApprovalGranted, null, Actor.Human(by),
            new ApprovalDecidedPayload(role, "fine"));

    public EventLogBuilder Denies(string role, string by) =>
        Append(RunEventKind.ApprovalDenied, null, Actor.Human(by),
            new ApprovalDecidedPayload(role, "no"));

    public EventLogBuilder Retries(string node, int attempt) =>
        Append(RunEventKind.NodeRetryScheduled, node, Actor.Engine,
            new RetryPayload(attempt, 3, 2, "transient"));

    public EventLogBuilder Compensated(string node) =>
        Append(RunEventKind.NodeCompensationCompleted, node, Actor.Engine,
            new CompensationPayload("revert-node-commit", true, "reverted"));

    public EventLogBuilder Waives(string ruleId, string by) =>
        Append(RunEventKind.PolicyWaiverGranted, null, Actor.Human(by),
            new PolicyWaiverPayload(ruleId, "change-control", "accepted risk"));

    public EventLogBuilder Replanned(string trigger) =>
        Append(RunEventKind.ReplanPerformed, trigger, Actor.Engine,
            new ReplanPayload(1, trigger, "input changed", [trigger], [], []));

    public EventLogBuilder Advance(double seconds)
    {
        _seconds += seconds;
        return this;
    }

    private EventLogBuilder Append<TPayload>(
        RunEventKind kind, string? node, Actor actor, TPayload payload)
    {
        _tail = RunEvent.Append(
            _tail,
            RunId,
            Origin.AddSeconds(_seconds),
            kind,
            node is null ? null : NodeId.Parse(node),
            actor,
            payload);

        _events.Add(_tail);
        return this;
    }
}
