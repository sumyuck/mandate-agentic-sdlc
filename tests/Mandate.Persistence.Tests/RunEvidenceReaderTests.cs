using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Persistence.Sqlite;

namespace Mandate.Persistence.Tests;

/// <summary>
/// Exported evidence has to survive the round trip back into a store, because that is the
/// only way a reviewer who was not present can check the chain themselves.
/// </summary>
public sealed class RunEvidenceReaderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 14, 25, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"mandate-evidence-{Guid.NewGuid():N}");

    private static RunId Run(string suffix = "aaa111") => RunId.New(Start, suffix);

    private async Task<(RunId RunId, string Export, ImmutableArray<RunEvent> Events)>
        ExportARunAsync(string scenario = "Greenfield")
    {
        RunId runId = Run();

        using SqliteRunJournal journal = SqliteRunJournal.OpenInMemory();

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous, runId, Start, RunEventKind.RunPlanned, null, Actor.Engine,
                new RunPlannedPayload(
                    "sdlc", "v1", scenario, "Build a URL shortener", false,
                    "mandate/0.1.0", 4, ["intake", "requirements"])),
            CancellationToken.None);

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous, runId, Start.AddSeconds(1), RunEventKind.NodeStateChanged,
                NodeId.Parse("intake"), Actor.Engine,
                new NodeStateChangedPayload("Pending", "Ready", 0, "scheduled")),
            CancellationToken.None);

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous, runId, Start.AddSeconds(2), RunEventKind.RunCompleted, null,
                Actor.Engine, new RunCompletedPayload("Succeeded", "all stages settled")),
            CancellationToken.None);

        RunSummary summary =
            (await journal.FindAsync(runId, CancellationToken.None))!;
        ImmutableArray<RunEvent> events =
            await journal.ReadAsync(runId, CancellationToken.None);

        await RunEvidenceWriter.WriteAsync(_directory, summary, events, CancellationToken.None);

        return (runId, Path.Combine(_directory, runId.Value), events);
    }

    [Fact]
    public async Task An_exported_run_reads_back_with_every_event_intact()
    {
        (RunId runId, string export, ImmutableArray<RunEvent> original) = await ExportARunAsync();

        ExportedRun read = RunEvidenceReader.Read(export);

        Assert.Equal(runId, read.RunId);
        Assert.Equal(original.Length, read.Events.Length);

        foreach ((RunEvent expected, RunEvent actual) in original.Zip(read.Events))
        {
            Assert.Equal(expected.Sequence, actual.Sequence);
            Assert.Equal(expected.Kind, actual.Kind);
            Assert.Equal(expected.Actor, actual.Actor);
            Assert.Equal(expected.NodeId, actual.NodeId);
            Assert.Equal(expected.PreviousHash, actual.PreviousHash);

            // The digest is the whole point: it must come back byte-for-byte, because the
            // payload it commits to is re-read from the file rather than recomputed.
            Assert.Equal(expected.Hash, actual.Hash);
            Assert.Equal(expected.PayloadJson, actual.PayloadJson);
        }
    }

    [Fact]
    public async Task An_imported_run_verifies_in_the_store_it_was_imported_into()
    {
        (RunId runId, string export, _) = await ExportARunAsync();

        using SqliteRunJournal store = SqliteRunJournal.OpenInMemory();
        ExportedRun read = RunEvidenceReader.Read(export);

        int written = await store.ImportAsync(runId, read.Events, CancellationToken.None);

        Assert.Equal(read.Events.Length, written);

        ImmutableArray<RunEvent> stored = await store.ReadAsync(runId, CancellationToken.None);
        AuditVerification verification = AuditChain.Verify(runId, stored);

        Assert.True(verification.IsIntact, verification.Summary);
    }

    [Fact]
    public async Task An_imported_run_is_listed_and_found_like_any_other()
    {
        (RunId runId, string export, _) = await ExportARunAsync("Brownfield");

        using SqliteRunJournal store = SqliteRunJournal.OpenInMemory();
        await store.ImportAsync(
            runId, RunEvidenceReader.Read(export).Events, CancellationToken.None);

        RunSummary? found = await store.FindAsync(runId, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("Brownfield", found.Scenario);
        Assert.Equal(RunStatus.Succeeded, found.Status);
        Assert.Equal(3, found.EventCount);

        Assert.Single(await store.ListAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Importing_a_run_the_store_already_holds_is_refused()
    {
        (RunId runId, string export, _) = await ExportARunAsync();

        using SqliteRunJournal store = SqliteRunJournal.OpenInMemory();
        ImmutableArray<RunEvent> events = RunEvidenceReader.Read(export).Events;

        await store.ImportAsync(runId, events, CancellationToken.None);

        InvalidOperationException refused =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.ImportAsync(runId, events, CancellationToken.None));

        Assert.Contains("already in this store", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edited_payload_imports_and_then_fails_verification()
    {
        (RunId runId, string export, _) = await ExportARunAsync();

        // Tamper with the evidence exactly as someone would: change the text, leave the
        // recorded digest alone. Import must not launder that away by recomputing.
        string path = Path.Combine(export, RunEvidenceReader.EventsFileName);
        string[] lines = await File.ReadAllLinesAsync(path);
        lines[0] = lines[0].Replace(
            "Build a URL shortener", "Build something else", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(path, lines);

        using SqliteRunJournal store = SqliteRunJournal.OpenInMemory();
        ExportedRun read = RunEvidenceReader.Read(export);
        await store.ImportAsync(runId, read.Events, CancellationToken.None);

        AuditVerification verification = AuditChain.Verify(
            runId, await store.ReadAsync(runId, CancellationToken.None));

        Assert.False(verification.IsIntact);
    }

    [Fact]
    public async Task A_directory_of_exports_reads_every_run_under_it()
    {
        await ExportARunAsync();

        Assert.Single(RunEvidenceReader.ReadAll(_directory));
    }

    [Fact]
    public void A_directory_holding_no_events_file_is_not_an_exported_run()
    {
        Directory.CreateDirectory(_directory);

        Assert.Throws<FileNotFoundException>(() => RunEvidenceReader.Read(_directory));
    }

    [Fact]
    public void A_missing_directory_is_reported_rather_than_treated_as_empty()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => RunEvidenceReader.ReadAll(Path.Combine(_directory, "nowhere")));
    }

    /// <summary>
    /// The evidence committed to this repository is the submission's central claim. If it
    /// stops importing or stops verifying, that claim is broken, so it is pinned by a test
    /// rather than by having been checked once by hand.
    /// </summary>
    [Fact]
    public async Task The_committed_scenario_evidence_imports_and_verifies()
    {
        string root = Path.Combine(RepositoryRoot.Path, "runs");

        ImmutableArray<ExportedRun> exported = RunEvidenceReader.ReadAll(root);

        Assert.Equal(3, exported.Length);

        using SqliteRunJournal store = SqliteRunJournal.OpenInMemory();

        foreach (ExportedRun run in exported)
        {
            await store.ImportAsync(run.RunId, run.Events, CancellationToken.None);

            AuditVerification verification = AuditChain.Verify(
                run.RunId, await store.ReadAsync(run.RunId, CancellationToken.None));

            Assert.True(
                verification.IsIntact,
                $"{run.RunId}: {verification.Summary}");
        }

        Assert.Equal(3, (await store.ListAsync(10, CancellationToken.None)).Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
