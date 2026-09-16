namespace Helmsman.Core.Runs;

/// <summary>
/// Execution state of a single workflow node within a run.
/// </summary>
/// <remarks>
/// Every state that the brief's control requirements imply is explicit here rather than
/// collapsed into a generic "failed": a node blocked by policy, a node parked for human
/// approval, a node whose output was invalidated by re-planning and a node that was rolled
/// back are four materially different situations, and an auditor needs to tell them apart.
/// </remarks>
public enum NodeState
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Declared in the plan; upstream dependencies not yet satisfied.</summary>
    Pending = 1,

    /// <summary>Dependencies satisfied and entry gate passed; eligible to be scheduled.</summary>
    Ready = 2,

    /// <summary>Currently executing.</summary>
    Running = 3,

    /// <summary>Parked pending a human approval. Survives process exit by design.</summary>
    AwaitingApproval = 4,

    /// <summary>Stopped by a policy guardrail or a denied approval. Requires human action to clear.</summary>
    Blocked = 5,

    /// <summary>Completed and exit gate passed.</summary>
    Succeeded = 6,

    /// <summary>Execution or exit gate failed. May be retried while budget remains.</summary>
    Failed = 7,

    /// <summary>Running its compensating action to undo its effects.</summary>
    Compensating = 8,

    /// <summary>Compensation completed; the node's effects have been undone.</summary>
    RolledBack = 9,

    /// <summary>Not executed because a guard on every inbound edge evaluated false.</summary>
    Skipped = 10,

    /// <summary>A previously accepted result is no longer valid because an input changed.</summary>
    Invalidated = 11,

    /// <summary>Abandoned by a safe-stop or an operator cancellation.</summary>
    Cancelled = 12,
}
