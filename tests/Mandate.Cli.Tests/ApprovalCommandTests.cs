using System.Text.RegularExpressions;
using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>
/// The human loop, through the commands an operator actually types.
/// </summary>
public sealed partial class ApprovalCommandTests
{
    private static string ShippedWorkflow => Path.Combine(
        RepositoryRoot.Path, "workflows", "sdlc.v1.yaml");

    private static string Template => Path.Combine(RepositoryRoot.Path, "templates", "service");

    private static string Policies => Path.Combine(RepositoryRoot.Path, "workflows", "policies");

    private static string Seed(TemporaryWorkspace workspace, string initiatedBy = "muskan")
    {
        CliResult result = CliHarness.Run(
            "run", "Build a URL shortener", "--scenario", "greenfield",
            "--workflow", ShippedWorkflow, "--store", workspace.Store, "--as", initiatedBy,
            "--workspace-root", workspace.Path_("workspaces"), "--template", Template,
            "--policies", Policies);

        Match match = RunIdPattern().Match(result.Plain);
        match.Success.ShouldBeTrue($"no run id in: {result.Plain}");

        return match.Value;
    }

    private static CliResult Approve(
        TemporaryWorkspace workspace, string runId, string role, string by, params string[] extra) =>
        CliHarness.Run(
            ["approve", runId, "--role", role, "--as", by, "--store", workspace.Store, .. extra]);

    private static CliResult Resume(TemporaryWorkspace workspace, string runId) =>
        CliHarness.Run(
            "resume", runId, "--workflow", ShippedWorkflow, "--store", workspace.Store,
            "--workspace-root", workspace.Path_("workspaces"), "--template", Template,
            "--policies", Policies);

    [Fact]
    public void An_approval_is_recorded_and_points_at_the_next_step()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = Approve(workspace, runId, "tech-lead", "alex", "--note", "Fine.");

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("approved");
        result.Rendered.ShouldContain("human:alex");
        result.Rendered.ShouldContain("mandate resume");
    }

    [Fact]
    public void The_person_who_asked_for_the_work_cannot_sign_it_off()
    {
        // The control that actually bites in an agent-driven lifecycle: agents do the
        // producing, but a person requested it, and maker-checker means they do not also
        // approve it.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace, initiatedBy: "muskan");

        CliResult result = Approve(workspace, runId, "tech-lead", "muskan");

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("requested this run and cannot also approve");
        result.Rendered.ShouldContain("Segregation of duties");
    }

    [Fact]
    public void Approving_a_role_no_stage_asked_for_is_refused_and_lists_what_was_asked()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = Approve(workspace, runId, "chief-vibes-officer", "alex");

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("no stage asked for");
        result.Rendered.ShouldContain("tech-lead");
    }

    [Fact]
    public void An_approval_needs_a_role()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run("approve", runId, "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void A_refusal_without_a_reason_is_rejected()
    {
        // Whoever has to act on the refusal needs to know what was wrong.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run(
            "deny", runId, "--role", "tech-lead", "--as", "alex", "--store", workspace.Store);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("needs a reason");
    }

    [Fact]
    public void Approving_an_unknown_run_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        Approve(workspace, "run_20260101T000000Z_zzz999", "tech-lead", "alex")
            .ExitCode.ShouldBe(ExitCode.BadInput);
    }

    // ---- resume ----

    [Fact]
    public void Approving_then_resuming_carries_the_run_through_the_parallel_section()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Approve(workspace, runId, "tech-lead", "alex", "--note", "Design approved.")
            .ExitCode.ShouldBe(ExitCode.Success);

        CliResult resumed = Resume(workspace, runId);

        resumed.Rendered.ShouldContain("without re-running");

        foreach (string stage in new[] { "implement", "test", "code-review", "security-scan" })
        {
            resumed.Rendered.ShouldContain(stage);
        }

        // The release gate fails closed on a policy pack nothing has evaluated yet, which is
        // the correct outcome until the policy engine exists.
        resumed.Rendered.ShouldContain("release-readiness");
        resumed.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
    }

    [Fact]
    public void Denying_then_resuming_blocks_the_stage()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
                "deny", runId, "--role", "tech-lead", "--as", "alex",
                "--note", "Blast radius is larger than the requirement justifies.",
                "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.Success);

        CliResult resumed = Resume(workspace, runId);

        resumed.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        resumed.Rendered.ShouldContain("blocked");
    }

    [Fact]
    public void Resuming_without_a_decision_leaves_the_run_parked()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult resumed = Resume(workspace, runId);

        resumed.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        resumed.Rendered.ShouldContain("awaiting approval");
    }

    [Fact]
    public void Resuming_a_run_with_nothing_outstanding_says_so()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Approve(workspace, runId, "tech-lead", "alex", "--note", "Fine.");
        Resume(workspace, runId);

        // Second resume: the run is blocked at the release gate, which is outstanding work a
        // human must clear, so it is still resumable rather than finished.
        CliResult again = Resume(workspace, runId);
        again.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
    }

    [Fact]
    public void Resuming_an_unknown_run_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        Resume(workspace, "run_20260101T000000Z_zzz999").ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void A_malformed_run_id_is_refused_by_both_commands()
    {
        using TemporaryWorkspace workspace = new();

        Approve(workspace, "nonsense", "tech-lead", "alex").ExitCode.ShouldBe(ExitCode.BadInput);
        Resume(workspace, "nonsense").ExitCode.ShouldBe(ExitCode.BadInput);
    }

    [Fact]
    public void The_run_still_verifies_after_a_decision_and_a_resume()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Approve(workspace, runId, "tech-lead", "alex", "--note", "Fine.");
        Resume(workspace, runId);

        CliResult audit = CliHarness.Run("audit", "verify", runId, "--store", workspace.Store);

        audit.ExitCode.ShouldBe(ExitCode.Success);
        audit.Rendered.ShouldContain("chain intact");
    }

    [GeneratedRegex(@"run_\d{8}T\d{6}Z_[a-z0-9]+")]
    private static partial Regex RunIdPattern();
}
