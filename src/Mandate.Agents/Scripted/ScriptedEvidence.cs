using System.Collections.Frozen;
using Mandate.Core.Execution;
using Mandate.Core.Workflow;

namespace Mandate.Agents.Scripted;

/// <summary>
/// The evidence values scripted stages report.
/// </summary>
/// <remarks>
/// These are the facts the gates actually read, so they have to be plausible rather than
/// arbitrary: a scripted test stage reports zero failures and a real coverage figure, because
/// the coverage gate applies a threshold to it. The ambiguity score is derived from the run's
/// scenario, which is what makes the three scenarios take genuinely different paths through
/// the lifecycle rather than the same path with different labels.
/// </remarks>
internal static class ScriptedEvidence
{
    private static readonly FrozenDictionary<string, string> Fixed =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["test.failures"] = "0",
            ["test.coverage"] = "0.86",
            ["review.findings"] = "2",
            ["review.highest-severity"] = "low",
            ["security.findings"] = "0",
            ["security.secrets-found"] = "0",
            ["implementation.builds"] = "true",
            ["implementation.files-changed"] = "7",
            ["impact.blast-radius"] = "3",
            ["impact.affected-components"] = "api, worker, schema",
            ["policy.change-control-clean"] = "true",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static string For(string key, StageExecution execution)
    {
        if (Fixed.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (string.Equals(key, "requirements.ambiguity-score", StringComparison.Ordinal))
        {
            return IsAmbiguous(execution) ? "0.8" : "0.1";
        }

        // Anything else is a marker that the stage ran. Gates read the keys above; the rest
        // exist so downstream guards and context scopes have something real to resolve.
        return "true";
    }

    private static bool IsAmbiguous(StageExecution execution)
    {
        string? scenario = execution.Context.Latest(WorkflowContextKeys.Scenario)?.Value;

        return string.Equals(
            scenario,
            ScenarioKind.Ambiguous.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }
}
