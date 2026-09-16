using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;

namespace Mandate.Core.Execution;

/// <summary>
/// Waits, so that backoff is a decision the engine makes rather than a property of the clock.
/// </summary>
/// <remarks>
/// Injected for the same reason time is: a test that had to wait out an exponential backoff
/// would be slow enough that nobody would run it, and a backoff nobody tests is a backoff
/// nobody has verified is bounded.
/// </remarks>
public interface IDelay
{
    /// <summary>Waits for the given duration.</summary>
    Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>The real delay. Bound in the CLI composition root.</summary>
public sealed class RealDelay : IDelay
{
    /// <summary>Shared instance; the type is stateless.</summary>
    public static RealDelay Instance { get; } = new();

    private RealDelay()
    {
    }

    /// <inheritdoc />
    public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        duration <= TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(duration, cancellationToken);
}

/// <summary>
/// Reports whether an operator has asked the run to stop.
/// </summary>
/// <remarks>
/// Checked between scheduling passes rather than mid-stage, because stopping cleanly means
/// stopping at a boundary where the run's state is coherent. A stop that interrupted a stage
/// half-way would leave the workspace in a condition no one declared.
/// </remarks>
public interface ISafeStopMonitor
{
    /// <summary>True when a stop has been requested for this run.</summary>
    Task<bool> IsStopRequestedAsync(RunId runId, CancellationToken cancellationToken);
}

/// <summary>A monitor that never asks the run to stop.</summary>
public sealed class NeverStops : ISafeStopMonitor
{
    /// <summary>Shared instance; the type is stateless.</summary>
    public static NeverStops Instance { get; } = new();

    private NeverStops()
    {
    }

    /// <inheritdoc />
    public Task<bool> IsStopRequestedAsync(RunId runId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>What a compensating action is given.</summary>
/// <param name="RunId">The run being compensated.</param>
/// <param name="Node">The node whose effects are being undone.</param>
/// <param name="Workspace">The tree the node wrote into.</param>
public sealed record CompensationContext(RunId RunId, WorkflowNode Node, IRunWorkspace Workspace);

/// <summary>What undoing a node's effects achieved.</summary>
/// <param name="Undone">Whether the node's effects were successfully undone.</param>
/// <param name="Detail">What was done, in reviewer-facing terms.</param>
public sealed record CompensationResult(bool Undone, string Detail)
{
    /// <summary>The action succeeded.</summary>
    public static CompensationResult Success(string detail) => new(true, detail);

    /// <summary>The action could not undo the node's effects.</summary>
    public static CompensationResult Failed(string detail) => new(false, detail);
}

/// <summary>
/// Undoes a node's effects.
/// </summary>
/// <remarks>
/// Named in the workflow file and resolved at engine construction, so a node declaring a
/// compensation nothing implements fails before a run starts — rather than at the moment the
/// run most needs to undo something.
/// </remarks>
public interface ICompensationAction
{
    /// <summary>The identifier a workflow node uses to select this action.</summary>
    string Id { get; }

    /// <summary>What undoing looks like for this action.</summary>
    string Describes { get; }

    /// <summary>Undoes the node's effects.</summary>
    Task<CompensationResult> ExecuteAsync(
        CompensationContext context, CancellationToken cancellationToken);
}

/// <summary>Resolves the compensating action a node names.</summary>
public interface ICompensationRegistry
{
    /// <summary>Every action id this registry can resolve.</summary>
    IReadOnlySet<string> KnownActions { get; }

    /// <summary>Resolves an action, or returns <see langword="null"/> when it is unknown.</summary>
    ICompensationAction? Resolve(string actionId);
}
