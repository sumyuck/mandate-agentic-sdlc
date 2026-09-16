using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mandate.Core.Serialization;

namespace Mandate.Agents.Model;

/// <summary>
/// The shape every model-backed stage must answer in.
/// </summary>
/// <remarks>
/// <para>
/// One envelope for all eleven agents, deliberately. The engine's contract with a stage is
/// exactly "artifacts, context facts, decisions, files", so the model's contract mirrors it
/// one-for-one. Eleven bespoke schemas would be eleven parsers to keep in step with one
/// interface, and the differences between stages belong in their prompts — which is where a
/// reviewer looks for them — not in their wire formats.
/// </para>
/// <para>
/// A document carries both the content and where it belongs in the tree, because those are
/// the same decision. The engine hashes the content into an artifact and commits the file,
/// so what the audit log records and what the repository contains are the same bytes rather
/// than two accounts of them.
/// </para>
/// </remarks>
/// <param name="Summary">One line, for the run timeline.</param>
/// <param name="Documents">What the stage produced.</param>
/// <param name="Facts">Context values the stage contributes, keyed as the workflow declares.</param>
/// <param name="Decisions">Choices made, with the options rejected.</param>
public sealed record AgentResponse(
    string Summary,
    ImmutableArray<AgentDocument> Documents,
    ImmutableDictionary<string, string> Facts,
    ImmutableArray<AgentDecision> Decisions);

/// <summary>Something a stage produced, and where it belongs.</summary>
/// <param name="Kind">The artifact kind, hyphenated as the workflow spells it.</param>
/// <param name="Path">
/// Workspace-relative path, or <see langword="null"/> when the document is a record about
/// the run rather than part of the software being built.
/// </param>
/// <param name="Content">The document itself.</param>
public sealed record AgentDocument(string Kind, string? Path, string Content);

/// <summary>A choice a stage made.</summary>
/// <param name="Id">Stable identifier for the decision.</param>
/// <param name="Question">What was being decided.</param>
/// <param name="Options">Every option weighed, including the ones rejected.</param>
/// <param name="Chosen">The name of the option taken.</param>
/// <param name="Rationale">Why.</param>
/// <param name="Confidence">How sure, from 0 to 1.</param>
public sealed record AgentDecision(
    string Id,
    string Question,
    ImmutableArray<AgentOption> Options,
    string Chosen,
    string Rationale,
    double Confidence);

/// <summary>One option a stage weighed.</summary>
/// <param name="Name">Short name.</param>
/// <param name="Summary">What it would mean.</param>
/// <param name="RejectedBecause">Why it was not taken, or null for the chosen option.</param>
public sealed record AgentOption(string Name, string Summary, string? RejectedBecause);

/// <summary>
/// Turns what a model actually said into an <see cref="AgentResponse"/>.
/// </summary>
/// <remarks>
/// Written to expect imperfect output, because that is what arrives. A model asked for JSON
/// will sometimes wrap it in a fenced block, sometimes preface it with a sentence, and
/// occasionally do both. None of those is a reason to fail a stage, so they are handled;
/// anything beyond them is a reason, and fails loudly enough to be diagnosable from the
/// audit log alone.
/// </remarks>
public static class AgentResponseParser
{
    private static readonly JsonSerializerOptions Options = BuildOptions();

    /// <summary>Parses a model's answer.</summary>
    /// <exception cref="AgentResponseException">The answer was not usable.</exception>
    public static AgentResponse Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string json = ExtractJsonObject(text)
            ?? throw new AgentResponseException(
                "The answer contained no JSON object. " + Excerpt(text));

        Payload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<Payload>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new AgentResponseException(
                $"The answer was not valid JSON: {exception.Message} " + Excerpt(text), exception);
        }

        return payload is null
            ? throw new AgentResponseException("The answer was the JSON literal null.")
            : payload.Validate();
    }

    /// <summary>
    /// Finds the outermost JSON object in a block of text.
    /// </summary>
    /// <remarks>
    /// Scans rather than pattern-matching, and tracks string literals and escapes, because a
    /// brace inside a quoted C# snippet is extremely common in this system's answers — every
    /// implementation stage returns code. A naive search for the last <c>}</c> truncates the
    /// document at the first method body.
    /// </remarks>
    public static string? ExtractJsonObject(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        int start = text.IndexOf('{', StringComparison.Ordinal);

        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int index = start; index < text.Length; index++)
        {
            char character = text[index];

            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;

                case '{':
                    depth++;
                    break;

                case '}':
                    depth--;

                    if (depth == 0)
                    {
                        return text[start..(index + 1)];
                    }

                    break;

                default:
                    break;
            }
        }

        // Unbalanced: almost always an answer the model ran out of room to finish. The
        // caller reports it as a truncated stage rather than guessing at the remainder.
        return null;
    }

    private static string Excerpt(string text) =>
        "First 200 characters: " + (text.Length <= 200 ? text : text[..200] + "…");

    private static JsonSerializerOptions BuildOptions()
    {
        JsonSerializerOptions options = new(MandateJson.Pretty)
        {
            // Models are not consistent about casing, and a stage failing because it wrote
            // "Summary" rather than "summary" would be a pointless retry costing a prompt.
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>The wire shape, before validation.</summary>
    private sealed class Payload
    {
        public string? Summary { get; set; }

        public List<DocumentPayload>? Documents { get; set; }

        public Dictionary<string, string>? Facts { get; set; }

        public List<DecisionPayload>? Decisions { get; set; }

        public AgentResponse Validate() => new(
            string.IsNullOrWhiteSpace(Summary) ? "(no summary)" : Summary.Trim(),
            [.. (Documents ?? []).Select(document => document.Validate())],
            (Facts ?? []).ToImmutableDictionary(
                pair => pair.Key, pair => pair.Value ?? string.Empty, StringComparer.Ordinal),
            [.. (Decisions ?? []).Select(decision => decision.Validate())]);
    }

    private sealed class DocumentPayload
    {
        public string? Kind { get; set; }

        public string? Path { get; set; }

        public string? Content { get; set; }

        public AgentDocument Validate()
        {
            if (string.IsNullOrWhiteSpace(Kind))
            {
                throw new AgentResponseException("A document did not say what kind it is.");
            }

            if (string.IsNullOrWhiteSpace(Content))
            {
                throw new AgentResponseException(
                    $"The '{Kind}' document is empty. An empty artifact would pass an "
                    + "artifact-exists gate while containing nothing to review.");
            }

            return new AgentDocument(
                Kind.Trim(),
                string.IsNullOrWhiteSpace(Path) ? null : Path.Trim().Replace('\\', '/'),
                Content);
        }
    }

    private sealed class DecisionPayload
    {
        public string? Id { get; set; }

        public string? Question { get; set; }

        public List<OptionPayload>? Options { get; set; }

        public string? Chosen { get; set; }

        public string? Rationale { get; set; }

        public double Confidence { get; set; } = 0.5;

        public AgentDecision Validate() => new(
            string.IsNullOrWhiteSpace(Id) ? "decision" : Id.Trim(),
            Question?.Trim() ?? string.Empty,
            [.. (Options ?? []).Select(option => option.Validate())],
            Chosen?.Trim() ?? string.Empty,
            Rationale?.Trim() ?? string.Empty,
            Math.Clamp(Confidence, 0d, 1d));
    }

    private sealed class OptionPayload
    {
        public string? Name { get; set; }

        public string? Summary { get; set; }

        public string? RejectedBecause { get; set; }

        public AgentOption Validate() => new(
            Name?.Trim() ?? string.Empty,
            Summary?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(RejectedBecause) ? null : RejectedBecause.Trim());
    }
}

/// <summary>A model's answer could not be used.</summary>
/// <remarks>
/// Caught by the agent base class and turned into a stage failure, so an unusable answer
/// goes through the same retry, fallback and compensation machinery as any other failure
/// rather than escaping as an exception that kills the run.
/// </remarks>
public sealed class AgentResponseException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentResponseException()
        : base("The model's answer could not be used.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public AgentResponseException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public AgentResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
