using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Serialization;

namespace Mandate.Persistence;

/// <summary>
/// Reads run evidence back from the exported files.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <see cref="RunEvidenceWriter"/>, and the reason the export is worth
/// having. Evidence committed to a repository is only useful if the person holding it can
/// put it back in front of the tools that check it: a reviewer who clones this repository
/// has the events, and should be able to verify the chain and derive the metrics without
/// having been present when the run executed.
/// </para>
/// <para>
/// Digests are read, never recomputed. An event is rebuilt with the hash the file records,
/// so a file whose contents no longer match its digest imports and then fails verification,
/// which is the outcome that makes the check worth running. Recomputing on read would make
/// every imported log verify by construction and prove nothing.
/// </para>
/// </remarks>
public static class RunEvidenceReader
{
    /// <summary>The file within an exported run directory that holds the events.</summary>
    public const string EventsFileName = "events.jsonl";

    /// <summary>
    /// Reads every exported run directory under <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// A directory without an events file is skipped rather than refused, so pointing this
    /// at a tree that also holds unrelated directories is not an error.
    /// </remarks>
    public static ImmutableArray<ExportedRun> ReadAll(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"No directory at '{root}'.");
        }

        // A single exported run directory is as valid an argument as a tree of them.
        if (File.Exists(Path.Combine(root, EventsFileName)))
        {
            return [Read(root)];
        }

        return
        [
            .. Directory.EnumerateDirectories(root)
                .Where(directory => File.Exists(Path.Combine(directory, EventsFileName)))
                .OrderBy(directory => directory, StringComparer.Ordinal)
                .Select(Read)
        ];
    }

    /// <summary>Reads one exported run directory.</summary>
    public static ExportedRun Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string path = Path.Combine(directory, EventsFileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"'{directory}' holds no {EventsFileName}, so it is not an exported run.", path);
        }

        ImmutableArray<RunEvent>.Builder events = ImmutableArray.CreateBuilder<RunEvent>();
        RunId? runId = RunIdFromDirectory(directory);
        int lineNumber = 0;

        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ExportedEvent exported = Parse(line, path, lineNumber);

            if (runId is null)
            {
                throw new InvalidDataException(
                    $"'{directory}' is not named for a run, so the events in it cannot be " +
                    "attributed to one. Exported directories are named for their run id.");
            }

            events.Add(Rebuild(exported, runId.Value, path, lineNumber));
        }

        if (events.Count == 0)
        {
            throw new InvalidDataException($"'{path}' holds no events.");
        }

        return new ExportedRun(runId!.Value, directory, events.ToImmutable());
    }

    private static ExportedEvent Parse(string line, string path, int lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize<ExportedEvent>(line, MandateJson.Canonical)
                   ?? throw new InvalidDataException($"{path}:{lineNumber} is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"{path}:{lineNumber} is not a readable event: {exception.Message}", exception);
        }
    }

    private static RunEvent Rebuild(
        ExportedEvent exported, RunId runId, string path, int lineNumber)
    {
        try
        {
            return RunEvent.Rehydrate(
                sequence: exported.Sequence,
                runId: runId,
                occurredAt: DateTimeOffset.Parse(
                    exported.OccurredAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                kind: Enum.Parse<RunEventKind>(exported.Kind),
                nodeId: exported.NodeId is null ? null : NodeId.Parse(exported.NodeId),
                actor: Actor.Parse(exported.Actor),

                // The raw text, not a re-serialisation. The digest covers these exact bytes,
                // so anything that could reformat them would break a chain that is intact.
                payloadJson: exported.Payload.GetRawText(),
                previousHash: Sha256Hash.Parse(exported.PreviousHash),
                storedHash: Sha256Hash.Parse(exported.Hash));
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException(
                $"{path}:{lineNumber} could not be rebuilt: {exception.Message}", exception);
        }
    }

    private static RunId? RunIdFromDirectory(string directory)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        return RunId.TryParse(name, out RunId parsed) ? parsed : null;
    }
}

/// <summary>A run read back from its exported evidence.</summary>
/// <param name="RunId">The run the evidence belongs to.</param>
/// <param name="Directory">Where it was read from.</param>
/// <param name="Events">Its events, in sequence order, carrying their recorded digests.</param>
public sealed record ExportedRun(
    RunId RunId, string Directory, ImmutableArray<RunEvent> Events);
