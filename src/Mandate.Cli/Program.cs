using Mandate.Cli.Commands;
using Spectre.Console.Cli;

namespace Mandate.Cli;

/// <summary>
/// Composition root for the <c>mandate</c> CLI.
/// </summary>
/// <remarks>
/// This is the only place in the system where adapters (persistence, policy, LLM, telemetry)
/// are bound to the ports the engine depends on. The orchestration engine itself references
/// no infrastructure — see docs/adr/0003-hexagonal-layering.md.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        CommandApp app = new();
        app.Configure(Configure);
        return app.Run(args);
    }

    /// <summary>
    /// Declares the command surface.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Main"/> so the tests drive exactly the command surface the
    /// binary exposes. A test harness that configured its own commands would verify a CLI
    /// nobody ships.
    /// </remarks>
    public static void Configure(IConfigurator config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.SetApplicationName("mandate");
        config.UseStrictParsing();
        config.ValidateExamples();

        config.AddCommand<InfoCommand>("info")
            .WithDescription("Show engine identity and host diagnostics.")
            .WithExample("info")
            .WithExample("info", "--json");

        config.AddCommand<RunCommand>("run")
            .WithDescription("Execute the lifecycle for a requirement.")
            .WithExample("run", "\"Build a URL shortener\"", "--scenario", "greenfield")
            .WithExample("run", "\"Add click analytics\"", "--scenario", "brownfield");

        config.AddCommand<ApproveCommand>("approve")
            .WithDescription("Record a human approval against a parked stage.")
            .WithExample("approve", "run_20260916T142500Z_a1b2c3", "--role", "tech-lead")
            .WithExample(
                "approve", "run_20260916T142500Z_a1b2c3", "--role", "tech-lead",
                "--as", "alex", "--note", "Design matches the agreed scope.");

        config.AddCommand<DenyCommand>("deny")
            .WithDescription("Refuse a parked stage, with a reason.")
            .WithExample(
                "deny", "run_20260916T142500Z_a1b2c3", "--role", "tech-lead",
                "--note", "Blast radius is larger than the requirement justifies.");

        config.AddCommand<ResumeCommand>("resume")
            .WithDescription("Continue a run that stopped, from its recorded log.")
            .WithExample("resume", "run_20260916T142500Z_a1b2c3");

        config.AddCommand<AmendCommand>("amend")
            .WithDescription("Record that an input the run already acted on has changed.")
            .WithExample(
                "amend", "run_20260916T142500Z_a1b2c3", "--stage", "requirements",
                "--as", "muskan", "--reason", "Expiry means a TTL, not one-time use.");

        config.AddCommand<WaiveCommand>("waive")
            .WithDescription("Allow a policy violation through, on the record.")
            .WithExample(
                "waive", "run_20260916T142500Z_a1b2c3", "--rule", "CHG-003",
                "--as", "alex", "--reason", "Covered by the manual test plan attached to INC-42.")
            .WithExample(
                "waive", "run_20260916T142500Z_a1b2c3", "--rule", "CHG-003", "--withdraw");

        config.AddBranch("policy", policy =>
        {
            policy.SetDescription("Inspect and evaluate the policy packs.");

            policy.AddCommand<ListPolicyCommand>("list")
                .WithDescription("List the rules this system asserts it obeys.")
                .WithExample("policy", "list")
                .WithExample("policy", "list", "--category", "security");

            policy.AddCommand<CheckPolicyCommand>("check")
                .WithDescription("Evaluate every policy pack against a recorded run.")
                .WithExample("policy", "check", "run_20260916T142500Z_a1b2c3");
        });

        config.AddCommand<StopRunCommand>("stop")
            .WithDescription("Ask a run to halt at its next safe boundary.")
            .WithExample("stop", "run_20260916T142500Z_a1b2c3")
            .WithExample("stop", "run_20260916T142500Z_a1b2c3", "--clear");

        config.AddBranch("runs", runs =>
        {
            runs.SetDescription("Inspect and export recorded runs.");

            runs.AddCommand<ListRunsCommand>("list")
                .WithDescription("List recorded runs, most recent first.")
                .WithExample("runs", "list")
                .WithExample("runs", "list", "--limit", "5");

            runs.AddCommand<ShowRunCommand>("show")
                .WithDescription("Rebuild a run from its log and show where it stands.")
                .WithExample("runs", "show", "run_20260916T142500Z_a1b2c3")
                .WithExample("runs", "show", "run_20260916T142500Z_a1b2c3", "--events");

            runs.AddCommand<ExportRunCommand>("export")
                .WithDescription("Write a run's evidence to a reviewable directory.")
                .WithExample("runs", "export", "run_20260916T142500Z_a1b2c3");
        });

        config.AddBranch("audit", audit =>
        {
            audit.SetDescription("Verify the integrity of recorded runs.");

            audit.AddCommand<AuditVerifyCommand>("verify")
                .WithDescription("Prove a run's audit log has not been altered.")
                .WithExample("audit", "verify")
                .WithExample("audit", "verify", "run_20260916T142500Z_a1b2c3");
        });

        config.AddBranch("workflow", workflow =>
        {
            workflow.SetDescription("Inspect the declarative lifecycle definition.");

            workflow.AddCommand<ValidateWorkflowCommand>("validate")
                .WithDescription("Check that a workflow file can be loaded and executed.")
                .WithExample("workflow", "validate")
                .WithExample("workflow", "validate", "workflows/sdlc.v1.yaml");

            workflow.AddCommand<RenderWorkflowCommand>("render")
                .WithDescription("Render the lifecycle as a Mermaid diagram.")
                .WithExample("workflow", "render", "--markdown")
                .WithExample("workflow", "render", "-o", "docs/diagrams/sdlc.mmd");
        });
    }
}

/// <summary>
/// The exit codes every command uses.
/// </summary>
/// <remarks>
/// Distinct codes for "the work failed" and "a human is needed" because a CI job or a demo
/// script has to tell them apart: a run waiting on an approval is not a broken run, and
/// treating it as one would make the human checkpoint look like an error.
/// </remarks>
internal static class ExitCode
{
    /// <summary>The command did what was asked.</summary>
    public const int Success = 0;

    /// <summary>The work failed, or verification found a defect.</summary>
    public const int Failed = 1;

    /// <summary>The inputs were wrong: a bad id, a missing file, an unusable configuration.</summary>
    public const int BadInput = 2;

    /// <summary>The run is complete as far as it can go and is waiting on a human.</summary>
    public const int AwaitingHuman = 3;
}
