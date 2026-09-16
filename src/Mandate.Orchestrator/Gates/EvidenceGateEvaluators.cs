using System.Globalization;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Core.Workflow;

namespace Mandate.Orchestrator.Gates;

/// <summary>
/// Passes when a named human has recorded an approval for the required role.
/// </summary>
/// <remarks>
/// The engine can never satisfy this condition on its own behalf: approvals enter the run only
/// through a recorded human act. A gate that failed only on this condition parks the node
/// rather than failing it, because the work is complete and waiting rather than broken.
/// </remarks>
public sealed class ApprovalHeldGateEvaluator : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind => "approval-held";

    /// <inheritdoc />
    public string Describes => "A named human has approved, in the required role.";

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        string role = evaluation.Condition.Expression.Trim();

        if (string.IsNullOrEmpty(role))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition, false, "No approval role was named. Fails closed."));
        }

        if (!evaluation.Run.HeldApprovals.TryGetValue(role, out Actor approver))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition, false, $"No approval recorded for role '{role}'."));
        }

        Actor? producer = evaluation.Run.ProducerOf(evaluation.Node.Id);

        bool segregationRequired = evaluation.Node.Approvals
            .Any(approval =>
                string.Equals(approval.Role, role, StringComparison.OrdinalIgnoreCase)
                && approval.SegregationOfDuties);

        if (segregationRequired && producer is { } author && approver.IsSameParticipantAs(author))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"'{approver}' produced this work and cannot also approve it in role '{role}'. "
                + "Segregation of duties applies."));
        }

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition, true, $"Approved by {approver} in role '{role}'."));
    }
}

/// <summary>
/// Passes when a context fact exists and satisfies a predicate.
/// </summary>
/// <remarks>
/// The workhorse for conditions backed by recorded evidence: a test run's failure count, a
/// security scan's secret count, whether the workspace compiled. Absent evidence is always a
/// failure, never a pass — a gate that passed because the data it needed had not been produced
/// would appear in the audit log as a satisfied check.
/// </remarks>
public sealed class ContextEvidenceGateEvaluator(
    string kind,
    string describes,
    Func<GateCondition, string> keySelector,
    Func<string, bool> satisfied,
    Func<string, string> explain) : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind { get; } = kind;

    /// <inheritdoc />
    public string Describes { get; } = describes;

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        string key = keySelector(evaluation.Condition);
        string? value = evaluation.Run.LatestValue(key);

        if (value is null)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"No evidence: context key '{key}' has not been recorded. Fails closed, because "
                + "a check that passes for want of data is worse than no check."));
        }

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition, satisfied(value), $"{key} = '{value}': {explain(value)}"));
    }
}

/// <summary>
/// Passes when the named policy pack has no outstanding blocking violation.
/// </summary>
/// <remarks>
/// Waived violations do not block, but they are still reported: a waiver is an override on
/// the record, not a way of making a rule stop applying. A pack that was not evaluated fails
/// closed, because "nothing checked it" and "it came back clean" must never look the same.
/// </remarks>
public sealed class PolicyCleanGateEvaluator : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind => "policy-clean";

    /// <inheritdoc />
    public string Describes => "Every rule in the named policy pack evaluates clean.";

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        string pack = evaluation.Condition.Expression.Trim();

        if (string.IsNullOrEmpty(pack))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition, false, "No policy pack was named. Fails closed."));
        }

        if (evaluation.Policies is null
            || !evaluation.Policies.TryGetValue(pack, out PolicyEvaluation? result))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"The policy pack '{pack}' was not evaluated, so it cannot be said to be "
                + "clean. Fails closed."));
        }

        if (result.IsClean)
        {
            IEnumerable<string> waived = result.Waived.Select(verdict => verdict.Rule.Id);

            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                true,
                result.Summary
                + (waived.Any()
                    ? $" Waived on the record: {string.Join(", ", waived)}."
                    : string.Empty)));
        }

        IEnumerable<string> blocking = result.Blocking.Select(
            verdict => $"{verdict.Rule.Id} ({verdict.Explanation})");

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition,
            false,
            result.Summary + " Outstanding: " + string.Join("; ", blocking)));
    }
}

/// <summary>Passes when a recorded numeric fact meets a floor given in the condition.</summary>
public sealed class NumericFloorGateEvaluator(string kind, string describes, string key)
    : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind { get; } = kind;

    /// <inheritdoc />
    public string Describes { get; } = describes;

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!decimal.TryParse(
                evaluation.Condition.Expression,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal floor))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"'{evaluation.Condition.Expression}' is not a number, so the threshold cannot "
                + "be applied. Fails closed."));
        }

        string? recorded = evaluation.Run.LatestValue(key);

        if (recorded is null)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"No evidence: context key '{key}' has not been recorded. Fails closed."));
        }

        if (!decimal.TryParse(
                recorded, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal actual))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"{key} = '{recorded}', which is not a number. Fails closed."));
        }

        bool passed = actual >= floor;

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition,
            passed,
            $"{key} = {actual.ToString(CultureInfo.InvariantCulture)}, "
            + $"threshold {floor.ToString(CultureInfo.InvariantCulture)}: "
            + (passed ? "met." : "not met.")));
    }
}

/// <summary>Passes when a recorded numeric fact is at or below a ceiling given in the condition.</summary>
/// <remarks>
/// The mirror of <see cref="NumericFloorGateEvaluator"/>, for the conditions where a lower
/// number is the good outcome — an ambiguity score, a defect count, a blast radius.
/// </remarks>
public sealed class NumericCeilingGateEvaluator(string kind, string describes, string key)
    : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind { get; } = kind;

    /// <inheritdoc />
    public string Describes { get; } = describes;

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!decimal.TryParse(
                evaluation.Condition.Expression,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal ceiling))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"'{evaluation.Condition.Expression}' is not a number, so the ceiling cannot be "
                + "applied. Fails closed."));
        }

        string? recorded = evaluation.Run.LatestValue(key);

        if (recorded is null)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"No evidence: context key '{key}' has not been recorded. Fails closed."));
        }

        if (!decimal.TryParse(
                recorded, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal actual))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"{key} = '{recorded}', which is not a number. Fails closed."));
        }

        bool passed = actual <= ceiling;

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition,
            passed,
            $"{key} = {actual.ToString(CultureInfo.InvariantCulture)}, "
            + $"ceiling {ceiling.ToString(CultureInfo.InvariantCulture)}: "
            + (passed ? "within." : "exceeded.")));
    }
}

/// <summary>
/// Passes when the highest recorded finding severity is strictly below the named ceiling.
/// </summary>
public sealed class SeverityCeilingGateEvaluator(string kind, string describes, string key)
    : IGateEvaluator
{
    private static readonly string[] Ladder = ["none", "info", "low", "medium", "high", "critical"];

    /// <inheritdoc />
    public string Kind { get; } = kind;

    /// <inheritdoc />
    public string Describes { get; } = describes;

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        int ceiling = Rank(evaluation.Condition.Expression);

        if (ceiling < 0)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"'{evaluation.Condition.Expression}' is not a known severity. Expected one of "
                + $"{string.Join(", ", Ladder)}. Fails closed."));
        }

        string? recorded = evaluation.Run.LatestValue(key);

        if (recorded is null)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"No evidence: context key '{key}' has not been recorded. Fails closed."));
        }

        int highest = Rank(recorded);

        if (highest < 0)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"{key} = '{recorded}', which is not a known severity. Fails closed."));
        }

        bool passed = highest < ceiling;

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition,
            passed,
            $"{key} = '{recorded}', ceiling '{evaluation.Condition.Expression}': "
            + (passed ? "below the ceiling." : "at or above the ceiling.")));
    }

    private static int Rank(string? severity) =>
        severity is null
            ? -1
            : Array.FindIndex(Ladder, entry =>
                string.Equals(entry, severity.Trim(), StringComparison.OrdinalIgnoreCase));
}
