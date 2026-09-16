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

        new ContextEvidenceGateEvaluator(
            kind: "no-secrets-committed",
            describes: "The security scan found no credential material in the change.",
            keySelector: _ => "security.secrets-found",
            satisfied: value => value.Trim() == "0",
            explain: value => value.Trim() == "0" ? "none found." : "secrets present."),

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
}
