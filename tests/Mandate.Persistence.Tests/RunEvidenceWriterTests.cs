using System.Collections.Immutable;
using System.Text.Json;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;

namespace Mandate.Persistence.Tests;

public sealed class RunEvidenceWriterTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 14, 25, 0, TimeSpan.Zero);
    private static readonly RunId Run = RunId.New(Start, "abc123");

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"mandate-evidence-{Guid.NewGuid():N}");

    private static ImmutableArray<RunEvent> BuildEvents()
    {
        List<RunEvent> events = [];
        RunEvent? previous = null;

        RunEvent Append<TPayload>(
            RunEventKind kind, string? nodeId, Actor actor, TPayload payload, int second)
        {
            previous = RunEvent.Append(
                previous, Run, Start.AddSeconds(second), kind,
                nodeId is null ? null : NodeId.Parse(nodeId), actor, payload);

            events.Add(previous);
            return previous;
        }

        Append(RunEventKind.RunPlanned, null, Actor.Engine, new RunPlannedPayload(
            "sdlc", "v1", "Greenfield", "Build a URL shortener", false,
            "mandate/0.1.0", 4, ["intake", "architecture"]), 0);

        Append(RunEventKind.NodeStateChanged, "intake", Actor.Engine,
            new NodeStateChangedPayload("Pending", "Ready", 0, "Entry gate passed."), 1);

        Append(RunEventKind.NodeStateChanged, "intake", Actor.Agent("intake"),
            new NodeStateChangedPayload("Ready", "Running", 1, "Attempt 1 of 2."), 2);

        Append(RunEventKind.DecisionRecorded, "architecture", Actor.Agent("architect"),
            new DecisionRecordedPayload(
                "architecture-001",
                "How should short codes be generated?",
                [
                    new DecisionOptionPayload("base62", "Base62 over a sequence.", null),
                    new DecisionOptionPayload(
                        "hash", "Truncated hash.", "Needs collision handling on the hot path."),
                ],
                "base62",
                "Sequential ids keep the hot path collision-free.",
                0.8,
                "Agent",
                []), 3);

        Append(RunEventKind.RunCompleted, null, Actor.Engine,
            new RunCompletedPayload("AwaitingApproval", "waiting on tech-lead"), 4);

        return [.. events];
    }

    private static RunSummary Summary(ImmutableArray<RunEvent> events) => new(
        Run, "sdlc@v1", "Greenfield", "Build a URL shortener",
        RunStatus.AwaitingApproval, Start, Start.AddSeconds(4), events.Length);

    private async Task<EvidenceExport> ExportAsync()
    {
        ImmutableArray<RunEvent> events = BuildEvents();

        return await RunEvidenceWriter.WriteAsync(
            _directory, Summary(events), events, CancellationToken.None);
    }

    [Fact]
    public async Task It_writes_the_three_review_artefacts()
    {
        EvidenceExport export = await ExportAsync();

        File.Exists(Path.Combine(export.Directory, "events.jsonl")).ShouldBeTrue();
        File.Exists(Path.Combine(export.Directory, "run.json")).ShouldBeTrue();
        File.Exists(Path.Combine(export.Directory, "timeline.md")).ShouldBeTrue();
        export.ChainIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task The_event_log_is_one_json_object_per_line()
    {
        // Readable with head, greppable, and diffable between runs.
        EvidenceExport export = await ExportAsync();

        string[] lines = await File.ReadAllLinesAsync(
            Path.Combine(export.Directory, "events.jsonl"), CancellationToken.None);

        lines.Length.ShouldBe(5);

        foreach (string line in lines)
        {
            Should.NotThrow(() => JsonDocument.Parse(line));
            line.ShouldNotContain("\n");
        }
    }

    [Fact]
    public async Task The_exported_log_carries_the_recorded_digests()
    {
        // So verification can be re-run against the files alone, without the store.
        EvidenceExport export = await ExportAsync();

        using JsonDocument first = JsonDocument.Parse(
            (await File.ReadAllLinesAsync(
                Path.Combine(export.Directory, "events.jsonl"), CancellationToken.None))[0]);

        first.RootElement.GetProperty("previousHash").GetString()
            .ShouldBe(Sha256Hash.Genesis.Hex);
        first.RootElement.GetProperty("hash").GetString()!.Length.ShouldBe(64);
        first.RootElement.GetProperty("actor").GetString().ShouldBe("engine:orchestrator");
    }

    [Fact]
    public async Task Payloads_are_exported_as_json_not_as_an_escaped_string()
    {
        EvidenceExport export = await ExportAsync();

        using JsonDocument first = JsonDocument.Parse(
            (await File.ReadAllLinesAsync(
                Path.Combine(export.Directory, "events.jsonl"), CancellationToken.None))[0]);

        JsonElement payload = first.RootElement.GetProperty("payload");

        payload.ValueKind.ShouldBe(JsonValueKind.Object);
        payload.GetProperty("workflow").GetString().ShouldBe("sdlc");
    }

    [Fact]
    public async Task The_run_summary_records_the_projected_state_and_chain_verdict()
    {
        EvidenceExport export = await ExportAsync();

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(export.Directory, "run.json"), CancellationToken.None));

        document.RootElement.GetProperty("runId").GetString().ShouldBe(Run.Value);
        document.RootElement.GetProperty("chainIntact").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("status").GetString().ShouldBe("AwaitingApproval");
        document.RootElement.GetProperty("nodes").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task The_timeline_records_decisions_with_the_options_rejected()
    {
        // The point of the decision log: a reviewer can see what was weighed, not only what
        // was chosen.
        EvidenceExport export = await ExportAsync();

        string timeline = await File.ReadAllTextAsync(
            Path.Combine(export.Directory, "timeline.md"), CancellationToken.None);

        timeline.ShouldContain("How should short codes be generated?");
        timeline.ShouldContain("Rejected **hash**");
        timeline.ShouldContain("collision handling");
        timeline.ShouldContain("agent:architect");
    }

    [Fact]
    public async Task A_tampered_export_fails_verification_exactly_as_the_store_would()
    {
        ImmutableArray<RunEvent> events = BuildEvents();

        RunEvent original = events[2];
        ImmutableArray<RunEvent> tampered = events.SetItem(2, RunEvent.Rehydrate(
            original.Sequence, original.RunId, original.OccurredAt, original.Kind,
            original.NodeId, Actor.Human("someone-else"), original.PayloadJson,
            original.PreviousHash, original.Hash));

        EvidenceExport export = await RunEvidenceWriter.WriteAsync(
            _directory, Summary(tampered), tampered, CancellationToken.None);

        export.ChainIntact.ShouldBeFalse();

        string timeline = await File.ReadAllTextAsync(
            Path.Combine(export.Directory, "timeline.md"), CancellationToken.None);

        timeline.ShouldContain("BROKEN");
    }

    [Fact]
    public async Task Exporting_twice_overwrites_rather_than_accumulating()
    {
        await ExportAsync();
        EvidenceExport second = await ExportAsync();

        Directory.GetFiles(second.Directory).Length.ShouldBe(3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
