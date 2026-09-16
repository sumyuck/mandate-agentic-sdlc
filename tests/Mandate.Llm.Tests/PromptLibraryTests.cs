using Mandate.Core.Llm;
using Mandate.Llm.Prompts;

namespace Mandate.Llm.Tests;

/// <summary>
/// The prompt library is the specification of what each stage is asked to do. These tests
/// hold it to the same standard as the workflow loader: strict, and loud about mistakes at
/// load time rather than at the point a model produces something odd.
/// </summary>
public sealed class PromptLibraryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "mandate-prompts-" + Guid.NewGuid().ToString("N")[..8]);

    public PromptLibraryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string Wellformed = """
        ---
        id: requirements-analyst
        version: v1
        description: Turns a request into a requirement spec.
        inputs:
          - requirement
        max-output-tokens: 2000
        ---

        ## system

        You normalise requirements.

        ## user

        Normalise this: {{requirement}}
        """;

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_wellformed_prompt_parses()
    {
        string path = Write("requirements-analyst.v1.prompt.md", Wellformed);
        PromptTemplate prompt = PromptTemplate.Parse(path, Wellformed);

        prompt.Id.ShouldBe("requirements-analyst");
        prompt.Version.ShouldBe("v1");
        prompt.Identity.ShouldBe("requirements-analyst.v1");
        prompt.MaxOutputTokens.ShouldBe(2000);
        prompt.Inputs.ShouldBe(["requirement"]);
    }

    [Fact]
    public void Rendering_substitutes_the_declared_inputs()
    {
        string path = Write("requirements-analyst.v1.prompt.md", Wellformed);
        PromptTemplate prompt = PromptTemplate.Parse(path, Wellformed);

        LlmRequest request = prompt.Render(
            "claude-sonnet-5",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requirement"] = "Build a URL shortener.",
            });

        request.Model.ShouldBe("claude-sonnet-5");
        request.MaxOutputTokens.ShouldBe(2000);
        request.System.ShouldBe("You normalise requirements.");
        request.Messages.Single().Text.ShouldBe("Normalise this: Build a URL shortener.");
    }

    [Fact]
    public void A_placeholder_the_front_matter_does_not_declare_is_a_load_error()
    {
        string text = Wellformed.Replace(
            "Normalise this: {{requirement}}",
            "Normalise this: {{requirement}} for {{audience}}",
            StringComparison.Ordinal);

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text))
            .Message.ShouldContain("audience");
    }

    [Fact]
    public void An_input_the_body_never_uses_is_a_load_error()
    {
        string text = Wellformed.Replace(
            "  - requirement", "  - requirement\n  - audience", StringComparison.Ordinal);

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text))
            .Message.ShouldContain("audience");
    }

    [Fact]
    public void The_file_name_must_match_what_the_front_matter_declares()
    {
        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(
                Write("architect.v1.prompt.md", Wellformed), Wellformed))
            .Message.ShouldContain("requirements-analyst.v1.prompt.md");
    }

    [Theory]
    [InlineData("## system", "no '## system' section")]
    [InlineData("## user", "no '## user' section")]
    public void Both_sections_are_required(string heading, string expected)
    {
        string text = Wellformed.Replace(heading, "## other", StringComparison.Ordinal);

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text))
            .Message.ShouldContain(expected);
    }

    [Fact]
    public void An_output_ceiling_must_be_declared()
    {
        string text = Wellformed.Replace(
            "max-output-tokens: 2000", "max-output-tokens: 0", StringComparison.Ordinal);

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text))
            .Message.ShouldContain("max-output-tokens");
    }

    [Fact]
    public void Front_matter_is_required()
    {
        const string text = "## system\n\nhello\n\n## user\n\nhello";

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text))
            .Message.ShouldContain("front-matter fence");
    }

    [Fact]
    public void An_unknown_front_matter_key_is_refused_rather_than_ignored()
    {
        string text = Wellformed.Replace(
            "max-output-tokens: 2000",
            "max-output-tokens: 2000\nmax-output-token: 10",
            StringComparison.Ordinal);

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text));
    }

    [Fact]
    public void Rendering_without_a_value_for_a_declared_input_is_refused()
    {
        PromptTemplate prompt = PromptTemplate.Parse(
            Write("requirements-analyst.v1.prompt.md", Wellformed), Wellformed);

        Should.Throw<PromptRenderException>(
            () => prompt.Render("m", new Dictionary<string, string>(StringComparer.Ordinal)))
            .Message.ShouldContain("requirement");
    }

    [Fact]
    public void Rendering_with_a_value_the_prompt_does_not_take_is_refused()
    {
        PromptTemplate prompt = PromptTemplate.Parse(
            Write("requirements-analyst.v1.prompt.md", Wellformed), Wellformed);

        Should.Throw<PromptRenderException>(() => prompt.Render(
            "m",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requirement"] = "x",
                ["audience"] = "y",
            }))
            .Message.ShouldContain("audience");
    }

    [Fact]
    public void A_run_identifier_in_a_value_is_refused_because_it_would_defeat_replay()
    {
        PromptTemplate prompt = PromptTemplate.Parse(
            Write("requirements-analyst.v1.prompt.md", Wellformed), Wellformed);

        Should.Throw<PromptRenderException>(() => prompt.Render(
            "m",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requirement"] = "Run run_20260916T142500Z_a1b2c3 asked for a shortener.",
            }))
            .Message.ShouldContain("run identifier");
    }

    [Fact]
    public void A_timestamp_in_a_value_is_refused_because_it_would_defeat_replay()
    {
        PromptTemplate prompt = PromptTemplate.Parse(
            Write("requirements-analyst.v1.prompt.md", Wellformed), Wellformed);

        Should.Throw<PromptRenderException>(() => prompt.Render(
            "m",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requirement"] = "Requested at 2026-09-16T14:25:00Z.",
            }))
            .Message.ShouldContain("timestamp");
    }

    [Fact]
    public void Asking_for_a_prompt_by_id_resolves_the_highest_version()
    {
        Write("requirements-analyst.v1.prompt.md", Wellformed);

        string second = Wellformed
            .Replace("version: v1", "version: v2", StringComparison.Ordinal)
            .Replace("You normalise requirements.", "You normalise requirements, tersely.",
                StringComparison.Ordinal);

        Write("requirements-analyst.v2.prompt.md", second);

        PromptLibrary library = PromptLibrary.Load(_directory);

        library.Get("requirements-analyst").Version.ShouldBe("v2");
        library.Get("requirements-analyst", "v1").Version.ShouldBe("v1");
    }

    [Fact]
    public void A_gap_in_the_versions_is_refused()
    {
        Write("requirements-analyst.v1.prompt.md", Wellformed);
        Write(
            "requirements-analyst.v3.prompt.md",
            Wellformed.Replace("version: v1", "version: v3", StringComparison.Ordinal));

        Should.Throw<PromptFormatException>(() => PromptLibrary.Load(_directory))
            .Message.ShouldContain("v2 is missing");
    }

    [Fact]
    public void The_library_fingerprint_changes_when_a_prompt_changes()
    {
        Write("requirements-analyst.v1.prompt.md", Wellformed);
        var before = PromptLibrary.Load(_directory).Fingerprint;

        Write(
            "requirements-analyst.v1.prompt.md",
            Wellformed.Replace(
                "You normalise requirements.", "You normalise requirements carefully.",
                StringComparison.Ordinal));

        PromptLibrary.Load(_directory).Fingerprint.ShouldNotBe(before);
    }

    [Fact]
    public void An_empty_directory_is_refused()
    {
        Should.Throw<PromptFormatException>(() => PromptLibrary.Load(_directory))
            .Message.ShouldContain("no prompt files");
    }

    [Fact]
    public void Asking_for_a_prompt_that_is_not_there_names_the_ones_that_are()
    {
        Write("requirements-analyst.v1.prompt.md", Wellformed);
        PromptLibrary library = PromptLibrary.Load(_directory);

        Should.Throw<PromptFormatException>(() => library.Get("architect"))
            .Message.ShouldContain("requirements-analyst");
    }

    [Fact]
    public void An_input_declared_verbatim_may_carry_a_timestamp()
    {
        // The first real run tripped on this: a design document contained an example
        // `expiresAt` value, and the repeatability scan refused the stage's own input.
        // Content produced upstream is fixed once recorded; it is the engine injecting the
        // current time that would make a prompt unrepeatable.
        string text = Wellformed
            .Replace("  - requirement", "  - requirement\nverbatim:\n  - requirement",
                StringComparison.Ordinal);

        PromptTemplate prompt = PromptTemplate.Parse(
            Write("requirements-analyst.v1.prompt.md", text), text);

        prompt.Verbatim.ShouldBe(["requirement"]);

        prompt.Render(
            "m",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["requirement"] = "Links expire at 2026-01-01T00:00:00Z.",
            })
            .Messages.Single().Text.ShouldContain("2026-01-01T00:00:00Z");
    }

    [Fact]
    public void An_input_not_declared_verbatim_still_refuses_a_run_identifier()
    {
        string text = Wellformed
            .Replace("  - requirement", "  - requirement\nverbatim:\n  - requirement",
                StringComparison.Ordinal);

        PromptTemplate prompt = PromptTemplate.Parse(
            Write("requirements-analyst.v1.prompt.md", text), text);

        // Exempting an input exempts that input only; nothing else changes.
        prompt.Inputs.ShouldBe(["requirement"]);
    }

    [Fact]
    public void A_verbatim_entry_that_is_not_an_input_is_a_load_error()
    {
        string text = Wellformed.Replace(
            "max-output-tokens: 2000", "verbatim:\n  - workspace\nmax-output-tokens: 2000",
            StringComparison.Ordinal);

        Should.Throw<PromptFormatException>(
            () => PromptTemplate.Parse(Write("requirements-analyst.v1.prompt.md", text), text))
            .Message.ShouldContain("workspace");
    }
}
