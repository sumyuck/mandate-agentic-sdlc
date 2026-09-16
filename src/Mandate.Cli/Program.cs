using Mandate.Cli.Commands;
using Spectre.Console.Cli;

namespace Mandate.Cli;

/// <summary>
/// Composition root for the <c>mandate</c> CLI.
/// </summary>
/// <remarks>
/// This is the only place in the system where adapters (persistence, policy, LLM,
/// telemetry) are bound to the ports the engine depends on. The orchestration engine
/// itself references no infrastructure — see docs/adr/0003-hexagonal-layering.md.
/// </remarks>
internal static class Program
{
    public static int Main(string[] args)
    {
        CommandApp app = new();

        app.Configure(config =>
        {
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
        });

        return app.Run(args);
    }
}
