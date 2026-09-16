using System.Collections.Immutable;
using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Compensation;

/// <summary>
/// Undoes a node's effect on the workspace by reverting its commits.
/// </summary>
/// <remarks>
/// This is what makes rollback a fact rather than an assertion. After it runs the tree can be
/// inspected: the files the node added are gone, the files it edited are as they were, and
/// the working tree is clean. A log line claiming a rollback occurred would prove none of
/// that.
/// </remarks>
public sealed class RevertNodeCommitAction : ICompensationAction
{
    /// <inheritdoc />
    public string Id => "revert-node-commit";

    /// <inheritdoc />
    public string Describes => "Reverts the node's commits, restoring the tree it changed.";

    /// <inheritdoc />
    public async Task<CompensationResult> ExecuteAsync(
        CompensationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Anything a failed attempt left uncommitted is not a change any node declared, so it
        // is discarded before the revert rather than reverted along with it.
        await context.Workspace.DiscardUncommittedAsync(cancellationToken).ConfigureAwait(false);

        int reverted = await context.Workspace
            .RevertNodeAsync(context.Node.Id, cancellationToken)
            .ConfigureAwait(false);

        WorkspaceStatus status = await context.Workspace
            .StatusAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!status.IsClean)
        {
            return CompensationResult.Failed(
                $"Reverted {reverted} commit(s) from '{context.Node.Id}', but the tree is not "
                + "clean. The workspace is in a state no node declared and needs a human.");
        }

        return CompensationResult.Success(
            reverted == 0
                ? $"'{context.Node.Id}' had committed nothing; there was nothing to undo."
                : $"Reverted {reverted} commit(s) from '{context.Node.Id}'. The tree is clean at "
                  + $"{status.HeadSha[..Math.Min(8, status.HeadSha.Length)]}, and both the change "
                  + "and its reversal remain in the history.");
    }
}

/// <summary>A registry built from a fixed set of compensating actions.</summary>
public sealed class CompensationRegistry : ICompensationRegistry
{
    private readonly ImmutableDictionary<string, ICompensationAction> _byId;

    /// <summary>Creates a registry, rejecting two actions claiming the same id.</summary>
    public CompensationRegistry(IEnumerable<ICompensationAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);

        ImmutableDictionary<string, ICompensationAction>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, ICompensationAction>(StringComparer.Ordinal);

        foreach (ICompensationAction action in actions)
        {
            if (builder.ContainsKey(action.Id))
            {
                throw new ArgumentException(
                    $"Two actions claim the compensation id '{action.Id}'. Which one undid a "
                    + "node's effects would depend on registration order.",
                    nameof(actions));
            }

            builder[action.Id] = action;
        }

        _byId = builder.ToImmutable();
        KnownActions = _byId.Keys.ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> KnownActions { get; }

    /// <inheritdoc />
    public ICompensationAction? Resolve(string actionId) =>
        _byId.TryGetValue(actionId, out ICompensationAction? action) ? action : null;

    /// <summary>The actions the shipped lifecycle uses.</summary>
    public static CompensationRegistry BuiltIn() => new([new RevertNodeCommitAction()]);
}
