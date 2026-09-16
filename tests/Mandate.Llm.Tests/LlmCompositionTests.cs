using Mandate.Core.Llm;
using Mandate.Llm.Clients;
using Mandate.Llm.Prompts;
using Mandate.Llm.Tests.Support;

namespace Mandate.Llm.Tests;

/// <summary>
/// The composition root for the model layer. These tests assert the property the whole
/// design exists for: nothing reaches a provider unless somebody asked for a mode that does.
/// </summary>
public sealed class LlmCompositionTests
{
    private static LlmOptions Options(LlmMode mode) => new(
        Mode: mode,
        PromptDirectory: Path.Combine(RepositoryRoot.Path, PromptLibrary.DefaultDirectory),
        CassetteRoot: Path.Combine(RepositoryRoot.Path, "cassettes"),
        PricingPath: Path.Combine(RepositoryRoot.Path, "config", "model-pricing.yaml"));

    [Fact]
    public void The_repositorys_prompt_library_loads()
    {
        PromptLibrary library = PromptLibrary.Load(
            Path.Combine(RepositoryRoot.Path, PromptLibrary.DefaultDirectory));

        library.Prompts.ShouldNotBeEmpty();
        library.Contains("connectivity-check").ShouldBeTrue();
        library.Fingerprint.Hex.Length.ShouldBe(64);
    }

    [Fact]
    public void Every_prompt_declares_an_output_ceiling_and_a_description()
    {
        PromptLibrary library = PromptLibrary.Load(
            Path.Combine(RepositoryRoot.Path, PromptLibrary.DefaultDirectory));

        foreach (PromptTemplate prompt in library.Prompts)
        {
            prompt.MaxOutputTokens.ShouldBeGreaterThan(0);
            prompt.Description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Replay_is_what_you_get_when_you_ask_for_nothing()
    {
        new LlmOptions().Mode.ShouldBe(LlmMode.Replay);
    }

    [Theory]
    [InlineData(LlmMode.Replay)]
    [InlineData(LlmMode.Stub)]
    public void The_offline_modes_need_no_api_key(LlmMode mode)
    {
        using LlmLayer layer = LlmComposition.Build(Options(mode), new TestClock());

        layer.Mode.ShouldBe(mode);
        layer.Client.ShouldNotBeOfType<AnthropicLlmClient>();
    }

    [Fact]
    public void A_budget_wraps_whatever_mode_was_chosen()
    {
        using LlmLayer layer = LlmComposition.Build(
            Options(LlmMode.Stub) with { Budget = LlmBudget.Default }, new TestClock());

        layer.Budget.ShouldNotBeNull();
        layer.Client.ShouldBeOfType<BudgetedLlmClient>();
        layer.Description.ShouldContain("capped at 60 calls");
    }

    [Fact]
    public void Without_a_budget_nothing_is_wrapped()
    {
        using LlmLayer layer = LlmComposition.Build(Options(LlmMode.Stub), new TestClock());

        layer.Budget.ShouldBeNull();
        layer.Client.ShouldBeOfType<StubLlmClient>();
    }

    [Fact]
    public void The_layer_describes_itself_well_enough_to_read_in_a_run_report()
    {
        using LlmLayer layer = LlmComposition.Build(Options(LlmMode.Replay), new TestClock());

        layer.Description.ShouldContain("replay");
        layer.Description.ShouldContain("prompt(s)");
    }

    [Fact]
    public void Offline_modes_still_load_prices_so_a_run_can_report_what_it_would_have_cost()
    {
        using LlmLayer layer = LlmComposition.Build(Options(LlmMode.Stub), new TestClock());

        layer.Prices.Models.ShouldContain("claude-sonnet-5");
    }

    [Fact]
    public void Asking_for_live_calls_without_a_key_fails_before_anything_is_created()
    {
        LlmException refused = Should.Throw<LlmException>(() => LlmComposition.Build(
            Options(LlmMode.Live) with { ApiKey = "   " }, new TestClock()));

        refused.Message.ShouldContain("ANTHROPIC_API_KEY");
    }
}
