using System.Text.RegularExpressions;
using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>
/// Executing a run through the CLI, including the exit codes a script depends on.
/// </summary>
public sealed partial class RunCommandTests
{
    private static string ShippedWorkflow => Path.Combine(
        RepositoryRoot.Path, "workflows", "sdlc.v1.yaml");

    private static string Template => Path.Combine(RepositoryRoot.Path, "templates", "service");

    private static string Policies => Path.Combine(RepositoryRoot.Path, "workflows", "policies");

    private static CliResult Run(TemporaryWorkspace workspace, string scenario, params string[] extra) =>
        CliHarness.Run(
        [
            "run", "Do the thing", "--scenario", scenario,
            "--workflow", ShippedWorkflow, "--store", workspace.Store, "--as", "tester",
            "--workspace-root", workspace.Path_("workspaces"), "--template", Template,
            "--policies", Policies,
            .. extra,
        ]);

    [Fact]
    public void A_greenfield_run_stops_at_the_first_human_checkpoint()
    {
        // Exit code 3, not 1: a run waiting on an approval is not a broken run, and a CI job
        // or demo script has to be able to tell them apart.
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "greenfield");

        result.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        result.Rendered.ShouldContain("awaiting approval");
        result.Rendered.ShouldContain("chain intact");
    }

    [Fact]
    public void An_ambiguous_run_is_blocked_rather_than_failed()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "ambiguous");

        result.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        result.Rendered.ShouldContain("blocked");
    }

    [Fact]
    public void A_brownfield_run_takes_the_impact_analysis_path()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "brownfield");

        result.Rendered.ShouldContain("impact-analysis");
        result.Rendered.ShouldContain("succeeded");
    }

    [Fact]
    public void A_missing_workspace_template_is_reported_as_a_sentence_not_a_stack_trace()
    {
        // The template path is relative to the working directory, so running from the wrong
        // place is an easy mistake and should read like one.
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run(
            "run", "Do the thing", "--workflow", ShippedWorkflow, "--store", workspace.Store,
            "--template", workspace.Path_("no-such-template"));

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("no workspace template");
        result.Rendered.ShouldContain("--template");
    }

    [Fact]
    public void A_run_writes_its_output_into_a_workspace()
    {
        using TemporaryWorkspace workspace = new();

        Run(workspace, "greenfield");

        string root = workspace.Path_("workspaces");
        Directory.Exists(root).ShouldBeTrue();

        // Seeded from the template, then extended by the stages that ran.
        string[] created = Directory.GetDirectories(root);
        created.Length.ShouldBe(1);
        File.Exists(Path.Combine(created[0], "Program.cs")).ShouldBeTrue();
        File.Exists(Path.Combine(created[0], "docs", "design.md")).ShouldBeTrue();
    }

    [Fact]
    public void An_unrecognised_scenario_is_refused_before_anything_runs()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "vibes");

        result.ExitCode.ShouldNotBe(ExitCode.Success);
        result.All.ShouldContain("greenfield");
        File.Exists(workspace.Store).ShouldBeFalse();
    }

    [Fact]
    public void A_workflow_that_cannot_be_loaded_is_reported_as_bad_input()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run(
            "run", "Do the thing", "--workflow", "does/not/exist.yaml",
            "--store", workspace.Store, "--template", Template, "--policies", Policies);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("cannot load workflow");
    }

    [Fact]
    public void A_run_is_recorded_and_its_id_printed_unwrapped()
    {
        // The id has to be copy-pasteable and pipeable; the renderer would split it.
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "greenfield");

        File.Exists(workspace.Store).ShouldBeTrue();

        Match match = RunIdPattern().Match(result.Plain);

        match.Success.ShouldBeTrue($"no unwrapped run id in: {result.Plain}");
        match.Value.ShouldNotContain("\n");
    }

    [Fact]
    public void An_ephemeral_run_records_nothing()
    {
        // Not recording a run has to be asked for, because an unrecorded run cannot be
        // inspected, verified or resumed.
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "greenfield", "--ephemeral");

        result.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        File.Exists(workspace.Store).ShouldBeFalse();
        result.Rendered.ShouldNotContain("recorded in");
    }

    [Fact]
    public void The_events_view_shows_the_audit_log_with_its_digests()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "greenfield", "--events");

        result.Rendered.ShouldContain("RunPlanned");
        result.Rendered.ShouldContain("EntryGateEvaluated");
        result.Rendered.ShouldContain("digest");
    }

    [Fact]
    public void Concurrency_can_be_constrained_from_the_command_line()
    {
        using TemporaryWorkspace workspace = new();

        Run(workspace, "greenfield", "--max-concurrency", "1")
            .ExitCode.ShouldBe(ExitCode.AwaitingHuman);
    }

    [Fact]
    public void An_unusable_concurrency_limit_is_refused_rather_than_clamped()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = Run(workspace, "greenfield", "--max-concurrency", "0");

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("engine not configured");
    }

    [GeneratedRegex(@"run_\d{8}T\d{6}Z_[a-z0-9]+")]
    private static partial Regex RunIdPattern();
}
