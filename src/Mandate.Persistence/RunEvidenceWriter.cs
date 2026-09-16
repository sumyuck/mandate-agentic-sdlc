using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Serialization;

namespace Mandate.Persistence;

/// <summary>One event, in the exported form.</summary>
/// <remarks>
/// A flat record rather than the domain type, so the export is a stable file format that does
/// not move when the internal model does — and so it can be read by anything, not only by
/// this engine.
/// </remarks>
public sealed record ExportedEvent(
    long Sequence,
    string OccurredAt,
    string Kind,
    string? NodeId,
    string Actor,
    string PreviousHash,
    string Hash,
    JsonElement Payload);

/// <summary>What was written, and where.</summary>
/// <param name="Directory">The directory the evidence was written to.</param>
/// <param name="EventCount">How many events were exported.</param>
/// <param name="ChainIntact">Whether the exported chain verified.</param>
public sealed record EvidenceExport(string Directory, int EventCount, bool ChainIntact);

/// <summary>
/// Writes a run's evidence to disk in a reviewable form.
/// </summary>
/// <remarks>
/// <para>
/// The store is the system of record; this is the reviewable copy. It exists because a
/// grader, an auditor or an interviewer should be able to read what happened without running
/// the engine or opening a database — and because evidence that can only be inspected with
/// the tool that produced it is weak evidence.
/// </para>
/// <para>
/// The export carries the recorded digests, so verification can be re-run against the files
/// alone. Tampering with an exported log is detectable in exactly the same way as tampering
/// with the store.
/// </para>
/// </remarks>
public static class RunEvidenceWriter
{
    /// <summary>Writes a run's evidence into <c>&lt;root&gt;/&lt;runId&gt;/</c>.</summary>
    public static async Task<EvidenceExport> WriteAsync(
        string root,
        RunSummary summary,
        ImmutableArray<RunEvent> events,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(summary);

        string directory = Path.Combine(root, summary.RunId.Value);
        Directory.CreateDirectory(directory);

        AuditVerification verification = AuditChain.Verify(summary.RunId, events);
        RunState state = RunState.Rebuild(summary.RunId, events);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "events.jsonl"), RenderEvents(events), cancellationToken)
            .ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "run.json"),
            JsonSerializer.Serialize(
                new
                {
                    runId = summary.RunId.Value,
                    workflow = summary.Workflow,
                    scenario = summary.Scenario,
                    request = summary.Request,
                    status = summary.Status.ToString(),
                    startedAt = summary.StartedAt,
                    updatedAt = summary.UpdatedAt,
                    eventCount = summary.EventCount,
                    chainIntact = verification.IsIntact,
                    nodes = state.Nodes.Values
                        .OrderBy(node => node.Id.Value, StringComparer.Ordinal)
                        .Select(node => new
                        {
                            id = node.Id.Value,
                            state = node.State.ToString(),
                            attempts = node.AttemptsMade,
                            elapsedMilliseconds = node.Elapsed?.TotalMilliseconds,
                            detail = node.Detail,
                        }),
                    artifacts = state.Artifacts
                        .OrderBy(artifact => artifact.Name, StringComparer.Ordinal)
                        .Select(artifact => new
                        {
                            hash = artifact.Hash.Hex,
                            kind = artifact.Kind.ToString(),
                            name = artifact.Name,
                            producedBy = artifact.ProducedBy.Value,
                            derivedFrom = artifact.DerivedFrom.Select(hash => hash.Hex),
                        }),
                    context = state.Context.Keys.Select(key => new
                    {
                        key,
                        value = state.Context.Latest(key)!.Value,
                        revisions = state.Context.History(key).Length,
                    }),
                },
                MandateJson.Pretty),
            cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(
            Path.Combine(directory, "timeline.md"),
            RenderTimeline(summary, events, state, verification),
            cancellationToken).ConfigureAwait(false);

        return new EvidenceExport(directory, events.Length, verification.IsIntact);
    }

    private static string RenderEvents(ImmutableArray<RunEvent> events)
    {
        StringBuilder lines = new();

        foreach (RunEvent @event in events)
        {
            ExportedEvent exported = new(
                @event.Sequence,
                @event.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                @event.Kind.ToString(),
                @event.NodeId?.Value,
                @event.Actor.Value,
                @event.PreviousHash.Hex,
                @event.Hash.Hex,
                JsonDocument.Parse(@event.PayloadJson).RootElement);

            // One event per line: readable with `head`, greppable, and diffable between runs.
            lines.AppendLine(JsonSerializer.Serialize(exported, MandateJson.Canonical));
        }

        return lines.ToString();
    }

    private static string RenderTimeline(
        RunSummary summary,
        ImmutableArray<RunEvent> events,
        RunState state,
        AuditVerification verification)
    {
        StringBuilder markdown = new();

        markdown.AppendLine(CultureInfo.InvariantCulture, $"# Run `{summary.RunId}`");
        markdown.AppendLine();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- **Request:** {summary.Request}");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- **Workflow:** `{summary.Workflow}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- **Scenario:** {summary.Scenario}");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- **Status:** {summary.Status}");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- **Events:** {events.Length}");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- **Audit chain:** {verification.Summary}");
        markdown.AppendLine();

        markdown.AppendLine("## Stages");
        markdown.AppendLine();
        markdown.AppendLine("| stage | state | attempts | detail |");
        markdown.AppendLine("|---|---|---:|---|");

        foreach (NodeExecutionState node in state.Nodes.Values
                     .OrderBy(node => node.Id.Value, StringComparer.Ordinal))
        {
            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"| `{node.Id}` | {node.State} | {node.AttemptsMade} | {node.Detail ?? ""} |");
        }

        markdown.AppendLine();
        markdown.AppendLine("## Decisions");
        markdown.AppendLine();

        ImmutableArray<RunEvent> decisions =
            [.. events.Where(@event => @event.Kind == RunEventKind.DecisionRecorded)];

        if (decisions.IsEmpty)
        {
            markdown.AppendLine("_No decisions were recorded._");
        }

        foreach (RunEvent @event in decisions)
        {
            DecisionRecordedPayload payload = @event.Payload<DecisionRecordedPayload>();

            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"### `{payload.DecisionId}` — {payload.Question}");
            markdown.AppendLine();
            markdown.AppendLine(CultureInfo.InvariantCulture,
                $"**Chosen:** {payload.Chosen} (confidence {payload.Confidence:0.##}, "
                + $"by {@event.Actor})");
            markdown.AppendLine();
            markdown.AppendLine(CultureInfo.InvariantCulture, $"{payload.Rationale}");
            markdown.AppendLine();

            foreach (DecisionOptionPayload option in payload.Options.Where(
                         option => option.RejectedBecause is not null))
            {
                markdown.AppendLine(CultureInfo.InvariantCulture,
                    $"- Rejected **{option.Name}**: {option.RejectedBecause}");
            }

            markdown.AppendLine();
        }

        return markdown.ToString();
    }
}
