using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Gates;

/// <summary>Passes when the run has produced an artifact of the named kind.</summary>
public sealed class ArtifactExistsGateEvaluator : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind => "artifact-exists";

    /// <inheritdoc />
    public string Describes => "An artifact of the named kind has been produced.";

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        if (!ArtifactKindSyntax.TryParse(evaluation.Condition.Expression, out ArtifactKind kind))
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"'{evaluation.Condition.Expression}' is not a known artifact kind, so the "
                + "condition cannot be judged. Fails closed."));
        }

        ImmutableArray<Artifact> matches = [.. evaluation.Run.ArtifactsOfKindOrEmpty(kind)];

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition,
            !matches.IsEmpty,
            matches.IsEmpty
                ? $"No {ArtifactKindSyntax.Format(kind)} artifact has been produced."
                : $"{matches.Length} {ArtifactKindSyntax.Format(kind)} artifact(s): "
                  + string.Join(", ", matches.Select(artifact => $"{artifact.Name} ({artifact.Hash.Abbreviated})"))));
    }
}

/// <summary>Passes when at least one of several artifact kinds has been produced.</summary>
/// <remarks>
/// Used where a stage may satisfy its obligation in more than one way — a clarification is
/// answered either by a human's response or by a recorded assumption, and either is
/// acceptable, but silence is not.
/// </remarks>
public sealed class AnyArtifactExistsGateEvaluator : IGateEvaluator
{
    /// <inheritdoc />
    public string Kind => "any-artifact-exists";

    /// <inheritdoc />
    public string Describes => "At least one of the named artifact kinds has been produced.";

    /// <inheritdoc />
    public ValueTask<GateConditionVerdict> EvaluateAsync(
        GateEvaluation evaluation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<string> names =
        [
            .. evaluation.Condition.Expression
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        ];

        if (names.IsEmpty)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition, false, "No artifact kinds were named. Fails closed."));
        }

        List<string> unknown = [];
        List<ArtifactKind> kinds = [];

        foreach (string name in names)
        {
            if (ArtifactKindSyntax.TryParse(name, out ArtifactKind kind))
            {
                kinds.Add(kind);
            }
            else
            {
                unknown.Add(name);
            }
        }

        if (unknown.Count > 0)
        {
            return ValueTask.FromResult(new GateConditionVerdict(
                evaluation.Condition,
                false,
                $"Unknown artifact kind(s): {string.Join(", ", unknown)}. Fails closed."));
        }

        ImmutableArray<ArtifactKind> present =
            [.. kinds.Where(kind => evaluation.Run.ArtifactsOfKindOrEmpty(kind).Any())];

        return ValueTask.FromResult(new GateConditionVerdict(
            evaluation.Condition,
            !present.IsEmpty,
            present.IsEmpty
                ? $"None of {string.Join(", ", kinds.Select(ArtifactKindSyntax.Format))} was produced."
                : $"Produced: {string.Join(", ", present.Select(ArtifactKindSyntax.Format))}."));
    }
}

/// <summary>Translates between the hyphenated names used in workflow files and artifact kinds.</summary>
internal static class ArtifactKindSyntax
{
    public static bool TryParse(string? value, out ArtifactKind kind)
    {
        kind = ArtifactKind.Unknown;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse(candidate, ignoreCase: true, out kind)
               && kind != ArtifactKind.Unknown;
    }

    public static string Format(ArtifactKind kind)
    {
        IEnumerable<string> parts = kind.ToString()
            .Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? "-" + char.ToLowerInvariant(character)
                    : char.ToLowerInvariant(character).ToString());

        return string.Concat(parts);
    }
}
