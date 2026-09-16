using System.Text.RegularExpressions;
using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>Amending an input the run already acted on.</summary>
public sealed partial class AmendCommandTests
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

    private static CliResult Resume(TemporaryWorkspace workspace, string runId) =>
        CliHarness.Run(
            "resume", runId, "--workflow", ShippedWorkflow, "--store", workspace.Store,
            "--workspace-root", workspace.Path_("workspaces"), "--template", Template,
            "--policies", Policies);

    private static CliResult Amend(
        TemporaryWorkspace workspace, string runId, string stage, params string[] extra) =>
        CliHarness.Run(
            ["amend", runId, "--stage", stage, "--as", "muskan", "--store", workspace.Store,
             .. extra]);

    [Fact]
    public void An_amendment_needs_a_reason()
    {
        // It discards work that was already done, and the record has to say why.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = Amend(workspace, runId, "requirements");

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("needs a reason");
    }

    [Fact]
    public void A_stage_that_has_not_produced_anything_cannot_be_amended()
    {
        // There is nothing to redo, and saying so beats recording a decision that changes
        // nothing.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = Amend(
            workspace, runId, "implement", "--reason", "Changed my mind.");

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("has not produced anything yet");
    }

    [Fact]
    public void A_stage_that_is_not_part_of_the_run_is_refused()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Amend(workspace, runId, "invented-stage", "--reason", "Because.")
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void Amending_an_unknown_run_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        Amend(workspace, "run_20260101T000000Z_zzz999", "requirements", "--reason", "Because.")
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void An_amendment_redoes_the_stage_and_withdraws_the_approval_given_for_it()
    {
        // The full loop: a human changes their mind about an input, the work built on it is
        // redone, and the signature that endorsed the old work stops counting.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
            "approve", runId, "--role", "tech-lead", "--as", "alex",
            "--note", "Design approved.", "--store", workspace.Store);

        Resume(workspace, runId);

        CliResult amended = Amend(
            workspace, runId, "requirements", "--reason", "Expiry means a TTL.");

        amended.ExitCode.ShouldBe(ExitCode.Success);
        amended.Rendered.ShouldContain("stops counting");

        CliResult resumed = Resume(workspace, runId);

        resumed.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        resumed.Rendered.ShouldContain("awaiting approval");

        CliResult events = CliHarness.Run(
            "runs", "show", runId, "--events", "--store", workspace.Store);

        events.Rendered.ShouldContain("RunAmended");
        events.Rendered.ShouldContain("ReplanPerformed");
        events.Rendered.ShouldContain("NodeInvalidated");
    }

    [Fact]
    public void The_chain_still_verifies_after_an_amendment_and_a_re_plan()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
            "approve", runId, "--role", "tech-lead", "--as", "alex", "--note", "ok",
            "--store", workspace.Store);

        Resume(workspace, runId);
        Amend(workspace, runId, "requirements", "--reason", "Scope changed.");
        Resume(workspace, runId);

        CliHarness.Run("audit", "verify", runId, "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.Success);
    }

    [GeneratedRegex(@"run_\d{8}T\d{6}Z_[a-z0-9]+")]
    private static partial Regex RunIdPattern();
}
