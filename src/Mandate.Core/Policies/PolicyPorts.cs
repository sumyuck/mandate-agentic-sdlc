using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Core.Workflow;

namespace Mandate.Core.Policies;

/// <summary>What a policy check is given.</summary>
/// <param name="Rule">The rule being evaluated.</param>
/// <param name="Run">Read-only view of the run's accumulated evidence.</param>
/// <param name="Graph">The workflow being executed, for rules about the lifecycle itself.</param>
/// <param name="Workspace">The tree the run wrote into.</param>
public sealed record PolicyCheckContext(
    PolicyRule Rule, IRunView Run, WorkflowGraph Graph, IRunWorkspace Workspace);

/// <summary>
/// Evaluates one kind of policy rule.
/// </summary>
/// <remarks>
/// Like gate conditions, checks are evidence-based and fail closed: a check that cannot reach
/// the evidence it needs reports a violation rather than a pass. A rule that quietly passed
/// because its evidence was missing would appear in the audit trail as a control that held.
/// </remarks>
public interface IPolicyCheck
{
    /// <summary>The check kind this evaluates, as written in a policy pack.</summary>
    string Kind { get; }

    /// <summary>What the check looks at.</summary>
    string Describes { get; }

    /// <summary>Judges the rule.</summary>
    Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken);
}

/// <summary>Resolves the check a rule names.</summary>
public interface IPolicyCheckRegistry
{
    /// <summary>Every check kind this registry can evaluate.</summary>
    IReadOnlySet<string> KnownKinds { get; }

    /// <summary>Resolves a check, or returns <see langword="null"/> when the kind is unknown.</summary>
    IPolicyCheck? Resolve(string kind);
}

/// <summary>
/// Evaluates policy packs against a run.
/// </summary>
/// <remarks>
/// Separate from the gate machinery because policy answers a different question. A gate asks
/// whether this stage's own work is acceptable; policy asks whether the run as a whole is
/// still within the rules it is required to obey — and the second question is not the sum of
/// the first.
/// </remarks>
public interface IPolicyEngine
{
    /// <summary>Every pack this engine can evaluate.</summary>
    ImmutableArray<PolicyPack> Packs { get; }

    /// <summary>Evaluates a named pack.</summary>
    /// <exception cref="KeyNotFoundException">No such pack is loaded.</exception>
    Task<PolicyEvaluation> EvaluateAsync(
        string pack,
        IRunView run,
        WorkflowGraph graph,
        IRunWorkspace workspace,
        CancellationToken cancellationToken);
}
