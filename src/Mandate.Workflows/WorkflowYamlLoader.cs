using System.Collections.Immutable;
using System.Globalization;
using Mandate.Core.Artifacts;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mandate.Workflows;

/// <summary>
/// Reads a workflow file into a <see cref="WorkflowDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// Loading is strict by design. An unknown key, a misspelled enum value, a malformed duration
/// or an unparseable guard is an error naming the file, the line and the accepted values —
/// never a silently ignored setting. A workflow whose retry policy was quietly dropped
/// because the key was misspelled would be a governance failure that no test would catch.
/// </para>
/// <para>
/// Loading produces a definition; it does not decide whether that definition can run. Use
/// <see cref="WorkflowGraph.Build"/> for that. The separation matters because the two failures
/// need different fixes: a format error is a typo, a validation error is a design problem.
/// </para>
/// </remarks>
public static class WorkflowYamlLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(HyphenatedNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .Build();

    /// <summary>Loads and parses a workflow file.</summary>
    /// <exception cref="WorkflowFormatException">The file is missing or not a valid workflow.</exception>
    public static WorkflowDefinition LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new WorkflowFormatException(path, "The workflow file does not exist.");
        }

        return Load(File.ReadAllText(path), Path.GetFileName(path));
    }

    /// <summary>Parses workflow YAML.</summary>
    /// <param name="yaml">The document text.</param>
    /// <param name="sourceName">Name used in error messages.</param>
    /// <exception cref="WorkflowFormatException">The document is not a valid workflow.</exception>
    public static WorkflowDefinition Load(string yaml, string sourceName = "<inline>")
    {
        ArgumentNullException.ThrowIfNull(yaml);

        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw new WorkflowFormatException(sourceName, "The workflow document is empty.");
        }

        WorkflowDocument document = Deserialize(yaml, sourceName);

        if (document.Nodes is null || document.Nodes.Count == 0)
        {
            throw new WorkflowFormatException(sourceName, "The workflow declares no 'nodes'.");
        }

        ImmutableArray<WorkflowNode> nodes =
            [.. document.Nodes.Select(node => MapNode(node, document, sourceName))];

        ImmutableArray<WorkflowEdge> edges = document.Edges is null
            ? []
            : [.. document.Edges.Select(edge => MapEdge(edge, sourceName))];

        return new WorkflowDefinition(
            Name: Required(document.Name, "name", sourceName),
            Version: Required(document.Version, "version", sourceName),
            Description: document.Description ?? string.Empty,
            Nodes: nodes,
            Edges: edges);
    }

    /// <summary>Loads a file and builds the executable graph in one step.</summary>
    /// <exception cref="WorkflowFormatException">The file is not a valid workflow.</exception>
    /// <exception cref="WorkflowValidationException">The workflow cannot be executed.</exception>
    public static WorkflowGraph LoadGraph(string path) => WorkflowGraph.Build(LoadFile(path));

    private static WorkflowDocument Deserialize(string yaml, string sourceName)
    {
        try
        {
            return Deserializer.Deserialize<WorkflowDocument>(yaml)
                   ?? throw new WorkflowFormatException(sourceName, "The document is not a mapping.");
        }
        catch (YamlException exception)
        {
            // YamlDotNet's own message is precise about unknown keys and duplicates; keep it,
            // and add the line so a reviewer can go straight there.
            int? line = exception.Start.Line == 0 ? null : (int)exception.Start.Line;

            throw new WorkflowFormatException(sourceName, Describe(exception), line);
        }
    }

    private static string Describe(YamlException exception)
    {
        string message = exception.InnerException?.Message ?? exception.Message;

        return message.Contains("not found on type", StringComparison.Ordinal)
            ? message + " Workflow keys are hyphenated, for example 'entry-gate', "
                      + "'max-attempts', 'produces-context'."
            : message;
    }

    private static WorkflowNode MapNode(
        NodeDocument node, WorkflowDocument document, string sourceName)
    {
        string id = Required(node.Id, "nodes[].id", sourceName);
        string where = $"node '{id}'";

        return new WorkflowNode(
            Id: ParseNodeId(id, sourceName),
            Stage: ParseEnum<SdlcStage>(node.Stage, "stage", where, sourceName),
            Agent: Required(node.Agent, $"{where}.agent", sourceName),
            Description: node.Description ?? string.Empty,
            EntryGate: MapGates(node.EntryGate, where, "entry-gate", sourceName),
            ExitGate: MapGates(node.ExitGate, where, "exit-gate", sourceName),
            Retry: MapRetry(node.Retry, where, sourceName),
            Autonomy: ParseEnum<AutonomyLevel>(node.Autonomy, "autonomy", where, sourceName),
            Approvals: MapApprovals(node.Approvals, where, sourceName),
            Join: node.Join is null
                ? WorkflowDefaults.Join
                : ParseEnum<JoinPolicy>(node.Join, "join", where, sourceName),
            QuorumSize: node.QuorumSize ?? 0,
            Timeout: node.Timeout is null
                ? WorkflowDefaults.Timeout
                : ParseDuration(node.Timeout, $"{where}.timeout", sourceName),
            Compensation: node.Compensation,
            Model: node.Model ?? document.DefaultModel,
            Produces: MapArtifactKinds(node.Produces, where, sourceName),
            ProducesContext: MapContextKeys(node.ProducesContext, where, sourceName));
    }

    private static ImmutableArray<GateCondition> MapGates(
        List<GateDocument>? gates, string where, string field, string sourceName)
    {
        if (gates is null)
        {
            return [];
        }

        return
        [
            .. gates.Select(gate => new GateCondition(
                Kind: Required(gate.Kind, $"{where}.{field}[].kind", sourceName),
                Expression: gate.Expression ?? string.Empty,
                Description: gate.Description ?? string.Empty)),
        ];
    }

    private static RetryPolicy MapRetry(RetryDocument? retry, string where, string sourceName)
    {
        if (retry is null)
        {
            return RetryPolicy.None;
        }

        return new RetryPolicy(
            MaxAttempts: retry.MaxAttempts ?? 1,
            InitialBackoff: retry.InitialBackoff is null
                ? TimeSpan.Zero
                : ParseDuration(retry.InitialBackoff, $"{where}.retry.initial-backoff", sourceName),
            BackoffMultiplier: retry.BackoffMultiplier ?? 1d,
            MaxBackoff: retry.MaxBackoff is null
                ? TimeSpan.Zero
                : ParseDuration(retry.MaxBackoff, $"{where}.retry.max-backoff", sourceName),
            JitterRatio: retry.JitterRatio ?? 0d,
            OnExhaustion: retry.OnExhaustion is null
                ? FallbackStrategy.FailNode
                : ParseEnum<FallbackStrategy>(
                    retry.OnExhaustion, "retry.on-exhaustion", where, sourceName));
    }

    private static ImmutableArray<ApprovalRequirement> MapApprovals(
        List<ApprovalDocument>? approvals, string where, string sourceName)
    {
        if (approvals is null)
        {
            return [];
        }

        return
        [
            .. approvals.Select(approval => new ApprovalRequirement(
                Role: Required(approval.Role, $"{where}.approvals[].role", sourceName),
                Reason: Required(approval.Reason, $"{where}.approvals[].reason", sourceName),
                // Defaults to enforced. An approval that may be granted by whoever produced
                // the work is the weaker control, so it has to be asked for explicitly.
                SegregationOfDuties: approval.SegregationOfDuties ?? true)),
        ];
    }

    private static ImmutableArray<ArtifactKind> MapArtifactKinds(
        List<string>? kinds, string where, string sourceName) =>
        kinds is null
            ? []
            : [.. kinds.Select(kind => ParseEnum<ArtifactKind>(kind, "produces[]", where, sourceName))];

    private static ImmutableArray<string> MapContextKeys(
        List<string>? keys, string where, string sourceName)
    {
        if (keys is null)
        {
            return [];
        }

        foreach (string key in keys)
        {
            if (!Core.Context.ContextFact.IsValidKey(key))
            {
                throw new WorkflowFormatException(
                    sourceName,
                    $"'{key}' in {where}.produces-context is not a valid context key. Expected "
                    + "dot-separated lowercase segments, such as 'requirements.scope'.");
            }
        }

        return [.. keys];
    }

    private static WorkflowEdge MapEdge(EdgeDocument edge, string sourceName)
    {
        string from = Required(edge.From, "edges[].from", sourceName);
        string to = Required(edge.To, "edges[].to", sourceName);
        string where = $"edge '{from}' -> '{to}'";

        return new WorkflowEdge(
            From: ParseNodeId(from, sourceName),
            To: ParseNodeId(to, sourceName),
            Kind: edge.Kind is null
                ? WorkflowDefaults.Edge
                : ParseEnum<EdgeKind>(edge.Kind, "kind", where, sourceName),
            Guard: string.IsNullOrWhiteSpace(edge.Guard) ? null : edge.Guard.Trim(),
            On: edge.On is null
                ? LoopBackTrigger.Unknown
                : ParseEnum<LoopBackTrigger>(edge.On, "on", where, sourceName));
    }

    private static string Required(string? value, string field, string sourceName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new WorkflowFormatException(sourceName, $"'{field}' is required.")
            : value;

    private static NodeId ParseNodeId(string value, string sourceName) =>
        NodeId.TryParse(value, out NodeId id)
            ? id
            : throw new WorkflowFormatException(
                sourceName,
                $"'{value}' is not a valid node id. Expected a lowercase slug such as "
                + "'release-readiness'.");

    private static TimeSpan ParseDuration(string value, string field, string sourceName) =>
        DurationSyntax.TryParse(value, out TimeSpan duration)
            ? duration
            : throw new WorkflowFormatException(
                sourceName,
                $"'{value}' in '{field}' is not a valid duration. Expected a positive number "
                + "with a unit: '45s', '5m', '2h'.");

    private static TEnum ParseEnum<TEnum>(
        string? value, string field, string where, string sourceName)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkflowFormatException(sourceName, $"'{where}.{field}' is required.");
        }

        // Hyphenated YAML ('impact-analysis') maps onto PascalCase members ('ImpactAnalysis').
        // 'on: success' reads better in a file than 'on: on-success'.
        string candidate = value.Replace("-", string.Empty, StringComparison.Ordinal);

        if (typeof(TEnum) == typeof(LoopBackTrigger))
        {
            candidate = "On" + candidate;
        }

        if (Enum.TryParse(candidate, ignoreCase: true, out TEnum parsed)
            && !IsUnknownMember(parsed))
        {
            return parsed;
        }

        IEnumerable<string> accepted = Enum.GetNames<TEnum>()
            .Where(name => !string.Equals(name, "Unknown", StringComparison.Ordinal))
            .Select(ToHyphenated);

        throw new WorkflowFormatException(
            sourceName,
            $"'{value}' is not a valid {field} for {where}. Accepted values: "
            + string.Join(", ", accepted) + ".");
    }

    private static bool IsUnknownMember<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        string.Equals(value.ToString(), "Unknown", StringComparison.Ordinal);

    private static string ToHyphenated(string pascalCase)
    {
        IEnumerable<string> parts = pascalCase
            .Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? "-" + char.ToLower(character, CultureInfo.InvariantCulture)
                    : char.ToLower(character, CultureInfo.InvariantCulture).ToString());

        return string.Concat(parts);
    }
}
