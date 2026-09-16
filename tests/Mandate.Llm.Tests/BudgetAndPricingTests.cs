using Mandate.Core.Llm;
using Mandate.Llm.Clients;
using Mandate.Llm.Pricing;
using Mandate.Llm.Tests.Support;

namespace Mandate.Llm.Tests;

/// <summary>
/// Spend is a governance control here, not an optimisation, so it is tested like one: the
/// ceiling must refuse the call that would breach it, not report the breach afterwards.
/// </summary>
public sealed class BudgetAndPricingTests
{
    private static ModelPriceBook Prices => ModelPriceBook.Load(
        Path.Combine(RepositoryRoot.Path, ModelPriceBook.DefaultPath));

    [Fact]
    public void The_repositorys_price_list_loads_and_is_dated()
    {
        ModelPriceBook book = Prices;

        book.AsOf.ShouldNotBeNullOrWhiteSpace();
        book.Source.ShouldStartWith("https://");
        book.Models.ShouldContain("claude-sonnet-5");
        book.Models.ShouldContain("claude-haiku-4-5");
    }

    [Fact]
    public void A_million_input_tokens_of_sonnet_costs_two_dollars()
    {
        // The published figure, checked against arithmetic that runs in nano-dollars. If
        // this drifts, either the price file was edited or the cost maths is wrong, and
        // both are worth stopping for.
        long nanos = Prices.NanoUsdFor("claude-sonnet-5", new LlmUsage(1_000_000, 0, 0, 0))!.Value;

        (nanos / 1_000_000_000m).ShouldBe(2.00m);
    }

    [Fact]
    public void Each_kind_of_token_is_charged_at_its_own_rate()
    {
        // 1M each of input, output, cache read and cache write on Haiku 4.5:
        // $1 + $5 + $0.10 + $1.25.
        long nanos = Prices
            .NanoUsdFor("claude-haiku-4-5", new LlmUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000))!
            .Value;

        (nanos / 1_000_000_000m).ShouldBe(7.35m);
    }

    [Fact]
    public void An_unpriced_model_reports_no_cost_rather_than_zero()
    {
        Prices.NanoUsdFor("some-other-vendors-model", new LlmUsage(1000, 1000, 0, 0)).ShouldBeNull();
    }

    [Fact]
    public async Task Spend_accumulates_across_calls()
    {
        BudgetedLlmClient budgeted = new(
            new FakeLlmClient(_ => FakeLlmClient.Answer(inputTokens: 1000, outputTokens: 200)),
            LlmBudget.Unlimited,
            Prices);

        await budgeted.CompleteAsync(Requests.A(), CancellationToken.None);
        await budgeted.CompleteAsync(Requests.A(user: "Again."), CancellationToken.None);

        LlmSpend spend = budgeted.Spend;

        spend.Calls.ShouldBe(2);
        spend.Usage.InputTokens.ShouldBe(2000);
        spend.Usage.OutputTokens.ShouldBe(400);

        // 2 x (1000 input at $1/MTok + 200 output at $5/MTok) = 2 x $0.002 = $0.004.
        spend.Usd.ShouldBe(0.004m);
    }

    [Fact]
    public async Task The_call_ceiling_refuses_the_call_that_would_breach_it()
    {
        FakeLlmClient live = new();
        BudgetedLlmClient budgeted = new(live, new LlmBudget(2, int.MaxValue, null), Prices);

        await budgeted.CompleteAsync(Requests.A(user: "one"), CancellationToken.None);
        await budgeted.CompleteAsync(Requests.A(user: "two"), CancellationToken.None);

        await Should.ThrowAsync<LlmBudgetExceededException>(
            () => budgeted.CompleteAsync(Requests.A(user: "three"), CancellationToken.None));

        // Refused, not reported: the third call never reached the provider.
        live.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task The_token_ceiling_is_checked_against_the_worst_case_before_spending()
    {
        // The ceiling is below this single call's declared output ceiling, so it must be
        // refused up front rather than after the tokens have been generated and billed.
        BudgetedLlmClient budgeted = new(
            new ExplodingLlmClient(), new LlmBudget(10, 500, null), Prices);

        await Should.ThrowAsync<LlmBudgetExceededException>(
            () => budgeted.CompleteAsync(
                Requests.A(maxOutputTokens: 4000), CancellationToken.None));
    }

    [Fact]
    public async Task The_dollar_ceiling_is_checked_against_the_worst_case_before_spending()
    {
        // 8000 output tokens of Sonnet is $0.08 at worst; the ceiling is one cent.
        BudgetedLlmClient budgeted = new(
            new ExplodingLlmClient(), new LlmBudget(10, int.MaxValue, 0.01m), Prices);

        LlmBudgetExceededException refused = await Should.ThrowAsync<LlmBudgetExceededException>(
            () => budgeted.CompleteAsync(
                Requests.A(model: "claude-sonnet-5", maxOutputTokens: 8000),
                CancellationToken.None));

        refused.Message.ShouldContain("Spend ceiling");
    }

    [Fact]
    public async Task A_call_that_fits_inside_the_dollar_ceiling_goes_through()
    {
        FakeLlmClient live = new();

        BudgetedLlmClient budgeted = new(
            live, new LlmBudget(10, int.MaxValue, 1.00m), Prices);

        await budgeted.CompleteAsync(
            Requests.A(model: "claude-sonnet-5", maxOutputTokens: 8000), CancellationToken.None);

        live.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task An_unpriced_model_is_bounded_by_calls_and_tokens_not_by_dollars()
    {
        FakeLlmClient live = new(_ => FakeLlmClient.Answer(model: "unpriced-model"));

        BudgetedLlmClient budgeted = new(
            live, new LlmBudget(10, int.MaxValue, 0.000001m), Prices);

        // No price means no dollar bound to enforce; the call is allowed, and the fact that
        // its cost is unknown is reported rather than silently counted as zero.
        await budgeted.CompleteAsync(
            Requests.A(model: "unpriced-model"), CancellationToken.None);

        budgeted.Spend.UnpricedCalls.ShouldBe(1);
        budgeted.Spend.NanoUsd.ShouldBe(0);
        budgeted.Spend.Summary.ShouldContain("unpriced");
    }

    [Fact]
    public void A_ceiling_of_zero_is_not_a_ceiling()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new LlmBudget(0, 10, null).Validated());
        Should.Throw<ArgumentOutOfRangeException>(() => new LlmBudget(10, 0, null).Validated());
        Should.Throw<ArgumentOutOfRangeException>(() => new LlmBudget(10, 10, 0m).Validated());
    }

    [Fact]
    public void The_description_says_what_the_ceilings_are()
    {
        new BudgetedLlmClient(new FakeLlmClient(), new LlmBudget(60, 1_500_000, 5m), Prices)
            .Description
            .ShouldBe("fake, capped at 60 calls / 1,500,000 tokens / $5.00");
    }
}
