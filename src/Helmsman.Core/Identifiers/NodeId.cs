using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Helmsman.Core.Identifiers;

/// <summary>
/// Identifier for a node in a workflow definition.
/// </summary>
/// <remarks>
/// Node ids come from hand-written YAML and end up in file paths, git tags, log fields and
/// report anchors. Constraining them to a lowercase slug at the boundary means none of those
/// consumers has to sanitise, and a typo fails at workflow load rather than mid-run.
/// </remarks>
public readonly partial record struct NodeId
{
    private NodeId(string value) => Value = value;

    /// <summary>The canonical string form.</summary>
    public string Value { get; }

    /// <summary>Validates and wraps a node id.</summary>
    public static NodeId Parse(string value) =>
        TryParse(value, out NodeId id)
            ? id
            : throw new FormatException(
                $"'{value}' is not a valid node id. Expected a lowercase slug such as "
                + "'release-readiness': letters, digits and single dashes, 1-64 characters.");

    /// <summary>Validates and wraps a node id without throwing.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out NodeId id)
    {
        id = default;

        if (value is null || !SlugPattern().IsMatch(value))
        {
            return false;
        }

        id = new NodeId(value);
        return true;
    }

    /// <summary>True when this identifier has not been assigned.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex SlugPattern();
}
