namespace Helmsman.Core.Runs;

/// <summary>Overall status of a run, projected from its node states.</summary>
public enum RunStatus
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Planned and persisted, not yet started.</summary>
    Planned = 1,

    /// <summary>At least one node is executing or eligible to execute.</summary>
    Running = 2,

    /// <summary>Cannot progress until a human approves or denies a parked node.</summary>
    AwaitingApproval = 3,

    /// <summary>Cannot progress because a policy guardrail is unresolved.</summary>
    Blocked = 4,

    /// <summary>Every required node succeeded and the terminal gate passed.</summary>
    Succeeded = 5,

    /// <summary>Terminated with a node failure that retries and fallbacks could not clear.</summary>
    Failed = 6,

    /// <summary>Terminated after compensating its completed work.</summary>
    RolledBack = 7,

    /// <summary>Halted at a safe boundary with state preserved; resumable.</summary>
    SafeStopped = 8,

    /// <summary>Terminated by operator cancellation.</summary>
    Cancelled = 9,
}
