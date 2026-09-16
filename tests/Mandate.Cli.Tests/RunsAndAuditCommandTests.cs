using System.Text.Json;
using System.Text.RegularExpressions;
using Mandate.Cli.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Mandate.Cli.Tests;

/// <summary>
/// Inspecting, exporting and verifying recorded runs.
/// </summary>
public sealed partial class RunsAndAuditCommandTests
{
    private static string ShippedWorkflow => Path.Combine(
        RepositoryRoot.Path, "workflows", "sdlc.v1.yaml");

    /// <summary>Executes a run and returns its id.</summary>
    private static string Seed(TemporaryWorkspace workspace, string scenario = "greenfield")
    {
        CliResult result = CliHarness.Run(
            "run", "Build a URL shortener", "--scenario", scenario,
            "--workflow", ShippedWorkflow, "--store", workspace.Store, "--as", "tester",
            "--workspace-root", workspace.Path_("workspaces"),
            "--template", Path.Combine(RepositoryRoot.Path, "templates", "service"));

        Match match = RunIdPattern().Match(result.Plain);
        match.Success.ShouldBeTrue($"no run id in: {result.Plain}");

        return match.Value;
    }

    // ---- runs list ----

    [Fact]
    public void An_empty_store_lists_nothing_and_succeeds()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run("runs", "list", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("No runs recorded yet");
    }

    [Fact]
    public void Recorded_runs_are_listed_with_their_scenario_and_status()
    {
        using TemporaryWorkspace workspace = new();
        Seed(workspace);

        CliResult result = CliHarness.Run("runs", "list", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("greenfield");
        result.Rendered.ShouldContain("awaiting approval");
    }

    [Fact]
    public void The_ids_view_prints_one_unwrapped_id_per_line()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run("runs", "list", "--ids", "--store", workspace.Store);

        string[] lines = result.Plain
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        lines.ShouldBe([runId]);
    }

    [Fact]
    public void The_json_view_is_parseable_and_carries_the_fields_a_script_needs()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run("runs", "list", "--json", "--store", workspace.Store);

        using JsonDocument document = JsonDocument.Parse(result.Plain);

        JsonElement first = document.RootElement[0];
        first.GetProperty("runId").GetString().ShouldBe(runId);
        first.GetProperty("scenario").GetString().ShouldBe("Greenfield");
        first.GetProperty("waitingOnHuman").GetBoolean().ShouldBeTrue();
        first.GetProperty("eventCount").GetInt64().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_listing_respects_its_limit()
    {
        using TemporaryWorkspace workspace = new();
        Seed(workspace);
        Seed(workspace, "brownfield");

        CliResult result = CliHarness.Run(
            "runs", "list", "--ids", "--limit", "1", "--store", workspace.Store);

        result.Plain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Length.ShouldBe(1);
    }

    // ---- runs show ----

    [Fact]
    public void A_run_is_rebuilt_from_its_log_and_summarised()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run("runs", "show", runId, "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("architecture");
        result.Rendered.ShouldContain("awaiting approval");
        result.Rendered.ShouldContain("skipped");
        result.Rendered.ShouldContain("artifact");
    }

    [Fact]
    public void The_events_view_of_a_run_shows_the_chain()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run(
            "runs", "show", runId, "--events", "--store", workspace.Store);

        result.Rendered.ShouldContain("NodeStateChanged");
        result.Rendered.ShouldContain("ApprovalRequested");
    }

    [Fact]
    public void A_malformed_run_id_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run("runs", "show", "not-a-run", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("is not a run id");
    }

    [Fact]
    public void An_unknown_run_is_reported_as_absent()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run(
            "runs", "show", "run_20260101T000000Z_zzz999", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("No run");
    }

    // ---- runs export ----

    [Fact]
    public void Exporting_writes_the_three_review_artefacts()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        string destination = workspace.Path_("evidence");

        CliResult result = CliHarness.Run(
            "runs", "export", runId, "--to", destination, "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);

        string directory = Path.Combine(destination, runId);
        File.Exists(Path.Combine(directory, "events.jsonl")).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "run.json")).ShouldBeTrue();
        File.Exists(Path.Combine(directory, "timeline.md")).ShouldBeTrue();
    }

    [Fact]
    public void Exporting_an_unknown_run_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        CliHarness.Run(
                "runs", "export", "run_20260101T000000Z_zzz999",
                "--to", workspace.Path_("evidence"), "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    // ---- audit verify ----

    [Fact]
    public void Verification_of_an_empty_store_succeeds_vacuously()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run("audit", "verify", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("No runs recorded yet");
    }

    [Fact]
    public void An_untouched_run_verifies()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run("audit", "verify", runId, "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("chain intact");
    }

    [Fact]
    public void Verifying_without_a_run_id_checks_every_run()
    {
        using TemporaryWorkspace workspace = new();
        Seed(workspace);
        Seed(workspace, "brownfield");

        CliResult result = CliHarness.Run("audit", "verify", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("2 run(s) verified");
    }

    [Fact]
    public void A_forged_event_is_detected_and_reported_through_the_command()
    {
        // The end-to-end version of the tampering test: the triggers stop edits, so the only
        // way in is an insert, and the hash chain is what catches it. This asserts the
        // operator-facing behaviour, not just the library's.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Forge(workspace.Store, runId);

        CliResult result = CliHarness.Run("audit", "verify", runId, "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Failed);
        result.Rendered.ShouldContain("BROKEN");
        result.Rendered.ShouldContain("BrokenLink");
        result.Rendered.ShouldContain("ContentAltered");
        result.Rendered.ShouldContain("failed verification");
    }

    [Fact]
    public void An_edit_is_refused_by_the_store_itself()
    {
        using TemporaryWorkspace workspace = new();
        Seed(workspace);

        using SqliteConnection connection = new($"Data Source={workspace.Store}");
        connection.Open();

        using SqliteCommand tamper = connection.CreateCommand();
        tamper.CommandText = "UPDATE run_events SET actor = 'human:someone-else';";

        Should.Throw<SqliteException>(() => tamper.ExecuteNonQuery())
            .Message.ShouldContain("append-only");
    }

    private static void Forge(string store, string runId)
    {
        using SqliteConnection connection = new($"Data Source={store}");
        connection.Open();

        using SqliteCommand sequence = connection.CreateCommand();
        sequence.CommandText =
            "SELECT MAX(sequence) + 1 FROM run_events WHERE run_id = $runId;";
        sequence.Parameters.AddWithValue("$runId", runId);

        long next = Convert.ToInt64(sequence.ExecuteScalar(), provider: null);

        using SqliteCommand forge = connection.CreateCommand();
        forge.CommandText = """
            INSERT INTO run_events
                (run_id, sequence, occurred_at, kind, node_id, actor,
                 payload_json, previous_hash, hash)
            VALUES
                ($runId, $sequence, '2026-09-16T23:59:00.0000000+00:00', 'ApprovalGranted',
                 'architecture', 'human:impostor', '{"role":"tech-lead","note":"forged"}',
                 $previous, $hash);
            """;
        forge.Parameters.AddWithValue("$runId", runId);
        forge.Parameters.AddWithValue("$sequence", next);
        forge.Parameters.AddWithValue("$previous", new string('0', 64));
        forge.Parameters.AddWithValue("$hash", new string('b', 64));
        forge.ExecuteNonQuery();
    }

    [GeneratedRegex(@"run_\d{8}T\d{6}Z_[a-z0-9]+")]
    private static partial Regex RunIdPattern();
}
