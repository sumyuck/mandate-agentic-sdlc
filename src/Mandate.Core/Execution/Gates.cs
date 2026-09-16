using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;

namespace Mandate.Core.Execution;

/// <summary>Which side of a node a gate guards.</summary>
public enum GatePosition
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Checked before the node may start.</summary>
    Entry = 1,

    /// <summary>Checked before the node's output is accepted.</summary>
    Exit = 2,
}

/// <summary>One gate condition's verdict.</summary>
/// <param name="Condition">The condition that was evaluated.</param>
/// <param name="Passed">Whether it held.</param>
/// <param name="Explanation">
/// The evidence behind the verdict, in reviewer-facing terms. Recorded on the gate's audit
/// event, so "why did this gate fail?" is answerable from the log alone.
/// </param>
public sealed record GateConditionVerdict(GateCondition Condition, bool Passed, string Explanation);

/// <summary>The combined result of evaluating a gate.</summary>
/// <param name="Position">Whether this was the entry or exit gate.</param>
/// <param name="NodeId">The node it guards.</param>
/// <param name="Verdicts">Each condition's verdict, in declaration order.</param>
public sealed record GateResult(
    GatePosition Position,
    NodeId NodeId,
    ImmutableArray<GateConditionVerdict> Verdicts)
{
    /// <summary>True when every condition held.</summary>
    public bool Passed => Verdicts.All(verdict => verdict.Passed);

    /// <summary>The conditions that did not hold.</summary>
    public IEnumerable<GateConditionVerdict> Failures => Verdicts.Where(verdict => !verdict.Passed);

    /// <summary>A one-line summary for logs and CLI output.</summary>
    public string Summary => Passed
        ? $"{Position} gate on '{NodeId}' passed ({Verdicts.Length} condition(s))."
        : $"{Position} gate on '{NodeId}' failed: "
          + string.Join("; ", Failures.Select(failure => failure.Explanation));
}

/// <summary>
/// Read-only view of a run, as gate evaluators and agents see it.
/// </summary>
/// <remarks>
/// Deliberately read-only. A gate that could alter the run it is judging would not be a gate,
/// and an evaluator that could record an approval could pass its own check.
/// </remarks>
public interface IRunView
{
    /// <summary>The run's identifier.</summary>
    RunId RunId { get; }

    /// <summary>The current overall status.</summary>
    RunStatus Status { get; }

    /// <summary>Accumulated context, with revision history.</summary>
    RunContext Context { get; }

    /// <summary>Every artifact produced so far.</summary>
    ImmutableArray<Artifact> Artifacts { get; }

    /// <summary>Approvals granted so far, by role, with the human who granted each.</summary>
    ImmutableDictionary<string, Actor> HeldApprovals { get; }

    /// <summary>The current state of a node.</summary>
    NodeState StateOf(NodeId nodeId);

    /// <summary>The actor that produced a node's output, when it has run.</summary>
    Actor? ProducerOf(NodeId nodeId);
}

/// <summary>Convenience reads over a run view.</summary>
public static class RunViewExtensions
{
    /// <summary>Artifacts of a given kind, or an empty sequence.</summary>
    public static IEnumerable<Artifact> ArtifactsOfKindOrEmpty(this IRunView run, ArtifactKind kind)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Artifacts.Where(artifact => artifact.Kind == kind);
    }

    /// <summary>The latest value of a context key, or <see langword="null"/> when absent.</summary>
    public static string? LatestValue(this IRunView run, string key)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Context.Latest(key)?.Value;
    }
}

/// <summary>What a gate evaluator is given.</summary>
/// <param name="Node">The node being gated.</param>
/// <param name="Position">Entry or exit.</param>
/// <param name="Condition">The specific condition to judge.</param>
/// <param name="Run">Read-only view of the run's accumulated evidence.</param>
public sealed record GateEvaluation(
    WorkflowNode Node,
    GatePosition Position,
    GateCondition Condition,
    IRunView Run);

/// <summary>
/// Judges one kind of gate condition.
/// </summary>
/// <remarks>
/// <para>
/// Every evaluator is evidence-based and fails closed: absent evidence is a failure, never a
/// pass. A gate that passed because the data it needed had not been produced would be worse
/// than no gate at all, since it would appear in the audit log as a satisfied check.
/// </para>
/// <para>
/// One evaluator per kind, each independently testable, and an unknown kind is rejected before
/// a run starts rather than discovered when a gate is reached.
/// </para>
/// </remarks>
public interface IGateEvaluator
{
    /// <summary>The condition kind this evaluator judges, as written in the workflow file.</summary>
    string Kind { get; }

    /// <summary>What the condition means, for diagnostics and documentation.</summary>
    string Describes { get; }

    /// <summary>Judges the condition.</summary>
    ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken);
}

/// <summary>Resolves the evaluator for a gate condition kind.</summary>
public interface IGateEvaluatorRegistry
{
    /// <summary>Every condition kind this registry can judge.</summary>
    IReadOnlySet<string> KnownKinds { get; }

    /// <summary>Resolves an evaluator, or returns <see langword="null"/> when the kind is unknown.</summary>
    IGateEvaluator? Resolve(string kind);
}
