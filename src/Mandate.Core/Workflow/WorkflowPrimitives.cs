using Mandate.Core.Identifiers;

namespace Mandate.Core.Workflow;

/// <summary>
/// How much a stage agent may do without a human in the loop.
/// </summary>
/// <remarks>
/// The brief's governing principle is that agents execute inside defined autonomy boundaries.
/// Making that a per-node declaration rather than a global setting is the difference between
/// controlled autonomy and a trust level: generating tests and signing off a release are not
/// the same risk, and should not carry the same freedom.
/// </remarks>
public enum AutonomyLevel
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>L0 — the agent may only propose; a human performs or accepts the action.</summary>
    ProposeOnly = 1,

    /// <summary>L1 — the agent may act inside the run workspace; a human approves promotion.</summary>
    ActInSandbox = 2,

    /// <summary>L2 — the agent may act and have low-risk output accepted automatically.</summary>
    ActAndAutoAcceptLowRisk = 3,

    /// <summary>
    /// L3 — fully autonomous, with no human checkpoint.
    /// </summary>
    /// <remarks>
    /// Representable but deliberately unused in the shipped workflow. Keeping the level in the
    /// model while declining to assign it states the boundary explicitly, which is more honest
    /// than omitting it and implying the question was never considered.
    /// </remarks>
    FullyAutonomous = 4,
}

/// <summary>How many inbound paths must succeed before a node becomes eligible.</summary>
public enum JoinPolicy
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Every inbound path must have succeeded. The default barrier.</summary>
    All = 1,

    /// <summary>Any one inbound path is enough.</summary>
    Any = 2,

    /// <summary>A declared number of inbound paths must have succeeded.</summary>
    Quorum = 3,
}

/// <summary>What an edge means for plan validity.</summary>
public enum EdgeKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Normal forward dependency. The forward subgraph must be acyclic.</summary>
    Forward = 1,

    /// <summary>
    /// A deliberate return to an earlier node, for a retry or a re-plan.
    /// </summary>
    /// <remarks>
    /// Non-linear execution needs backward edges, but a backward edge is also how a workflow
    /// becomes an infinite loop. Marking them explicitly lets validation insist that the
    /// forward graph is acyclic while still permitting the loops the lifecycle genuinely has,
    /// and every loop-back is bounded by the target node's retry budget.
    /// </remarks>
    LoopBack = 2,
}

/// <summary>What to do when a node exhausts its retry budget.</summary>
public enum FallbackStrategy
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Fail the node and let the run's failure handling take over.</summary>
    FailNode = 1,

    /// <summary>Re-run the stage with a reduced-capability agent.</summary>
    DegradedAgent = 2,

    /// <summary>Park the node and ask a human to intervene.</summary>
    HumanHandoff = 3,

    /// <summary>Compensate the node, undoing its effects.</summary>
    Compensate = 4,

    /// <summary>Skip the node, which requires a recorded waiver.</summary>
    SkipWithWaiver = 5,
}

/// <summary>
/// Bounded retry configuration for a node.
/// </summary>
/// <param name="MaxAttempts">Total attempts including the first. 1 means no retry.</param>
/// <param name="InitialBackoff">Delay before the second attempt.</param>
/// <param name="BackoffMultiplier">Factor applied to the delay after each failure.</param>
/// <param name="MaxBackoff">Ceiling on the delay.</param>
/// <param name="JitterRatio">Random proportion of the delay applied to avoid synchronised retries.</param>
/// <param name="OnExhaustion">What happens once attempts run out.</param>
public sealed record RetryPolicy(
    int MaxAttempts,
    TimeSpan InitialBackoff,
    double BackoffMultiplier,
    TimeSpan MaxBackoff,
    double JitterRatio,
    FallbackStrategy OnExhaustion)
{
    /// <summary>No retry: one attempt, then the fallback.</summary>
    public static RetryPolicy None { get; } = new(
        MaxAttempts: 1,
        InitialBackoff: TimeSpan.Zero,
        BackoffMultiplier: 1d,
        MaxBackoff: TimeSpan.Zero,
        JitterRatio: 0d,
        OnExhaustion: FallbackStrategy.FailNode);

    /// <summary>A conservative default for stages whose failures are often transient.</summary>
    public static RetryPolicy Default { get; } = new(
        MaxAttempts: 3,
        InitialBackoff: TimeSpan.FromSeconds(2),
        BackoffMultiplier: 2d,
        MaxBackoff: TimeSpan.FromSeconds(30),
        JitterRatio: 0.2d,
        OnExhaustion: FallbackStrategy.HumanHandoff);

    /// <summary>The delay before a given attempt, excluding jitter.</summary>
    /// <param name="attempt">The attempt about to be made, counting from 1.</param>
    public TimeSpan BackoffBefore(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        if (attempt == 1 || InitialBackoff == TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        double seconds = InitialBackoff.TotalSeconds
                         * Math.Pow(BackoffMultiplier, attempt - 2);

        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>True when another attempt is permitted after <paramref name="attemptsMade"/>.</summary>
    public bool PermitsRetryAfter(int attemptsMade) => attemptsMade < MaxAttempts;
}

/// <summary>
/// One assertion a gate makes before letting execution in or out of a node.
/// </summary>
/// <param name="Kind">
/// The assertion family, for example <c>artifact-exists</c>, <c>tests-pass</c>,
/// <c>coverage-at-least</c>, <c>policy-clean</c> or <c>approval-held</c>.
/// </param>
/// <param name="Expression">The assertion's parameter, interpreted by the evaluator for its kind.</param>
/// <param name="Description">Reviewer-facing statement of what this condition protects.</param>
public sealed record GateCondition(string Kind, string Expression, string Description);

/// <summary>
/// A required human sign-off on a node.
/// </summary>
/// <param name="Role">The role expected to approve, such as <c>tech-lead</c>.</param>
/// <param name="Reason">Why this action is high-impact enough to need a human.</param>
/// <param name="SegregationOfDuties">
/// When true, the approver may not be the participant that produced the work. This is the
/// change-control rule that stops an implementer from approving its own change.
/// </param>
public sealed record ApprovalRequirement(string Role, string Reason, bool SegregationOfDuties)
{
    /// <summary>True when <paramml name="approver"/> may not approve work produced by <paramref name="producer"/>.</summary>
    public bool WouldViolateSegregation(Actor approver, Actor producer) =>
        SegregationOfDuties && approver.IsSameParticipantAs(producer);
}
