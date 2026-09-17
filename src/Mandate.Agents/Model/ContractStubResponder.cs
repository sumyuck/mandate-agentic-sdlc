using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mandate.Core.Llm;
using Mandate.Core.Serialization;

namespace Mandate.Agents.Model;

/// <summary>
/// Answers any agent prompt with a structurally valid, entirely substanceless response.
/// </summary>
/// <remarks>
/// <para>
/// This exists so the full lifecycle can be walked with no API key, no recordings and no
/// network — which is what the engine's own tests need, and what someone evaluating the
/// orchestration wants before they care what a model said. It reads the output contract the
/// prompt states and satisfies it exactly: the declared document kinds, the declared context
/// facts, nothing else.
/// </para>
/// <para>
/// It fabricates shape, never substance, and every document it produces says so in its first
/// line. That is deliberate and not negotiable: a stub that emitted plausible-looking
/// requirements would make a stubbed run indistinguishable from a real one at a glance, and
/// someone would eventually mistake the first for the second. Responses are also marked
/// <see cref="LlmResponseSource.Stub"/> all the way into the audit log.
/// </para>
/// <para>
/// Reading the contract back out of the rendered prompt is frank re-parsing of text this
/// system generated a moment earlier. It is the right trade for a test double: the
/// alternative is a second table of what each stage produces, which would drift from the
/// workflow and quietly stop testing what it claims to.
/// </para>
/// </remarks>
public static partial class ContractStubResponder
{
    /// <summary>The line every stubbed document opens with.</summary>
    public const string Marker = "STUB: no model produced this. Structure only, no judgment.";

    /// <summary>Builds a contract-satisfying answer for a rendered prompt.</summary>
    public static string Respond(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string prompt = request.System + "\n" + string.Join("\n", request.Messages.Select(m => m.Text));

        ImmutableArray<string> kinds = ListAfter(DocumentKinds(), prompt);
        ImmutableArray<string> facts = ListAfter(ContextKeys(), prompt);

        StringBuilder json = new();
        json.Append("{\"summary\":")
            .Append(Quote($"Stubbed {request.PromptId}: produced {kinds.Length} document(s)."))
            .Append(",\"documents\":[");

        for (int index = 0; index < kinds.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            string kind = kinds[index];

            json.Append("{\"kind\":").Append(Quote(kind))
                .Append(",\"path\":").Append(Quote(PathFor(kind, request.PromptId)))
                .Append('}');
        }

        json.Append("],\"facts\":{");

        for (int index = 0; index < facts.Length; index++)
        {
            if (index > 0)
            {
                json.Append(',');
            }

            json.Append(Quote(facts[index])).Append(':').Append(Quote(ValueFor(facts[index])));
        }

        json.Append("},\"decisions\":[]}");

        // Content follows the envelope in delimited blocks, exactly as a real answer does.
        // A stub that used a different format would stop testing the parser that matters.
        foreach (string kind in kinds)
        {
            json.AppendLine().AppendLine()
                .Append(DocumentBlock.Start).Append(' ')
                .AppendLine(PathFor(kind, request.PromptId))
                .AppendLine(ContentFor(kind, request).TrimEnd())
                .Append(DocumentBlock.End);
        }

        return json.ToString();
    }

    /// <summary>
    /// A value that will satisfy the gate reading this key.
    /// </summary>
    /// <remarks>
    /// Chosen so a stubbed run reaches the end and exercises the whole graph, which is the
    /// only reason this class exists. The values are not measurements and a stubbed run's
    /// metrics should never be quoted as if they were — the run's events record that a stub
    /// answered, so the distinction survives into the evidence.
    /// </remarks>
    private static string ValueFor(string key) => key switch
    {
        "requirements.ambiguity-score" => "0.10",
        "test.coverage" => "0.90",
        "test.failures" => "0",
        "review.findings" => "0",
        "review.highest-severity" => "none",
        "security.findings" => "0",
        "security.secrets-found" => "false",
        "impact.blast-radius" => "low",
        "implementation.builds" => "true",
        "implementation.files-changed" => "1",
        "release.decision" => "go",
        _ when key.EndsWith(".recorded", StringComparison.Ordinal)
               || key.EndsWith(".written", StringComparison.Ordinal)
               || key.EndsWith(".clarified", StringComparison.Ordinal) => "true",
        _ => "stub",
    };

    /// <summary>
    /// Where a stubbed document belongs, or null when it is a record about the run.
    /// </summary>
    /// <remarks>
    /// Mirrors the paths the real prompts ask for, so a stubbed run produces a tree shaped
    /// like a real one and the workspace, commit and rollback machinery is genuinely
    /// exercised rather than skipped for want of files.
    /// </remarks>
    private static string PathFor(string kind, string promptId) => kind switch
    {
        "request" => "docs/request.md",
        "requirement-spec" => "docs/requirements.md",
        "ambiguity-report" => "docs/ambiguity.md",
        "clarification-request" => "docs/clarifications.md",
        "impact-analysis" => "docs/impact-analysis.md",
        "design-doc" => "docs/design.md",
        "architecture-decision-record" => "docs/adr/0001-stub.md",
        "api-contract" => "contracts/openapi.yaml",
        "source-patch" => $"src/{promptId}.cs",
        "test-suite" => $"tests/{promptId}.Tests.cs",
        "documentation" => "README.md",

        // Records about the run still go into the tree, under their own directory. A
        // document with nowhere to live is content the run hashed and then discarded.
        _ => $"docs/mandate/{kind}.md",
    };

    private static string ContentFor(string kind, LlmRequest request)
    {
        StringBuilder content = new();
        content.Append("// ").AppendLine(Marker);
        content.Append("// kind: ").AppendLine(kind);
        content.Append("// prompt: ").Append(request.PromptId).Append('.').AppendLine(request.PromptVersion);
        content.Append("// model requested: ").AppendLine(request.Model);
        content.Append("// request fingerprint: ").AppendLine(request.Fingerprint.Hex);

        // C# needs to compile if it is ever built; everything else is read, not parsed.
        if (kind is "source-patch" or "test-suite")
        {
            content.AppendLine();
            content.AppendLine("namespace Stub;");
            content.AppendLine();
            content.Append("// Produced by the contract stub for ")
                .Append(request.PromptId)
                .AppendLine(". Deliberately inert.");
        }

        return content.ToString();
    }

    /// <summary>
    /// Pulls a bolded, comma-separated list out of the rendered contract.
    /// </summary>
    /// <remarks>
    /// Returns empty when the prompt states no such list, which is not an error: the
    /// connectivity-check prompt has no contract, and the caller's validation is what
    /// decides whether an empty list is acceptable for a given node.
    /// </remarks>
    private static ImmutableArray<string> ListAfter(Regex pattern, string prompt)
    {
        Match match = pattern.Match(prompt);

        if (!match.Success)
        {
            return [];
        }

        return
        [
            .. match.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => entry.Length > 0),
        ];
    }

    private static string Quote(string value) =>
        JsonSerializer.Serialize(value, MandateJson.Canonical);

    [GeneratedRegex(
        @"document kinds, all of them and nothing else: \*\*([^*]+)\*\*",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DocumentKinds();

    [GeneratedRegex(
        @"facts, all of them and nothing else: \*\*([^*]+)\*\*",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ContextKeys();
}
