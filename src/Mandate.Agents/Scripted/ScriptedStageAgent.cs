using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Decisions;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;

namespace Mandate.Agents.Scripted;

/// <summary>How a scripted agent should behave, for exercising the engine.</summary>
/// <param name="FailOnAttempt">
/// Fail on this attempt number, <c>0</c> to fail every attempt, or <see langword="null"/> to
/// always succeed. Lets retry, fallback and compensation be driven deterministically instead
/// of waiting for a real agent to misbehave.
/// </param>
/// <param name="Failure">The failure message to report.</param>
/// <param name="ContextOverrides">
/// Context values to emit instead of the conventional ones — used to make a gate fail on
/// purpose, for instance by reporting coverage below the threshold.
/// </param>
/// <param name="Delay">Artificial work, so concurrency can be observed.</param>
public sealed record ScriptedBehaviour(
    int? FailOnAttempt = null,
    string Failure = "Scripted failure.",
    ImmutableDictionary<string, string>? ContextOverrides = null,
    TimeSpan? Delay = null)
{
    /// <summary>Always succeeds, with conventional output.</summary>
    public static ScriptedBehaviour Default { get; } = new();

    /// <summary>Fails every attempt.</summary>
    public static ScriptedBehaviour AlwaysFails(string failure) =>
        new(FailOnAttempt: 0, Failure: failure);

    /// <summary>Fails the first attempt and succeeds afterwards.</summary>
    public static ScriptedBehaviour FailsOnce(string failure) =>
        new(FailOnAttempt: 1, Failure: failure);

    /// <summary>Succeeds, but reports the given context values.</summary>
    public static ScriptedBehaviour Reporting(params (string Key, string Value)[] values) =>
        new(ContextOverrides: values.ToImmutableDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
}

/// <summary>
/// A stage agent that produces deterministic output derived from the node's own declaration.
/// </summary>
/// <remarks>
/// <para>
/// This is how the engine is exercised end to end before any model is involved. It is not a
/// mock in the usual sense: it produces genuine content-addressed artifacts with real
/// provenance, contributes the context facts the node declares, and records decisions — so the
/// gates, the lineage and the governance machinery are all doing real work on real data.
/// </para>
/// <para>
/// It also keeps every later part honest. Because the engine's behaviour is pinned by tests
/// that run against scripted agents, a change that only works because a model happened to
/// cooperate will fail.
/// </para>
/// </remarks>
public sealed class ScriptedStageAgent(string id, ScriptedBehaviour? behaviour = null) : IStageAgent
{
    private readonly ScriptedBehaviour _behaviour = behaviour ?? ScriptedBehaviour.Default;

    /// <inheritdoc />
    public string Id { get; } = id;

    /// <inheritdoc />
    public async Task<StageResult> ExecuteAsync(
        StageExecution execution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);

        if (_behaviour.Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (_behaviour.FailOnAttempt is { } failOn && (failOn == 0 || failOn == execution.Attempt))
        {
            return StageResult.Failed(_behaviour.Failure);
        }

        ImmutableArray<Artifact> artifacts = ProduceArtifacts(execution);
        ImmutableArray<ContextFact> facts = ProduceFacts(execution, artifacts);
        ImmutableArray<Decision> decisions = ProduceDecisions(execution, artifacts);

        return StageResult.Success(artifacts, facts, decisions);
    }

    private static ImmutableArray<Artifact> ProduceArtifacts(StageExecution execution)
    {
        WorkflowNode node = execution.Node;

        // Derived from the inputs the stage was actually given, so the provenance graph is
        // genuine rather than decorative.
        ImmutableArray<Sha256Hash> inputs = [.. execution.Inputs.Select(artifact => artifact.Hash)];

        return
        [
            .. node.Produces.Select(kind => Artifact.FromContent(
                kind,
                name: $"{node.Id}/{Hyphenate(kind.ToString())}.txt",
                mediaType: "text/plain",
                content: Encoding.UTF8.GetBytes(Body(execution, kind, inputs)),
                producedByNode: node.Id,
                producedBy: execution.Actor,
                producedAt: DateTimeOffset.UnixEpoch,
                derivedFrom: inputs)),
        ];
    }

    private static string Body(
        StageExecution execution, ArtifactKind kind, ImmutableArray<Sha256Hash> inputs)
    {
        StringBuilder body = new();
        body.AppendLine(CultureInfo.InvariantCulture, $"kind: {kind}");
        body.AppendLine(CultureInfo.InvariantCulture, $"stage: {execution.Node.Stage}");
        body.AppendLine(CultureInfo.InvariantCulture, $"node: {execution.Node.Id}");
        body.AppendLine(CultureInfo.InvariantCulture, $"agent: {execution.Actor}");
        body.AppendLine(CultureInfo.InvariantCulture, $"model: {execution.Node.Model ?? "none"}");
        body.AppendLine(CultureInfo.InvariantCulture, $"derived-from: {inputs.Length} artifact(s)");

        foreach (Sha256Hash input in inputs)
        {
            body.AppendLine(CultureInfo.InvariantCulture, $"  - {input.Hex}");
        }

        return body.ToString();
    }

    private ImmutableArray<ContextFact> ProduceFacts(
        StageExecution execution, ImmutableArray<Artifact> artifacts)
    {
        ImmutableArray<Sha256Hash> evidence = [.. artifacts.Select(artifact => artifact.Hash)];

        return
        [
            .. execution.Node.ProducesContext.Select(key => ContextFact.Create(
                key,
                ValueFor(key, execution),
                execution.Node.Id,
                execution.Actor,
                DateTimeOffset.UnixEpoch,
                evidence)),
        ];
    }

    private string ValueFor(string key, StageExecution execution) =>
        _behaviour.ContextOverrides?.TryGetValue(key, out string? overridden) == true
            ? overridden
            : ScriptedEvidence.For(key, execution);

    private static ImmutableArray<Decision> ProduceDecisions(
        StageExecution execution, ImmutableArray<Artifact> artifacts)
    {
        // Only where the stage genuinely chose something. A decision recorded at every stage
        // would be noise, and would make the decision log worth less rather than more.
        if (!execution.Node.Produces.Contains(ArtifactKind.ArchitectureDecisionRecord))
        {
            return [];
        }

        return
        [
            Decision.Record(
                id: $"{execution.Node.Id}-001",
                runId: execution.RunId,
                nodeId: execution.Node.Id,
                question: "How should the change be structured?",
                options:
                [
                    new DecisionOption(
                        "incremental",
                        "Extend the existing components in place.",
                        RejectedBecause: null),
                    new DecisionOption(
                        "rewrite",
                        "Replace the affected components.",
                        "Blast radius exceeds the requirement; the change does not justify it."),
                ],
                rationale: "The requirement is additive, so extending in place keeps the blast "
                           + "radius proportionate to the change.",
                confidence: 0.8,
                authority: DecisionAuthority.Agent,
                decidedBy: execution.Actor,
                decidedAt: DateTimeOffset.UnixEpoch,
                evidence: artifacts.Select(artifact => artifact.Hash)),
        ];
    }

    private static string Hyphenate(string pascalCase)
    {
        IEnumerable<string> parts = pascalCase
            .Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? "-" + char.ToLowerInvariant(character)
                    : char.ToLowerInvariant(character).ToString());

        return string.Concat(parts);
    }
}
