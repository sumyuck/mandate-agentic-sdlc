using Mandate.Cli.Tests.Support;

namespace Mandate.Cli.Tests;

/// <summary>
/// The model-layer commands. Every test here runs offline — the point of the layer is that
/// a reviewer never needs a key, and a test suite that quietly needed one would disprove it.
/// </summary>
public sealed class LlmCommandTests
{
    private static string Prompts => Path.Combine(RepositoryRoot.Path, "prompts");

    private static string Cassettes => Path.Combine(RepositoryRoot.Path, "cassettes");

    private static string Pricing =>
        Path.Combine(RepositoryRoot.Path, "config", "model-pricing.yaml");

    private static string[] Paths =>
        ["--prompts", Prompts, "--cassettes", Cassettes, "--pricing", Pricing];

    [Fact]
    public void Prompts_lists_the_library_with_its_fingerprint()
    {
        CliResult result = CliHarness.Run("llm", "prompts", "--prompts", Prompts);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("connectivity-check.v1");
        result.Rendered.ShouldContain("library");
    }

    [Fact]
    public void Prompts_reports_a_missing_directory_as_bad_input()
    {
        CliResult result = CliHarness.Run(
            "llm", "prompts", "--prompts", Path.Combine(Path.GetTempPath(), "no-prompts-here"));

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("cannot load prompts");
    }

    [Fact]
    public void Check_replays_the_recorded_exchange_without_a_key()
    {
        CliResult result = CliHarness.Run(["llm", "check", .. Paths]);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("replay");
        result.Rendered.ShouldContain("mandate-ok");

        // The recorded cost is reported as what the call would cost, not as money spent.
        result.Rendered.ShouldContain("cost if live");
    }

    [Fact]
    public void Check_in_stub_mode_says_plainly_that_no_model_was_consulted()
    {
        CliResult result = CliHarness.Run(["llm", "check", "--llm", "stub", .. Paths]);

        result.ExitCode.ShouldBe(ExitCode.Success);
        result.Rendered.ShouldContain("no model");
    }

    [Fact]
    public void Check_against_a_model_with_no_recording_fails_with_an_instruction()
    {
        CliResult result = CliHarness.Run(
            ["llm", "check", "--model", "claude-sonnet-5", .. Paths]);

        result.ExitCode.ShouldBe(ExitCode.Failed);
        result.Rendered.ShouldContain("No recording");
        result.Rendered.ShouldContain("re-record");
    }

    [Fact]
    public void An_unknown_mode_is_refused_before_anything_is_loaded()
    {
        CliResult result = CliHarness.Run(["llm", "check", "--llm", "wishful", .. Paths]);

        result.ExitCode.ShouldNotBe(ExitCode.Success);
    }

    [Fact]
    public void Check_reports_a_missing_price_list_as_bad_input()
    {
        CliResult result = CliHarness.Run(
            "llm", "check",
            "--prompts", Prompts,
            "--cassettes", Cassettes,
            "--pricing", Path.Combine(Path.GetTempPath(), "no-prices-here.yaml"));

        result.ExitCode.ShouldBe(ExitCode.BadInput);
        result.Rendered.ShouldContain("model layer unavailable");
    }
}
