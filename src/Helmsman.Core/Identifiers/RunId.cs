using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Helmsman.Core.Identifiers;

/// <summary>
/// Identifier for a single orchestrated run.
/// </summary>
/// <remarks>
/// The textual form is deliberately time-ordered — <c>run_20260916T142500Z_a1b2c3</c> — so
/// that runs sort chronologically as directory names and as database keys without needing a
/// separate timestamp column. The random suffix keeps two runs started in the same second
/// distinct.
/// </remarks>
public readonly record struct RunId
{
    private const string Prefix = "run_";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private RunId(string value) => Value = value;

    /// <summary>The canonical string form.</summary>
    public string Value { get; }

    /// <summary>Mints a new identifier for a run starting at <paramref name="startedAt"/>.</summary>
    public static RunId New(DateTimeOffset startedAt, string randomSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(randomSuffix);

        string timestamp = startedAt.ToUniversalTime()
            .ToString(TimestampFormat, CultureInfo.InvariantCulture);

        return new RunId($"{Prefix}{timestamp}_{randomSuffix.ToLowerInvariant()}");
    }

    /// <summary>Parses a canonical identifier, throwing when the form is not recognised.</summary>
    public static RunId Parse(string value) =>
        TryParse(value, out RunId id)
            ? id
            : throw new FormatException($"'{value}' is not a valid run id.");

    /// <summary>Parses a canonical identifier without throwing.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out RunId id)
    {
        id = default;

        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string[] parts = value[Prefix.Length..].Split('_');
        if (parts.Length != 2 || parts[1].Length == 0)
        {
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                parts[0],
                TimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
        {
            return false;
        }

        id = new RunId(value);
        return true;
    }

    /// <summary>True when this identifier has not been assigned.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}
