namespace Helmsman.Core.Events;

/// <summary>
/// The complete vocabulary of things that can be recorded about a run.
/// </summary>
/// <remarks>
/// This enum is the audit schema. Anything the orchestrator does that a reviewer might later
/// ask about has a kind here; anything without a kind cannot be recorded, which is a
/// deliberate constraint on the engine rather than a limitation of it.
/// </remarks>
public enum RunEventKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    // ---- Run lifecycle ----

    /// <summary>A run was created from a request and a workflow version.</summary>
    RunPlanned = 1,

    /// <summary>Execution began or resumed.</summary>
    RunStarted = 2,

    /// <summary>The run reached a terminal status.</summary>
    RunCompleted = 3,

    /// <summary>A safe stop was requested; the run will halt at the next safe boundary.</summary>
    SafeStopRequested = 4,

    /// <summary>The run halted at a safe boundary with state preserved.</summary>
    SafeStopCompleted = 5,

    // ---- Node lifecycle ----

    /// <summary>A node moved between states. Carries both the old and new state.</summary>
    NodeStateChanged = 10,

    /// <summary>A node began an execution attempt.</summary>
    NodeAttemptStarted = 11,

    /// <summary>A node's execution attempt finished, successfully or not.</summary>
    NodeAttemptFinished = 12,

    /// <summary>A failed node was retried within its budget.</summary>
    NodeRetryScheduled = 13,

    /// <summary>A node exhausted its retry budget; a fallback strategy applies.</summary>
    NodeRetryBudgetExhausted = 14,

    /// <summary>A fallback strategy was selected after retries were exhausted.</summary>
    NodeFallbackSelected = 15,

    /// <summary>A node's compensating action began.</summary>
    NodeCompensationStarted = 16,

    /// <summary>A node's effects were undone.</summary>
    NodeCompensationCompleted = 17,

    // ---- Gates and policy ----

    /// <summary>An entry gate was evaluated. Carries each condition's verdict.</summary>
    EntryGateEvaluated = 20,

    /// <summary>An exit gate was evaluated. Carries each condition's verdict.</summary>
    ExitGateEvaluated = 21,

    /// <summary>A policy rule reached a verdict on a proposed action.</summary>
    PolicyEvaluated = 22,

    /// <summary>A policy violation stopped a node.</summary>
    PolicyViolationBlocked = 23,

    /// <summary>A human granted an exception to a policy rule, with a recorded reason.</summary>
    PolicyWaiverGranted = 24,

    /// <summary>A human refused an exception to a policy rule.</summary>
    PolicyWaiverDenied = 25,

    // ---- Human oversight ----

    /// <summary>A node parked and is awaiting a named human approval.</summary>
    ApprovalRequested = 30,

    /// <summary>A human approved a parked node.</summary>
    ApprovalGranted = 31,

    /// <summary>A human declined a parked node.</summary>
    ApprovalDenied = 32,

    /// <summary>An ambiguous requirement produced a question for a human.</summary>
    ClarificationRequested = 33,

    /// <summary>A human answered a clarification question.</summary>
    ClarificationProvided = 34,

    /// <summary>An unanswered question was recorded as a working assumption instead.</summary>
    AssumptionRecorded = 35,

    // ---- Knowledge produced ----

    /// <summary>An artifact was produced, with its content address and provenance.</summary>
    ArtifactProduced = 40,

    /// <summary>A decision was recorded, with the options considered and the rationale.</summary>
    DecisionRecorded = 41,

    /// <summary>A fact was added to the shared run context.</summary>
    ContextFactAdded = 42,

    // ---- Re-planning ----

    /// <summary>An input to an accepted node changed, invalidating its result.</summary>
    NodeInvalidated = 50,

    /// <summary>The engine recomputed the plan. Carries the difference from the previous plan.</summary>
    ReplanPerformed = 51,
}
