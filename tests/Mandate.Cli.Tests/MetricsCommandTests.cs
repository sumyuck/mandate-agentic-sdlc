using System.Text.Json;
using System.Text.RegularExpressions;
using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>Metrics and the run report, through the commands.</summary>
public sealed partial class MetricsCommandTests
{
    private static string ShippedWorkflow => Path.Combine(
        RepositoryRoot.Path, "workflows", "sdlc.v1.yaml");

    private static string Template => Path.Combine(RepositoryRoot.Path, "templates", "service");

    private static string Policies => Path.Combine(RepositoryRoot.Path, "workflows", "policies");

    private static string Seed(TemporaryWorkspace workspace)
    {
        CliResult result = CliHarness.Run(
            "run", "Build a URL shortener", "--scenario", "greenfield",
            "--workflow", ShippedWorkflow, "--store", workspace.Store, "--as", "muskan",
            "--workspace-root", workspace.Path_("workspaces"), "--template", Template,
            "--policies", Policies);

        Match match = RunIdPattern().Match(result.Plain);
        match.Success.ShouldBeTrue($"no run id in: {result.Plain}");

        return match.Value;
    }

    [Fact]
    public void Metrics_are_reported_with_the_basis_for_each_figure()
    {
        // A number with no basis is an assertion. Each row says what it was computed from.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run("runs", "metrics", runId, "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("success rate");
        result.Rendered.ShouldContain("autonomy ratio");
        result.Rendered.ShouldContain("gate block rate");
        result.Rendered.ShouldContain("only caused");
    }

    [Fact]
    public void The_json_view_is_parseable_and_carries_the_derived_figures()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run(
            "runs", "metrics", runId, "--json", "--store", workspace.Store);

        using JsonDocument document = JsonDocument.Parse(result.Plain);

        document.RootElement.GetProperty("runId").GetString().ShouldBe(runId);
        document.RootElement.GetProperty("successRate").GetDouble().ShouldBeInRange(0, 1);
        document.RootElement.GetProperty("autonomyRatio").GetDouble().ShouldBeInRange(0, 1);
        document.RootElement.GetProperty("eventCount").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Metrics_for_an_unknown_run_are_refused()
    {
        using TemporaryWorkspace workspace = new();

        CliHarness.Run(
                "runs", "metrics", "run_20260101T000000Z_zzz999", "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void A_malformed_run_id_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        CliHarness.Run("runs", "metrics", "nonsense", "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void The_report_is_written_as_one_self_contained_page()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        string output = workspace.Path_("report.html");

        CliResult result = CliHarness.Run(
            "runs", "report", runId, "-o", output, "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("self-contained");

        string html = File.ReadAllText(output);

        html.ShouldStartWith("<!doctype html>");
        html.ShouldContain("Build a URL shortener");
        html.ShouldContain("chain intact");
        html.ShouldNotContain("<script");
    }

    [Fact]
    public void The_report_creates_the_directory_it_needs()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        string output = workspace.Path_("nested", "deeper", "report.html");

        CliHarness.Run("runs", "report", runId, "-o", output, "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.Success);

        File.Exists(output).ShouldBeTrue();
    }

    [Fact]
    public void The_report_shows_the_human_decisions_the_run_recorded()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
            "approve", runId, "--role", "tech-lead", "--as", "alex",
            "--note", "Design approved.", "--store", workspace.Store);

        string output = workspace.Path_("report.html");
        CliHarness.Run("runs", "report", runId, "-o", output, "--store", workspace.Store);

        string html = File.ReadAllText(output);

        html.ShouldContain("human:alex");
        html.ShouldContain("Design approved.");
    }

    [GeneratedRegex(@"run_\d{8}T\d{6}Z_[a-z0-9]+")]
    private static partial Regex RunIdPattern();
}
