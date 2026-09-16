using System.Text.Json;
using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>
/// The read-only commands: identity, workflow validation and rendering.
/// </summary>
public sealed class InfoAndWorkflowCommandTests
{
    private static string ShippedWorkflow => Path.Combine(
        RepositoryRoot.Path, "workflows", "sdlc.v1.yaml");

    [Fact]
    public void Info_reports_the_engine_identity()
    {
        CliResult result = CliHarness.Run("info");

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("fingerprint");
        result.Rendered.ShouldContain("mandate");
    }

    [Fact]
    public void Info_json_is_parseable_and_not_wrapped_by_the_renderer()
    {
        // The renderer hard-wraps at terminal width; machine output must bypass it or a
        // consumer gets a broken document.
        CliResult result = CliHarness.Run("info", "--json");

        result.ExitCode.ShouldBe(ExitCode.Success);

        using JsonDocument document = JsonDocument.Parse(result.Plain);

        document.RootElement.GetProperty("fingerprint").GetString()!
            .ShouldStartWith("mandate/");
        document.RootElement.GetProperty("targetFramework").GetString()!
            .ShouldContain(".NETCoreApp");
    }

    [Fact]
    public void An_unknown_command_is_refused()
    {
        CliHarness.Run("teleport").ExitCode.ShouldNotBe(ExitCode.Success);
    }

    [Fact]
    public void Validate_accepts_the_shipped_lifecycle_and_summarises_its_structure()
    {
        CliResult result = CliHarness.Run("workflow", "validate", ShippedWorkflow);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("sdlc@v1");
        result.Rendered.ShouldContain("valid");
        result.Rendered.ShouldContain("human checkpoint");
    }

    [Fact]
    public void Validate_reports_a_missing_file_as_bad_input()
    {
        CliResult result = CliHarness.Run("workflow", "validate", "does/not/exist.yaml");

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("does not exist");
    }

    [Fact]
    public void Validate_reports_a_malformed_file_as_bad_input()
    {
        using TemporaryWorkspace workspace = new();

        string path = workspace.Write("broken.yaml", "name: sdlc\nversion: [unclosed\n");

        CliResult result = CliHarness.Run("workflow", "validate", path);

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("format error");
    }

    [Fact]
    public void Validate_reports_an_unexecutable_workflow_as_a_failure_with_its_codes()
    {
        using TemporaryWorkspace workspace = new();

        // Two nodes depending on each other: a cycle in the forward graph.
        string path = workspace.Write("cyclic.yaml", """
            name: sdlc
            version: v1
            nodes:
              - id: a
                stage: requirements
                agent: x
                autonomy: act-in-sandbox
              - id: b
                stage: implementation
                agent: y
                autonomy: act-in-sandbox
            edges:
              - from: a
                to: b
              - from: b
                to: a
            """);

        CliResult result = CliHarness.Run("workflow", "validate", path);

        result.ExitCode.ShouldBe(ExitCode.Failed);
        result.Rendered.ShouldContain("WF030");
        result.Rendered.ShouldContain("cannot be executed");
    }

    [Fact]
    public void Validate_quiet_prints_nothing_when_the_workflow_is_sound()
    {
        CliResult result = CliHarness.Run("workflow", "validate", ShippedWorkflow, "--quiet");

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.All.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void Render_writes_a_mermaid_diagram_to_stdout()
    {
        CliResult result = CliHarness.Run("workflow", "render", ShippedWorkflow);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Plain.ShouldStartWith("flowchart TD");
        result.Plain.ShouldContain("release_readiness{{");
    }

    [Fact]
    public void Render_markdown_produces_a_fenced_block()
    {
        CliResult result = CliHarness.Run("workflow", "render", ShippedWorkflow, "--markdown");

        result.Plain.ShouldStartWith("```mermaid");
        result.Plain.TrimEnd().ShouldEndWith("```");
    }

    [Fact]
    public void Render_to_a_file_creates_the_directory_it_needs()
    {
        using TemporaryWorkspace workspace = new();

        string output = workspace.Path_("nested", "deeper", "sdlc.mmd");

        CliResult result = CliHarness.Run(
            "workflow", "render", ShippedWorkflow, "-o", output);

        result.ExitCode.ShouldBe(ExitCode.Success);
        File.Exists(output).ShouldBeTrue();
        File.ReadAllText(output).ShouldStartWith("flowchart TD");
    }

    [Fact]
    public void The_rendered_diagram_matches_the_one_committed_to_the_repository()
    {
        // The diagram in the documentation is generated from the workflow the engine runs.
        // If this fails, the committed diagram is stale: run `make diagram`.
        using TemporaryWorkspace workspace = new();

        string output = workspace.Path_("fresh.mmd");
        CliHarness.Run("workflow", "render", ShippedWorkflow, "-o", output);

        string committed = Path.Combine(RepositoryRoot.Path, "docs", "diagrams", "sdlc.v1.mmd");

        Normalise(File.ReadAllText(output))
            .ShouldBe(Normalise(File.ReadAllText(committed)));
    }

    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
}
