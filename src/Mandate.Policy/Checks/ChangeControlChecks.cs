using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;

namespace Mandate.Policy.Checks;

/// <summary>Every approval the lifecycle asks for has actually been given.</summary>
public sealed class RequiredApprovalsHeldCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "required-approvals-held";

    /// <inheritdoc />
    public override string Describes =>
        "Every approval the lifecycle requires has been recorded by a named human.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Only stages that actually ran. A stage skipped on this path never needed its
        // approval, and demanding one would block runs that took a legitimate branch.
        ImmutableArray<string> outstanding =
        [
            .. context.Graph.Nodes
                .Where(node => node.RequiresApproval && HasRun(context.Run, node.Id))
                .SelectMany(node => node.Approvals)
                .Select(approval => approval.Role)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(role => !context.Run.HeldApprovals.ContainsKey(role))
                .Order(StringComparer.Ordinal),
        ];

        return Task.FromResult(outstanding.IsEmpty
            ? Satisfied(context, "Every required approval is held.")
            : Violated(
                context,
                "Approval outstanding for role(s): " + string.Join(", ", outstanding)));
    }

    private static bool HasRun(Core.Execution.IRunView run, NodeId nodeId) =>
        run.StateOf(nodeId) is NodeState.Succeeded or NodeState.AwaitingApproval
            or NodeState.Running or NodeState.Blocked;
}

/// <summary>
/// No approval was given by the participant who produced the work or asked for the run.
/// </summary>
/// <remarks>
/// The gate enforces this at the moment of signing. This rule is the standing check that it
/// held across the whole run — including for approvals recorded before a later code change,
/// or through a path that bypassed the command.
/// </remarks>
public sealed class SegregationOfDutiesUpheldCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "segregation-of-duties-upheld";

    /// <inheritdoc />
    public override string Describes =>
        "No approval was given by whoever produced the work or requested the run.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        Actor? initiator =
            Actor.TryParse(
                context.Run.Context.Latest(WorkflowContextKeys.InitiatedBy)?.Value ?? string.Empty,
                out Actor parsed)
                ? parsed
                : null;

        List<string> breaches = [];

        foreach (WorkflowNode node in context.Graph.Nodes.Where(node => node.RequiresApproval))
        {
            foreach (ApprovalRequirement approval in node.Approvals.Where(
                         approval => approval.SegregationOfDuties))
            {
                if (!context.Run.HeldApprovals.TryGetValue(approval.Role, out Actor approver))
                {
                    continue;
                }

                if (context.Run.ProducerOf(node.Id) is { } producer
                    && producer.IsSameParticipantAs(approver))
                {
                    breaches.Add($"{approver} approved '{node.Id}', which they produced");
                }

                if (initiator is { } requester && requester.IsSameParticipantAs(approver))
                {
                    breaches.Add($"{approver} approved '{node.Id}' and also requested the run");
                }
            }
        }

        return Task.FromResult(breaches.Count == 0
            ? Satisfied(context, "Every segregated approval was given by an independent party.")
            : Violated(context, string.Join("; ", breaches)));
    }
}

/// <summary>Tests were actually executed, and their result recorded.</summary>
public sealed class TestsExecutedCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "tests-executed";

    /// <inheritdoc />
    public override string Describes =>
        "A test run produced a recorded result for the change.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool changed = context.Run.Artifacts.Any(a => a.Kind == ArtifactKind.SourcePatch);

        if (!changed)
        {
            return Task.FromResult(Satisfied(context, "The run changed no source."));
        }

        bool tested = context.Run.Artifacts.Any(a => a.Kind == ArtifactKind.TestReport);
        string? failures = context.Run.Context.Latest("test.failures")?.Value;

        if (!tested || failures is null)
        {
            return Task.FromResult(Violated(
                context,
                "Source changed but no test run was recorded. An untested change reaching a "
                + "release decision is the failure this rule exists to catch."));
        }

        return Task.FromResult(failures.Trim() == "0"
            ? Satisfied(context, $"A test run was recorded with {failures} failure(s).")
            : Violated(context, $"The recorded test run reported {failures} failure(s)."));
    }
}

/// <summary>The workspace holds no uncommitted change at a release decision.</summary>
public sealed class WorkspaceIsCleanCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "workspace-is-clean";

    /// <inheritdoc />
    public override string Describes =>
        "The run workspace holds no change that no stage committed.";

    /// <inheritdoc />
    public override async Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        Core.Execution.WorkspaceStatus status =
            await context.Workspace.StatusAsync(cancellationToken).ConfigureAwait(false);

        return status.IsClean
            ? Satisfied(
                context,
                $"The tree is clean at {Short(status.HeadSha)} across {status.CommitCount} commit(s).")
            : Violated(
                context,
                "The tree holds uncommitted changes, so what would be released is not what any "
                + "stage produced.");
    }

    private static string Short(string sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : sha[..Math.Min(8, sha.Length)];
}
