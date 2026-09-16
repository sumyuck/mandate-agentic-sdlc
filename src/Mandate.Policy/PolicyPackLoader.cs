using System.Collections.Immutable;
using Mandate.Core.Policies;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mandate.Policy;

/// <summary>Raised when a policy pack cannot be read.</summary>
public sealed class PolicyFormatException : Exception
{
    /// <summary>Creates the exception for a problem in a named pack.</summary>
    public PolicyFormatException(string sourceName, string problem)
        : base($"{sourceName}: {problem}")
    {
        SourceName = sourceName;
        Problem = problem;
    }

    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public PolicyFormatException()
        : base("The policy pack could not be read.") => SourceName = string.Empty;

    /// <summary>Creates the exception with a message.</summary>
    public PolicyFormatException(string message)
        : base(message) => SourceName = string.Empty;

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public PolicyFormatException(string message, Exception innerException)
        : base(message, innerException) => SourceName = string.Empty;

    /// <summary>The pack the problem was found in.</summary>
    public string SourceName { get; }

    /// <summary>The problem, without the location prefix.</summary>
    public string? Problem { get; }
}

/// <summary>
/// Reads policy packs from YAML.
/// </summary>
/// <remarks>
/// Strict, for the same reason the workflow loader is: a rule silently dropped because its
/// key was misspelled is a control that stops applying without anyone being told. That is a
/// worse outcome than the file failing to load.
/// </remarks>
public static class PolicyPackLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>Loads every pack in a directory.</summary>
    public static ImmutableArray<PolicyPack> LoadDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new PolicyFormatException(
                directory, "The policy directory does not exist.");
        }

        ImmutableArray<PolicyPack> packs =
        [
            .. Directory.GetFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(LoadFile),
        ];

        ImmutableArray<string> duplicated =
        [
            .. packs.GroupBy(pack => pack.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
        ];

        if (!duplicated.IsEmpty)
        {
            throw new PolicyFormatException(
                directory,
                "Two packs share a name: " + string.Join(", ", duplicated)
                + ". Which one a gate referred to would depend on file order.");
        }

        return packs;
    }

    /// <summary>Loads one pack.</summary>
    public static PolicyPack LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new PolicyFormatException(path, "The policy pack does not exist.");
        }

        return Load(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Parses policy pack YAML.</summary>
    public static PolicyPack Load(string yaml, string sourceName = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);

        PolicyPackDocument document;

        try
        {
            document = Deserializer.Deserialize<PolicyPackDocument>(yaml)
                       ?? throw new PolicyFormatException(sourceName, "The document is empty.");
        }
        catch (YamlException exception)
        {
            throw new PolicyFormatException(
                sourceName, exception.InnerException?.Message ?? exception.Message);
        }

        string name = Required(document.Name, "name", sourceName);
        string version = Required(document.Version, "version", sourceName);

        if (document.Rules is null || document.Rules.Count == 0)
        {
            throw new PolicyFormatException(
                sourceName, "The pack declares no rules, so it would enforce nothing.");
        }

        ImmutableArray<PolicyRule> rules =
            [.. document.Rules.Select(rule => MapRule(rule, sourceName))];

        ImmutableArray<string> duplicated =
        [
            .. rules.GroupBy(rule => rule.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key),
        ];

        if (!duplicated.IsEmpty)
        {
            throw new PolicyFormatException(
                sourceName,
                "Two rules share an id: " + string.Join(", ", duplicated)
                + ". A waiver naming one would be ambiguous.");
        }

        return new PolicyPack(name, version, document.Description ?? string.Empty, rules);
    }

    private static PolicyRule MapRule(PolicyRuleDocument rule, string sourceName)
    {
        string id = Required(rule.Id, "rules[].id", sourceName);

        return new PolicyRule(
            Id: id,
            Category: ParseEnum<PolicyCategory>(rule.Category, "category", id, sourceName),
            Severity: ParseEnum<PolicySeverity>(rule.Severity, "severity", id, sourceName),
            Statement: Required(rule.Statement, $"rule '{id}'.statement", sourceName),
            // A rule without a rationale cannot be sensibly waived: whoever is deciding needs
            // to know what they are overriding.
            Rationale: Required(rule.Rationale, $"rule '{id}'.rationale", sourceName),
            Check: Required(rule.Check, $"rule '{id}'.check", sourceName),
            Expression: rule.Expression ?? string.Empty);
    }

    private static string Required(string? value, string field, string sourceName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new PolicyFormatException(sourceName, $"'{field}' is required.")
            : value;

    private static TEnum ParseEnum<TEnum>(
        string? value, string field, string ruleId, string sourceName)
        where TEnum : struct, Enum
    {
        string candidate = (value ?? string.Empty)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        if (Enum.TryParse(candidate, ignoreCase: true, out TEnum parsed)
            && !string.Equals(parsed.ToString(), "Unknown", StringComparison.Ordinal))
        {
            return parsed;
        }

        IEnumerable<string> accepted = Enum.GetNames<TEnum>()
            .Where(name => !string.Equals(name, "Unknown", StringComparison.Ordinal))
            .Select(Hyphenate);

        throw new PolicyFormatException(
            sourceName,
            $"'{value}' is not a valid {field} for rule '{ruleId}'. Accepted: "
            + string.Join(", ", accepted) + ".");
    }

    private static string Hyphenate(string pascalCase) =>
        string.Concat(pascalCase.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? "-" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));
}

#pragma warning disable CA2227 // Deserialisation targets are populated by reflection.

internal sealed class PolicyPackDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    public List<PolicyRuleDocument>? Rules { get; set; }
}

internal sealed class PolicyRuleDocument
{
    public string? Id { get; set; }

    public string? Category { get; set; }

    public string? Severity { get; set; }

    public string? Statement { get; set; }

    public string? Rationale { get; set; }

    public string? Check { get; set; }

    public string? Expression { get; set; }
}

#pragma warning restore CA2227
