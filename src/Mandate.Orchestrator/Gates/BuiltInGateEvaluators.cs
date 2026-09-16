using System.Collections.Immutable;
using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Gates;

/// <summary>
/// The gate condition kinds the shipped lifecycle uses.
/// </summary>
/// <remarks>
/// Every one is evidence-based. Where the evidence is a context fact, the fact has to have
/// been recorded by a stage that actually did the work — so <c>tests-pass</c> reflects the
/// outcome of a test run rather than an agent's opinion of its own code.
/// </remarks>
public static class BuiltInGateEvaluators
{
    /// <summary>Every built-in evaluator.</summary>
    public static ImmutableArray<IGateEvaluator> All =>
    [
        new ArtifactExistsGateEvaluator(),
        new AnyArtifactExistsGateEvaluator(),
        new ApprovalHeldGateEvaluator(),

        new ContextEvidenceGateEvaluator(
            kind: "tests-pass",
            describes: "The recorded test run reported no failures.",
            keySelector: _ => "test.failures",
            satisfied: value => value.Trim() == "0",
            explain: value => value.Trim() == "0" ? "no failures." : "failures outstanding."),

        // 'security.secrets-found' is a boolean, as its name reads: the count of findings
        // is 'security.findings'. This gate previously compared it to the string "0", which
        // the scripted agent happened to emit — so the gate passed for the wrong reason and
        // would have rejected the word "false". Fail-closed on a value it cannot read, but
        // say which of the two failures it is: "there are secrets" and "I could not tell"
        // call for different actions from whoever reads the log.
        new ContextEvidenceGateEvaluator(
            kind: "no-secrets-committed",
            describes: "The security scan found no credential material in the change.",
            keySelector: _ => "security.secrets-found",
            satisfied: IsFalse,
            explain: value =>
                IsFalse(value) ? "none found."
                : IsTrue(value) ? "secrets present."
                : $"the scan recorded '{value.Trim()}', which is not true or false — its "
                  + "result could not be read, so the gate fails closed."),

        new ContextEvidenceGateEvaluator(
            kind: "workspace-builds",
            describes: "The run workspace compiles after the change.",
            keySelector: _ => "implementation.builds",
            satisfied: IsTrue,
            explain: value => IsTrue(value) ? "compiles." : "does not compile."),

        new PolicyCleanGateEvaluator(),

        new NumericCeilingGateEvaluator(
            kind: "ambiguity-below",
            describes: "The requirement has no unresolved material ambiguity.",
            key: "requirements.ambiguity-score"),

        new NumericFloorGateEvaluator(
            kind: "coverage-at-least",
            describes: "Coverage of the changed code meets the threshold.",
            key: "test.coverage"),

        new SeverityCeilingGateEvaluator(
            kind: "no-findings-above",
            describes: "No review finding reaches the named severity.",
            key: "review.highest-severity"),
    ];

    /// <summary>A registry of the built-in evaluators.</summary>
    public static GateEvaluatorRegistry CreateRegistry() => new(All);

    private static bool IsTrue(string value) =>
        bool.TryParse(value.Trim(), out bool parsed) && parsed;

    private static bool IsFalse(string value) =>
        bool.TryParse(value.Trim(), out bool parsed) && !parsed;
}
