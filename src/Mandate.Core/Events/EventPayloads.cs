using System.Collections.Immutable;

namespace Mandate.Core.Events;

// The payload carried by each kind of audit event. These are the persisted shapes: their
// member order is part of the hash preimage, so reordering a member is a breaking change to
// the audit chain and must go through a schema version bump rather than a silent edit.

/// <summary>Payload of <see cref="RunEventKind.RunPlanned"/>.</summary>
public sealed record RunPlannedPayload(
    string Workflow,
    string Version,
    string Scenario,
    string Request,
    bool HasExistingCode,
    string EngineFingerprint,
    int MaxConcurrency,
    ImmutableArray<string> PlannedNodes);

/// <summary>Payload of <see cref="RunEventKind.RunStarted"/>.</summary>
public sealed record RunStartedPayload(bool Resumed);

/// <summary>Payload of <see cref="RunEventKind.RunCompleted"/>.</summary>
public sealed record RunCompletedPayload(string Status, string Reason);

/// <summary>Payload of <see cref="RunEventKind.NodeStateChanged"/>.</summary>
public sealed record NodeStateChangedPayload(string From, string To, int Attempt, string Reason);

/// <summary>Payload of <see cref="RunEventKind.NodeAttemptStarted"/>.</summary>
public sealed record NodeAttemptStartedPayload(
    int Attempt, string Agent, string? Model, string Autonomy);

/// <summary>Payload of <see cref="RunEventKind.NodeAttemptFinished"/>.</summary>
public sealed record NodeAttemptFinishedPayload(
    int Attempt, bool Succeeded, string? Failure, long DurationMilliseconds);

/// <summary>One condition's verdict, as recorded on a gate event.</summary>
public sealed record GateConditionPayload(
    string Kind, string Expression, bool Passed, string Explanation);

/// <summary>Payload of the entry and exit gate events.</summary>
public sealed record GateEvaluatedPayload(
    string Position, bool Passed, ImmutableArray<GateConditionPayload> Conditions);

/// <summary>Payload of <see cref="RunEventKind.EdgeGuardEvaluated"/>.</summary>
public sealed record EdgeGuardEvaluatedPayload(
    string From, string To, string Guard, bool Taken, string Explanation);

/// <summary>Payload of <see cref="RunEventKind.ArtifactProduced"/>.</summary>
public sealed record ArtifactProducedPayload(
    string Hash,
    string Kind,
    string Name,
    string MediaType,
    long SizeBytes,
    ImmutableArray<string> DerivedFrom);

/// <summary>Payload of <see cref="RunEventKind.ContextFactAdded"/>.</summary>
public sealed record ContextFactAddedPayload(
    string Key, string Value, ImmutableArray<string> Evidence);

/// <summary>One option considered, as recorded on a decision event.</summary>
public sealed record DecisionOptionPayload(string Name, string Summary, string? RejectedBecause);

/// <summary>Payload of <see cref="RunEventKind.DecisionRecorded"/>.</summary>
public sealed record DecisionRecordedPayload(
    string DecisionId,
    string Question,
    ImmutableArray<DecisionOptionPayload> Options,
    string Chosen,
    string Rationale,
    double Confidence,
    string Authority,
    ImmutableArray<string> Evidence);

/// <summary>Payload of <see cref="RunEventKind.WorkspaceCommitted"/>.</summary>
public sealed record WorkspaceCommittedPayload(
    string Sha, int Attempt, int FilesChanged, ImmutableArray<string> Paths);

/// <summary>Payload of <see cref="RunEventKind.WorkspaceReverted"/>.</summary>
public sealed record WorkspaceRevertedPayload(
    int CommitsReverted, string HeadSha, bool TreeClean, string Detail);

/// <summary>Payload of the retry and fallback events.</summary>
public sealed record RetryPayload(
    int Attempt, int MaxAttempts, double BackoffSeconds, string Reason);

/// <summary>Payload of <see cref="RunEventKind.NodeFallbackSelected"/>.</summary>
public sealed record FallbackSelectedPayload(string Strategy, string Reason);

/// <summary>Payload of the compensation events.</summary>
public sealed record CompensationPayload(string Action, bool Undone, string Detail);

/// <summary>Payload of the safe-stop events.</summary>
public sealed record SafeStopPayload(string RequestedBy, int NodesCancelled, string Detail);

/// <summary>Payload of <see cref="RunEventKind.ApprovalRequested"/>.</summary>
public sealed record ApprovalRequestedPayload(
    string Role, string Reason, bool SegregationOfDuties, string? ProducedBy);

/// <summary>Payload of the approval decision events.</summary>
public sealed record ApprovalDecidedPayload(string Role, string Note);
