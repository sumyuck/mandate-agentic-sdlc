using Mandate.Core.Llm;
using Mandate.Core.Time;
using Mandate.Llm.Cassettes;
using Mandate.Llm.Clients;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;

namespace Mandate.Llm;

/// <summary>How the model layer should behave for a run.</summary>
public enum LlmMode
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Call the provider. Costs money; produces no recording.</summary>
    Live = 1,

    /// <summary>Call the provider and keep every exchange, so the run can be replayed.</summary>
    Record = 2,

    /// <summary>Answer from recordings. No key, no network, no spend.</summary>
    Replay = 3,

    /// <summary>Answer with synthetic text. No model is consulted.</summary>
    Stub = 4,
}

/// <summary>Everything a run needs from the model layer, assembled and described.</summary>
/// <param name="Mode">Which mode was assembled.</param>
/// <param name="Client">The client the agents call.</param>
/// <param name="Prompts">The prompt library the agents render from.</param>
/// <param name="Prices">The price list costs are computed against.</param>
/// <param name="Budget">The ceiling applied, when one was.</param>
/// <param name="Lifetime">Anything that must be disposed with the run.</param>
public sealed record LlmLayer(
    LlmMode Mode,
    ILlmClient Client,
    PromptLibrary Prompts,
    ModelPriceBook Prices,
    BudgetedLlmClient? Budget,
    IDisposable? Lifetime) : IDisposable
{
    /// <summary>A line describing the whole layer, for a run's evidence.</summary>
    public string Description =>
        $"{Mode.ToString().ToLowerInvariant()} · {Client.Description} · "
        + $"{Prompts.Prompts.Length} prompt(s) at {Prompts.Fingerprint.Abbreviated}";

    /// <inheritdoc />
    public void Dispose() => Lifetime?.Dispose();
}

/// <summary>What to build the model layer from.</summary>
/// <param name="Mode">Which mode to assemble.</param>
/// <param name="PromptDirectory">Where the prompts live.</param>
/// <param name="CassetteRoot">Where recordings live.</param>
/// <param name="PricingPath">Where the price list lives.</param>
/// <param name="Budget">The spend ceiling, or <see langword="null"/> for none.</param>
/// <param name="Refresh">Re-ask calls that are already recorded.</param>
/// <param name="ApiKey">An explicit key, or <see langword="null"/> to read the environment.</param>
/// <param name="StubResponder">
/// What the stub should answer with. Supplied by the caller rather than built in, because
/// what a useful stub answer looks like depends on who is asking — the agents know their
/// own output contract, and this project must not know about theirs.
/// </param>
public sealed record LlmOptions(
    LlmMode Mode = LlmMode.Replay,
    string PromptDirectory = PromptLibrary.DefaultDirectory,
    string CassetteRoot = FileCassetteStore.DefaultRoot,
    string PricingPath = ModelPriceBook.DefaultPath,
    LlmBudget? Budget = null,
    bool Refresh = false,
    string? ApiKey = null,
    Func<LlmRequest, string>? StubResponder = null);

/// <summary>
/// Assembles the model layer from a mode.
/// </summary>
/// <remarks>
/// <para>
/// The composition is the interesting part, and it is deliberately in one readable method:
/// live is the provider; record is the provider with a recorder around it; replay is the
/// cassettes alone; stub is neither. A budget wraps whichever of those makes real calls.
/// </para>
/// <para>
/// Replay defaults on. The mode that spends money should be the one somebody typed.
/// </para>
/// </remarks>
public static class LlmComposition
{
    /// <summary>Builds the layer.</summary>
    /// <exception cref="PromptFormatException">The prompts could not be loaded.</exception>
    /// <exception cref="ModelPricingException">The price list could not be loaded.</exception>
    /// <exception cref="LlmException">Live calls were asked for with no API key.</exception>
    public static LlmLayer Build(LlmOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        PromptLibrary prompts = PromptLibrary.Load(options.PromptDirectory);

        // Replay and stub still load prices. Neither spends anything, but both report what
        // the run would have cost, and a report that silently omits the figure in the two
        // modes a reviewer actually uses would be the wrong way round.
        ModelPriceBook prices = ModelPriceBook.Load(options.PricingPath);

        FileCassetteStore cassettes = new(options.CassetteRoot);

        (ILlmClient client, IDisposable? lifetime) = options.Mode switch
        {
            LlmMode.Live => LiveClient(options),
            LlmMode.Record => RecordingClient(options, cassettes, clock),
            LlmMode.Replay => (new ReplayLlmClient(cassettes), (IDisposable?)null),
            LlmMode.Stub => (new StubLlmClient(options.StubResponder), (IDisposable?)null),
            _ => throw new ArgumentOutOfRangeException(
                nameof(options), options.Mode, "Not a model layer mode."),
        };

        BudgetedLlmClient? budget = null;

        if (options.Budget is { } ceiling)
        {
            budget = new BudgetedLlmClient(client, ceiling, prices);
            client = budget;
        }

        return new LlmLayer(options.Mode, client, prompts, prices, budget, lifetime);
    }

    private static (ILlmClient, IDisposable?) LiveClient(LlmOptions options)
    {
        AnthropicLlmClient live = new(options.ApiKey ?? EnvironmentKey());
        return (live, live);
    }

    private static (ILlmClient, IDisposable?) RecordingClient(
        LlmOptions options, ICassetteStore cassettes, IClock clock)
    {
        AnthropicLlmClient live = new(options.ApiKey ?? EnvironmentKey());
        return (new RecordingLlmClient(live, cassettes, clock, options.Refresh), live);
    }

    private static string? EnvironmentKey() =>
        Environment.GetEnvironmentVariable(AnthropicLlmClient.ApiKeyVariable);
}
