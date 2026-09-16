using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Context;

/// <summary>
/// One attributed fact contributed to a run's shared context.
/// </summary>
/// <remarks>
/// Facts are never overwritten. Contributing a new value for a key appends a revision, so the
/// context carries its own history: "the scope changed after the design was signed off" is a
/// question the context can answer, which is what makes re-planning explicable after the fact.
/// </remarks>
/// <param name="Key">Namespaced key, such as <c>requirements.scope</c>.</param>
/// <param name="Value">The value, as canonical JSON or plain text.</param>
/// <param name="ProducedByNode">The node that contributed it.</param>
/// <param name="ProducedBy">The actor that contributed it.</param>
/// <param name="RecordedAt">When it was contributed.</param>
/// <param name="Evidence">Artifacts supporting the fact.</param>
public sealed partial record ContextFact(
    string Key,
    string Value,
    NodeId ProducedByNode,
    Actor ProducedBy,
    DateTimeOffset RecordedAt,
    ImmutableArray<Sha256Hash> Evidence)
{
    /// <summary>Creates a fact, validating its key.</summary>
    public static ContextFact Create(
        string key,
        string value,
        NodeId producedByNode,
        Actor producedBy,
        DateTimeOffset recordedAt,
        IEnumerable<Sha256Hash>? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!IsValidKey(key))
        {
            throw new FormatException(
                $"'{key}' is not a valid context key. Expected dot-separated lowercase "
                + "segments, such as 'requirements.acceptance-criteria'.");
        }

        if (producedBy.IsEmpty)
        {
            throw new ArgumentException(
                "A context fact must name its contributor.", nameof(producedBy));
        }

        return new ContextFact(
            key, value, producedByNode, producedBy, recordedAt,
            evidence is null ? [] : [.. evidence.Distinct()]);
    }

    /// <summary>True when <paramref name="key"/> is a well-formed namespaced key.</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>The leading namespace segment, used for scoping.</summary>
    public string Namespace
    {
        get
        {
            int separator = Key.IndexOf('.', StringComparison.Ordinal);
            return separator < 0 ? Key : Key[..separator];
        }
    }

    [GeneratedRegex(
        "^[a-z0-9]+(-[a-z0-9]+)*(\\.[a-z0-9]+(-[a-z0-9]+)*)*$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex KeyPattern();
}
