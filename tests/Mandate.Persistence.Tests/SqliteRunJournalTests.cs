using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Mandate.Persistence.Tests;

public sealed class SqliteRunJournalTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 14, 25, 0, TimeSpan.Zero);

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"mandate-tests-{Guid.NewGuid():N}");

    private string DatabasePath => Path.Combine(_directory, "runs.db");

    private static RunId Run(string suffix = "aaa111") => RunId.New(Start, suffix);

    private static async Task<int> WriteRunAsync(
        SqliteRunJournal journal,
        RunId runId,
        int events,
        string scenario = "Greenfield",
        DateTimeOffset? at = null)
    {
        DateTimeOffset origin = at ?? Start;

        for (int index = 0; index < events; index++)
        {
            int captured = index;

            await journal.AppendAsync(
                runId,
                previous => index == 0
                    ? RunEvent.Append(
                        previous, runId, origin, RunEventKind.RunPlanned, null, Actor.Engine,
                        new RunPlannedPayload(
                            "sdlc", "v1", scenario, "Do the thing", false,
                            "mandate/0.1.0", 4, ["a", "b"]))
                    : RunEvent.Append(
                        previous, runId, origin.AddSeconds(captured), RunEventKind.NodeStateChanged,
                        NodeId.Parse("a"), Actor.Engine,
                        new NodeStateChangedPayload("Pending", "Ready", 0, $"step {captured}")),
                CancellationToken.None);
        }

        return events;
    }

    [Fact]
    public async Task Events_survive_closing_and_reopening_the_store()
    {
        RunId runId = Run();

        using (SqliteRunJournal writing = SqliteRunJournal.Open(DatabasePath))
        {
            await WriteRunAsync(writing, runId, 5);
        }

        using SqliteRunJournal reading = SqliteRunJournal.Open(DatabasePath);
        ImmutableArray<RunEvent> events = await reading.ReadAsync(runId, CancellationToken.None);

        events.Length.ShouldBe(5);
        AuditChain.Verify(runId, events).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_rebuilt_from_disk_matches_the_one_that_was_written()
    {
        RunId runId = Run();

        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);
        await WriteRunAsync(journal, runId, 4);

        RunState? rebuilt = await journal.RebuildAsync(runId, CancellationToken.None);

        rebuilt.ShouldNotBeNull();
        rebuilt.LastSequence.ShouldBe(4);
        rebuilt.StateOf(NodeId.Parse("a")).ShouldBe(NodeState.Ready);
    }

    [Fact]
    public async Task Each_run_has_its_own_chain()
    {
        // A store holds many runs; linking an event to another run's tail would produce a log
        // that verifies against nothing.
        RunId first = Run("aaa111");
        RunId second = Run("bbb222");

        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);

        await WriteRunAsync(journal, first, 3);
        await WriteRunAsync(journal, second, 3);

        foreach (RunId runId in new[] { first, second })
        {
            ImmutableArray<RunEvent> events = await journal.ReadAsync(runId, CancellationToken.None);

            events.Length.ShouldBe(3);
            events[0].PreviousHash.ShouldBe(Sha256Hash.Genesis);
            AuditChain.Verify(runId, events).IsIntact.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Sequence_numbers_restart_at_one_for_each_run()
    {
        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);

        await WriteRunAsync(journal, Run("aaa111"), 2);
        await WriteRunAsync(journal, Run("bbb222"), 2);

        ImmutableArray<RunEvent> second =
            await journal.ReadAsync(Run("bbb222"), CancellationToken.None);

        second.Select(@event => @event.Sequence).ShouldBe([1L, 2L]);
    }

    [Fact]
    public async Task Concurrent_appends_produce_one_unbroken_chain()
    {
        RunId runId = Run();

        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous, runId, Start, RunEventKind.RunPlanned, null, Actor.Engine,
                new RunPlannedPayload("sdlc", "v1", "Greenfield", "x", false, "e", 4, ["a"])),
            CancellationToken.None);

        IEnumerable<Task> writers = Enumerable.Range(0, 40).Select(index => journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous, runId, Start.AddSeconds(index + 1), RunEventKind.ContextFactAdded,
                NodeId.Parse("run"), Actor.Engine,
                new ContextFactAddedPayload($"parallel.key{index}", index.ToString(), [])),
            CancellationToken.None));

        await Task.WhenAll(writers);

        ImmutableArray<RunEvent> events = await journal.ReadAsync(runId, CancellationToken.None);

        events.Length.ShouldBe(41);
        events.Select(@event => @event.Sequence)
            .ShouldBe(Enumerable.Range(1, 41).Select(value => (long)value));
        AuditChain.Verify(runId, events).IsIntact.ShouldBeTrue();
    }

    // ---- append-only enforcement ----

    [Fact]
    public async Task The_store_refuses_an_update_to_a_recorded_event()
    {
        // Append-only is enforced by the storage layer, not by the code that writes to it:
        // an edit through any client is refused.
        RunId runId = Run();

        using (SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath))
        {
            await WriteRunAsync(journal, runId, 3);
        }

        using SqliteConnection connection = new($"Data Source={DatabasePath}");
        connection.Open();

        using SqliteCommand tamper = connection.CreateCommand();
        tamper.CommandText =
            "UPDATE run_events SET payload_json = '{\"tampered\":true}' WHERE sequence = 2;";

        SqliteException error = Should.Throw<SqliteException>(() => tamper.ExecuteNonQuery());
        error.Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task The_store_refuses_a_delete_of_a_recorded_event()
    {
        RunId runId = Run();

        using (SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath))
        {
            await WriteRunAsync(journal, runId, 3);
        }

        using SqliteConnection connection = new($"Data Source={DatabasePath}");
        connection.Open();

        using SqliteCommand tamper = connection.CreateCommand();
        tamper.CommandText = "DELETE FROM run_events WHERE sequence = 2;";

        Should.Throw<SqliteException>(() => tamper.ExecuteNonQuery())
            .Message.ShouldContain("append-only");
    }

    [Fact]
    public async Task An_event_inserted_directly_into_the_store_breaks_verification()
    {
        // The triggers stop edits, but an insert is still an append as far as SQL is
        // concerned. The hash chain is what catches a forged one.
        RunId runId = Run();

        using (SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath))
        {
            await WriteRunAsync(journal, runId, 3);
        }

        using (SqliteConnection connection = new($"Data Source={DatabasePath}"))
        {
            connection.Open();

            using SqliteCommand forge = connection.CreateCommand();
            forge.CommandText = """
                INSERT INTO run_events
                    (run_id, sequence, occurred_at, kind, node_id, actor,
                     payload_json, previous_hash, hash)
                VALUES
                    ($runId, 4, '2026-09-16T14:30:00.0000000+00:00', 'ApprovalGranted', 'a',
                     'human:someone-else', '{"role":"tech-lead","note":"forged"}',
                     $previous, $hash);
                """;
            forge.Parameters.AddWithValue("$runId", runId.Value);
            forge.Parameters.AddWithValue("$previous", new string('0', 64));
            forge.Parameters.AddWithValue("$hash", new string('a', 64));
            forge.ExecuteNonQuery();
        }

        using SqliteRunJournal reading = SqliteRunJournal.Open(DatabasePath);
        AuditVerification verification = AuditChain.Verify(
            runId, await reading.ReadAsync(runId, CancellationToken.None));

        verification.IsIntact.ShouldBeFalse();
        verification.Breaks.ShouldContain(defect => defect.Reason == AuditBreakReason.BrokenLink);
        verification.Breaks.ShouldContain(defect => defect.Reason == AuditBreakReason.ContentAltered);
    }

    // ---- catalogue ----

    [Fact]
    public async Task Runs_are_listed_most_recent_first_with_their_request_and_scenario()
    {
        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);

        await WriteRunAsync(
            journal, RunId.New(Start, "aaa111"), 2, scenario: "Greenfield", at: Start);
        await WriteRunAsync(
            journal, RunId.New(Start.AddHours(1), "bbb222"), 2,
            scenario: "Brownfield", at: Start.AddHours(1));

        ImmutableArray<Core.Execution.RunSummary> runs =
            await journal.ListAsync(10, CancellationToken.None);

        runs.Length.ShouldBe(2);
        runs[0].Scenario.ShouldBe("Brownfield");
        runs[0].Workflow.ShouldBe("sdlc@v1");
        runs[0].Request.ShouldBe("Do the thing");
        runs[0].EventCount.ShouldBe(2);
    }

    [Fact]
    public async Task A_run_with_no_completion_event_is_reported_as_still_running()
    {
        // The process did not get to write one, so the run was interrupted rather than
        // finished. Saying so beats guessing.
        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);
        await WriteRunAsync(journal, Run(), 3);

        Core.Execution.RunSummary? summary = await journal.FindAsync(Run(), CancellationToken.None);

        summary.ShouldNotBeNull();
        summary.Status.ShouldBe(RunStatus.Running);
    }

    [Fact]
    public async Task A_completed_run_reports_the_status_it_recorded()
    {
        RunId runId = Run();

        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);
        await WriteRunAsync(journal, runId, 2);

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous, runId, Start.AddMinutes(1), RunEventKind.RunCompleted, null,
                Actor.Engine, new RunCompletedPayload("AwaitingApproval", "waiting")),
            CancellationToken.None);

        Core.Execution.RunSummary? summary = await journal.FindAsync(runId, CancellationToken.None);

        summary!.Status.ShouldBe(RunStatus.AwaitingApproval);
        summary.IsWaitingOnHuman.ShouldBeTrue();
    }

    [Fact]
    public async Task An_absent_run_is_reported_as_absent()
    {
        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);

        (await journal.FindAsync(Run("zzz999"), CancellationToken.None)).ShouldBeNull();
        (await journal.ReadAsync(Run("zzz999"), CancellationToken.None)).ShouldBeEmpty();
        (await journal.RebuildAsync(Run("zzz999"), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task An_in_memory_store_behaves_the_same_as_a_file_backed_one()
    {
        using SqliteRunJournal journal = SqliteRunJournal.OpenInMemory();

        await WriteRunAsync(journal, Run(), 3);

        AuditChain.Verify(Run(), await journal.ReadAsync(Run(), CancellationToken.None))
            .IsIntact.ShouldBeTrue();
    }

    [Fact]
    public void Opening_a_store_creates_its_directory()
    {
        using SqliteRunJournal journal = SqliteRunJournal.Open(DatabasePath);

        File.Exists(DatabasePath).ShouldBeTrue();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
