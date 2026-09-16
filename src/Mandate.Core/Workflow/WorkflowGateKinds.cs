namespace Mandate.Core.Workflow;

/// <summary>
/// Gate condition kinds the workflow schema itself gives meaning to.
/// </summary>
/// <remarks>
/// Most condition kinds are opaque to the domain: they are resolved by whichever evaluator is
/// registered. <see cref="ApprovalHeld"/> is the exception, because validation has to be able
/// to tell whether a declared approval is actually enforced by a gate. An approval that is
/// requested and then never required is worse than no approval, since the audit log would show
/// a checkpoint that nothing depended on.
/// </remarks>
public static class WorkflowGateKinds
{
    /// <summary>Requires a recorded human approval in a named role.</summary>
    public const string ApprovalHeld = "approval-held";
}
