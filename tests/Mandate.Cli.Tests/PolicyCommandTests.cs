using System.Text.RegularExpressions;
using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>
/// Policy through the commands an operator types, including the waiver path.
/// </summary>
public sealed partial class PolicyCommandTests
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

    private static CliResult Check(TemporaryWorkspace workspace, string runId) =>
        CliHarness.Run(
            "policy", "check", runId, "--store", workspace.Store, "--policies", Policies,
            "--workflow", ShippedWorkflow, "--workspace-root", workspace.Path_("workspaces"));

    // ---- listing ----

    [Fact]
    public void The_rules_can_be_listed()
    {
        CliResult result = CliHarness.Run("policy", "list", "--policies", Policies);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("change-control@v1");
        result.Rendered.ShouldContain("CHG-001");
        result.Rendered.ShouldContain("SEC-001");
        result.Rendered.ShouldContain("blocking");
    }

    [Fact]
    public void The_listing_can_be_narrowed_to_one_concern()
    {
        CliResult result = CliHarness.Run(
            "policy", "list", "--category", "security", "--policies", Policies);

        result.Rendered.ShouldContain("SEC-001");
        result.Rendered.ShouldNotContain("CHG-001");
    }

    [Fact]
    public void A_missing_policy_directory_is_reported()
    {
        using TemporaryWorkspace workspace = new();

        CliResult result = CliHarness.Run(
            "policy", "list", "--policies", workspace.Path_("nowhere"));

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("cannot load policies");
    }

    // ---- evaluating ----

    [Fact]
    public void A_run_can_be_evaluated_against_every_pack()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = Check(workspace, runId);

        result.Rendered.ShouldContain("change-control@v1");
        result.Rendered.ShouldContain("compliance@v1");
        result.Rendered.ShouldContain("security@v1");
        result.Rendered.ShouldContain("satisfied");
    }

    [Fact]
    public void An_outstanding_approval_is_reported_as_a_blocking_violation()
    {
        // The run is parked at the design approval, so the change-control rule that requires
        // held approvals has something real to say.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = Check(workspace, runId);

        result.ExitCode.ShouldBe(ExitCode.Failed);
        result.Rendered.ShouldContain("CHG-001");
        result.Rendered.ShouldContain("Approval outstanding");
    }

    [Fact]
    public void The_audit_integrity_rule_reads_the_run_that_is_being_checked()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Check(workspace, runId).Rendered.ShouldContain("chain intact");
    }

    [Fact]
    public void Checking_an_unknown_run_is_refused()
    {
        using TemporaryWorkspace workspace = new();

        Check(workspace, "run_20260101T000000Z_zzz999").ExitCode.ShouldBe(ExitCode.BadInput);
    }

    // ---- waivers ----

    [Fact]
    public void A_waiver_needs_a_reason()
    {
        // Overriding a control without saying why is indistinguishable from not having it.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run(
            "waive", runId, "--rule", "CHG-001", "--as", "alex",
            "--store", workspace.Store, "--policies", Policies);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("needs a reason");
    }

    [Fact]
    public void A_rule_nobody_declared_cannot_be_waived()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run(
            "waive", runId, "--rule", "MADE-UP-9", "--as", "alex", "--reason", "Because.",
            "--store", workspace.Store, "--policies", Policies);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("no rule");
    }

    [Fact]
    public void An_advisory_rule_cannot_be_waived_because_it_does_not_block()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliResult result = CliHarness.Run(
            "waive", runId, "--rule", "CMP-003", "--as", "alex", "--reason", "Because.",
            "--store", workspace.Store, "--policies", Policies);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("advisory");
    }

    [Fact]
    public void A_waived_violation_stops_blocking_but_is_still_reported()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        Check(workspace, runId).Rendered.ShouldContain("blocking");

        CliResult waived = CliHarness.Run(
            "waive", runId, "--rule", "CHG-001", "--as", "alex",
            "--reason", "Approval tracked in INC-42; releasing under change freeze exception.",
            "--store", workspace.Store, "--policies", Policies);

        waived.ExitCode.ShouldBe(ExitCode.Success);
        waived.Rendered.ShouldContain("still evaluated and still reported");

        CliResult after = Check(workspace, runId);

        after.Rendered.ShouldContain("waived");
        after.Rendered.ShouldContain("Approval outstanding", Case.Insensitive);
    }

    [Fact]
    public void A_waiver_can_be_withdrawn()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
            "waive", runId, "--rule", "CHG-001", "--as", "alex", "--reason", "Temporary.",
            "--store", workspace.Store, "--policies", Policies);

        CliResult withdrawn = CliHarness.Run(
            "waive", runId, "--rule", "CHG-001", "--withdraw",
            "--store", workspace.Store, "--policies", Policies);

        withdrawn.ExitCode.ShouldBe(ExitCode.Success);
        withdrawn.Rendered.ShouldContain("withdrawn");

        Check(workspace, runId).ExitCode.ShouldBe(ExitCode.Failed);
    }

    [Fact]
    public void A_waiver_does_not_bypass_the_gate_that_independently_requires_the_approval()
    {
        // Defence in depth: waiving the change-control rule about held approvals does not
        // release the stage, because the stage's own exit gate still requires the signature.
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
            "waive", runId, "--rule", "CHG-001", "--as", "alex", "--reason", "Tracked elsewhere.",
            "--store", workspace.Store, "--policies", Policies);

        CliResult resumed = CliHarness.Run(
            "resume", runId, "--workflow", ShippedWorkflow, "--store", workspace.Store,
            "--workspace-root", workspace.Path_("workspaces"), "--template", Template,
            "--policies", Policies);

        resumed.ExitCode.ShouldBe(ExitCode.AwaitingHuman);
        resumed.Rendered.ShouldContain("awaiting approval");
    }

    [Fact]
    public void The_waiver_is_on_the_record_and_the_chain_still_verifies()
    {
        using TemporaryWorkspace workspace = new();
        string runId = Seed(workspace);

        CliHarness.Run(
            "waive", runId, "--rule", "CHG-001", "--as", "alex", "--reason", "Tracked elsewhere.",
            "--store", workspace.Store, "--policies", Policies);

        CliResult events = CliHarness.Run(
            "runs", "show", runId, "--events", "--store", workspace.Store);

        events.Rendered.ShouldContain("PolicyWaiverGranted");
        events.Rendered.ShouldContain("human:alex");

        CliHarness.Run("audit", "verify", runId, "--store", workspace.Store)
            .ExitCode.ShouldBe(ExitCode.Success);
    }

    [GeneratedRegex(@"run_\d{8}T\d{6}Z_[a-z0-9]+")]
    private static partial Regex RunIdPattern();
}
