using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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

/// <summary>
/// The markers that carry document content outside the JSON envelope.
/// </summary>
/// <remarks>
/// <para>
/// Content does not travel inside JSON, and this is the single most consequential format
/// decision in the system. Every stage here returns source code or Markdown measured in
/// kilobytes, and JSON requires all of it to be escaped: quotes, backslashes, newlines.
/// Models get that wrong often enough that it was the dominant cause of failed stages —
/// an unescaped quote 2.4 kB into a design document, a raw newline in twenty kilobytes of
/// C#, a Windows path or a regular expression eating its own backslashes. Each one threw
/// away a complete, correct answer and spent another prompt getting the same thing back.
/// </para>
/// <para>
/// So the envelope stays JSON — it is short, structured, and models write short JSON
/// reliably — and the content moves into delimited blocks where no escaping exists to get
/// wrong. The marker is deliberately unlikely to occur in real content, and a block is
/// matched to its document by path so the two cannot silently drift apart.
/// </para>
/// </remarks>
public static class DocumentBlock
{
    /// <summary>Opens a content block. Followed by a space and the document's path.</summary>
    public const string Start = "@@@MANDATE-FILE";

    /// <summary>Closes a content block, alone on its line.</summary>
    public const string End = "@@@MANDATE-END";
}

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

        json = EscapeRawControlCharacters(json);
        ImmutableDictionary<string, string> blocks = ReadContentBlocks(text);

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
            : payload.Validate(blocks);
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

    /// <summary>
    /// Escapes control characters that appear raw inside a JSON string literal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single most common way a model's answer is invalid here, and the repair is
    /// provably safe: inside a JSON string a raw newline or tab is <em>always</em> a syntax
    /// error, so escaping one can only rescue a broken document — it can never change the
    /// meaning of a valid one. Every stage in this system returns source code inside JSON,
    /// and one stray newline in eighty kilobytes of C# would otherwise throw the whole
    /// answer away and spend another prompt getting the same thing back.
    /// </para>
    /// <para>
    /// Deliberately narrow. Nothing else is repaired: a missing brace or a bad escape
    /// sequence is a genuinely ambiguous document, and guessing at the author's intent
    /// would mean committing invented content to a repository.
    /// </para>
    /// </remarks>
    public static string EscapeRawControlCharacters(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        StringBuilder repaired = new(json.Length);
        bool inString = false;
        bool escaped = false;

        foreach (char character in json)
        {
            if (escaped)
            {
                repaired.Append(character);
                escaped = false;
                continue;
            }

            if (inString)
            {
                switch (character)
                {
                    case '\\':
                        repaired.Append(character);
                        escaped = true;
                        continue;

                    case '"':
                        repaired.Append(character);
                        inString = false;
                        continue;

                    case '\n':
                        repaired.Append("\\n");
                        continue;

                    case '\r':
                        repaired.Append("\\r");
                        continue;

                    case '\t':
                        repaired.Append("\\t");
                        continue;

                    default:
                        if (char.IsControl(character))
                        {
                            repaired.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                            continue;
                        }

                        repaired.Append(character);
                        continue;
                }
            }

            if (character == '"')
            {
                inString = true;
            }

            repaired.Append(character);
        }

        return repaired.ToString();
    }

    /// <summary>
    /// Reads the delimited content blocks, keyed by the path each one declares.
    /// </summary>
    /// <remarks>
    /// Line-oriented and unforgiving about the markers, because the whole point is that
    /// nothing inside a block needs interpreting. A block's content is taken exactly as
    /// written, newlines, quotes, backslashes and all.
    /// </remarks>
    public static ImmutableDictionary<string, string> ReadContentBlocks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        ImmutableDictionary<string, string>.Builder blocks =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        string? openPath = null;
        List<string> content = [];

        foreach (string line in lines)
        {
            string trimmed = line.Trim();

            if (openPath is null)
            {
                if (trimmed.StartsWith(DocumentBlock.Start, StringComparison.Ordinal))
                {
                    openPath = trimmed[DocumentBlock.Start.Length..].Trim();

                    if (openPath.Length == 0)
                    {
                        throw new AgentResponseException(
                            $"A '{DocumentBlock.Start}' marker named no path.");
                    }

                    content.Clear();
                }

                continue;
            }

            if (string.Equals(trimmed, DocumentBlock.End, StringComparison.Ordinal))
            {
                blocks[openPath] = string.Join("\n", content);
                openPath = null;
                continue;
            }

            content.Add(line);
        }

        if (openPath is not null)
        {
            // Almost always an answer that ran out of room mid-file. Guessing where it
            // should have ended would mean committing a truncated source file.
            throw new AgentResponseException(
                $"The content block for '{openPath}' was never closed with "
                + $"'{DocumentBlock.End}'. The answer was probably cut short.");
        }

        return blocks.ToImmutable();
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

        public Dictionary<string, JsonElement>? Facts { get; set; }

        public List<DecisionPayload>? Decisions { get; set; }

        /// <summary>
        /// A fact value as text, whatever JSON type the model chose to write it in.
        /// </summary>
        /// <remarks>
        /// Context facts are strings by design — a guard compares them as text and the
        /// audit log stores them as text. A model writing <c>true</c> rather than
        /// <c>"true"</c> means exactly the same thing, and failing the stage over the
        /// quotation marks would spend a retry to be told the same thing again. Booleans
        /// are lower-cased and numbers rendered invariantly so a gate comparing them does
        /// not have to care which form arrived.
        /// </remarks>
        private static string AsText(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.Number => value.GetRawText(),

            // An object or an array is not a fact. A gate compares facts as scalars, so
            // collapsing a structure into text here would produce a value that silently
            // never matches anything.
            _ => throw new AgentResponseException(
                $"A context fact must be a string, a number or a boolean. Received "
                + $"{value.ValueKind.ToString().ToLowerInvariant()}: {Excerpt(value.GetRawText())}"),
        };

        public AgentResponse Validate(ImmutableDictionary<string, string> blocks)
        {
            ImmutableArray<AgentDocument> documents =
                [.. (Documents ?? []).Select(document => document.Validate(blocks))];

            foreach (string path in blocks.Keys.Order(StringComparer.Ordinal))
            {
                if (!documents.Any(document =>
                    string.Equals(document.Path, path, StringComparison.Ordinal)))
                {
                    throw new AgentResponseException(
                        $"A content block was supplied for '{path}', but no document in the "
                        + "JSON declares that path. Content nothing declares would be written "
                        + "into the tree with no artifact recording where it came from.");
                }
            }

            return Build(documents);
        }

        private AgentResponse Build(ImmutableArray<AgentDocument> documents) => new(
            string.IsNullOrWhiteSpace(Summary) ? "(no summary)" : Summary.Trim(),
            documents,
            (Facts ?? []).ToImmutableDictionary(
                pair => pair.Key, pair => AsText(pair.Value), StringComparer.Ordinal),
            [.. (Decisions ?? []).Select(decision => decision.Validate())]);
    }

    private sealed class DocumentPayload
    {
        public string? Kind { get; set; }

        public string? Path { get; set; }

        public string? Content { get; set; }

        public AgentDocument Validate(ImmutableDictionary<string, string> blocks)
        {
            if (string.IsNullOrWhiteSpace(Kind))
            {
                throw new AgentResponseException("A document did not say what kind it is.");
            }

            string? path = string.IsNullOrWhiteSpace(Path)
                ? null
                : Path.Trim().Replace('\\', '/');

            // Content comes from the delimited block, where nothing needs escaping. An
            // inline `content` field is still honoured — it is how the short documents and
            // every test in the suite are written — but the block wins when both exist,
            // because the block is the one that cannot have been mangled in transit.
            string? content = path is not null && blocks.TryGetValue(path, out string? block)
                ? block
                : Content;

            if (string.IsNullOrWhiteSpace(content))
            {
                throw new AgentResponseException(
                    $"The '{Kind}' document is empty. An empty artifact would pass an "
                    + $"artifact-exists gate while containing nothing to review. Supply its "
                    + $"content in a '{DocumentBlock.Start} {path}' block.");
            }

            return new AgentDocument(Kind.Trim(), path, content);
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
