using System.Globalization;
using System.Text.RegularExpressions;

namespace Mandate.Workflows;

/// <summary>
/// Parses the compact duration form used in workflow files, such as <c>90s</c>, <c>5m</c>, <c>1h</c>.
/// </summary>
/// <remarks>
/// <see cref="TimeSpan.Parse(string, IFormatProvider)"/> would require <c>00:05:00</c>, which
/// is easy to mistype and hard to scan in a file meant to be reviewed by humans. The compact
/// form is also unambiguous about units, which <c>5</c> alone would not be.
/// </remarks>
public static partial class DurationSyntax
{
    /// <summary>Parses a duration, throwing when malformed.</summary>
    public static TimeSpan Parse(string value) =>
        TryParse(value, out TimeSpan duration)
            ? duration
            : throw new FormatException(
                $"'{value}' is not a valid duration. Expected a positive number followed by a "
                + "unit: 's' seconds, 'm' minutes or 'h' hours - for example '45s', '5m', '2h'.");

    /// <summary>Parses a duration without throwing.</summary>
    public static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = default;

        if (value is null)
        {
            return false;
        }

        Match match = Pattern().Match(value.Trim());

        if (!match.Success)
        {
            return false;
        }

        if (!double.TryParse(
                match.Groups["amount"].ValueSpan,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double amount)
            || amount <= 0)
        {
            return false;
        }

        duration = match.Groups["unit"].Value switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => default,
        };

        return duration > TimeSpan.Zero;
    }

    /// <summary>Renders a duration back into the compact form.</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration.TotalHours >= 1 && duration.TotalHours % 1 == 0)
        {
            return $"{duration.TotalHours:0}h";
        }

        if (duration.TotalMinutes >= 1 && duration.TotalMinutes % 1 == 0)
        {
            return $"{duration.TotalMinutes:0}m";
        }

        return $"{duration.TotalSeconds:0.##}s";
    }

    [GeneratedRegex(
        @"^(?<amount>\d+(\.\d+)?)(?<unit>[smh])$",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}
