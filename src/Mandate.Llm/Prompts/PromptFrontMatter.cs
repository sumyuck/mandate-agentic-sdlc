using System.Collections.Immutable;
using Mandate.Core.Llm;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mandate.Llm.Prompts;

/// <summary>
/// The declared header of a prompt file.
/// </summary>
/// <remarks>
/// Front matter rather than a sidecar file so a prompt is one reviewable unit: the text and
/// the contract it satisfies cannot drift apart in separate diffs.
/// </remarks>
/// <param name="Id">The prompt's stable name.</param>
/// <param name="Version">The version, in the form <c>v1</c>.</param>
/// <param name="Description">What the prompt is for.</param>
/// <param name="Inputs">The placeholders the body is allowed to use.</param>
/// <param name="Verbatim">
/// Inputs that carry content produced upstream, exempt from the repeatability scan.
/// </param>
/// <param name="MaxOutputTokens">The output ceiling answers to this prompt need.</param>
/// <param name="Effort">How much reasoning this stage is allowed before answering.</param>
internal sealed record PromptFrontMatter(
    string Id,
    string Version,
    string Description,
    ImmutableArray<string> Inputs,
    ImmutableArray<string> Verbatim,
    int MaxOutputTokens,
    LlmEffort Effort)
{
    private const string Fence = "---";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        // Unmatched keys throw (YamlDotNet's default, kept deliberately) and duplicates are
        // rejected. A misspelled `max-output-token` must not fall back to a default and
        // quietly truncate every answer this prompt produces. Same settings as the workflow
        // loader, so "strict by default" is one rule across the system rather than two.
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>Separates a prompt file's front matter from its body.</summary>
    /// <exception cref="PromptFormatException">The header is missing or unusable.</exception>
    public static (PromptFrontMatter Matter, string Body) Split(string path, string text)
    {
        string normalised = text.ReplaceLineEndings("\n");

        if (!normalised.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            throw new PromptFormatException(
                $"{path}: a prompt must open with a '---' front-matter fence.");
        }

        int close = normalised.IndexOf("\n" + Fence + "\n", StringComparison.Ordinal);

        if (close < 0)
        {
            throw new PromptFormatException($"{path}: the front matter is never closed.");
        }

        string header = normalised[(Fence.Length + 1)..close];
        string body = normalised[(close + Fence.Length + 2)..];

        Header parsed;

        try
        {
            parsed = Yaml.Deserialize<Header>(header)
                ?? throw new PromptFormatException($"{path}: the front matter is empty.");
        }
        catch (YamlException exception)
        {
            throw new PromptFormatException(
                $"{path}: the front matter is not valid YAML. {exception.Message}", exception);
        }

        return (parsed.Validate(path), body);
    }

    /// <summary>The deserialisation shape. Validated into the record above.</summary>
    private sealed class Header
    {
        public string? Id { get; set; }

        public string? Version { get; set; }

        public string? Description { get; set; }

        public List<string>? Inputs { get; set; }

        public List<string>? Verbatim { get; set; }

        public int MaxOutputTokens { get; set; }

        public string? Effort { get; set; }

        public PromptFrontMatter Validate(string path)
        {
            string id = Require(path, Id, "id");
            string version = Require(path, Version, "version");
            string description = Require(path, Description, "description");

            if (!PromptLibrary.IsValidId(id))
            {
                throw new PromptFormatException(
                    $"{path}: '{id}' is not a usable prompt id. Expected a lowercase slug, "
                    + "such as 'requirements-analyst'.");
            }

            if (!PromptLibrary.IsValidVersion(version))
            {
                throw new PromptFormatException(
                    $"{path}: '{version}' is not a version. Expected 'v1', 'v2' and so on — "
                    + "ordered, so 'the latest prompt' is a fact rather than a guess.");
            }

            if (MaxOutputTokens <= 0)
            {
                throw new PromptFormatException(
                    $"{path}: 'max-output-tokens' must be declared and positive. A prompt "
                    + "knows how much room its answer needs; the caller does not.");
            }

            ImmutableArray<string> inputs = [.. Inputs ?? []];

            foreach (string input in inputs)
            {
                if (!PromptLibrary.IsValidId(input))
                {
                    throw new PromptFormatException(
                        $"{path}: '{input}' is not a usable input name. Expected a lowercase "
                        + "slug.");
                }
            }

            if (inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Length)
            {
                throw new PromptFormatException($"{path}: an input is declared twice.");
            }

            ImmutableArray<string> verbatim = [.. Verbatim ?? []];

            foreach (string exempt in verbatim)
            {
                if (!inputs.Contains(exempt, StringComparer.Ordinal))
                {
                    throw new PromptFormatException(
                        $"{path}: '{exempt}' is listed under 'verbatim' but is not an input "
                        + "of this prompt.");
                }
            }

            LlmEffort effort = LlmEffort.Unspecified;

            if (!string.IsNullOrWhiteSpace(Effort)
                && (!Enum.TryParse(Effort.Trim(), ignoreCase: true, out effort)
                    || effort == LlmEffort.Unspecified))
            {
                throw new PromptFormatException(
                    $"{path}: '{Effort}' is not a reasoning effort. Expected none, low, "
                    + "medium, high, xhigh or max.");
            }

            return new PromptFrontMatter(
                id, version, description, inputs, verbatim, MaxOutputTokens, effort);
        }

        private static string Require(string path, string? value, string field) =>
            string.IsNullOrWhiteSpace(value)
                ? throw new PromptFormatException($"{path}: '{field}' is required.")
                : value.Trim();
    }
}

/// <summary>A prompt file could not be loaded.</summary>
public sealed class PromptFormatException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PromptFormatException()
        : base("The prompt file could not be loaded.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public PromptFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PromptFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A prompt could not be rendered from the values supplied.</summary>
public sealed class PromptRenderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PromptRenderException()
        : base("The prompt could not be rendered.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public PromptRenderException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public PromptRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
