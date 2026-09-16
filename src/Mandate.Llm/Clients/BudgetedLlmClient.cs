using System.Globalization;
using Mandate.Core.Llm;
using Mandate.Llm.Pricing;

namespace Mandate.Llm.Clients;

/// <summary>What a single run is allowed to spend on models.</summary>
/// <remarks>
/// Three ceilings rather than one because they fail in different ways. A runaway loop
/// breaches the call count long before the dollar cap; a stage that pastes an entire
/// repository into a prompt breaches the token cap on its first call; and the dollar cap is
/// the one a human actually agreed to. Any of the three is enough to stop.
/// </remarks>
/// <param name="MaxCalls">How many model calls the run may make.</param>
/// <param name="MaxTokens">How many tokens, of every kind, it may consume.</param>
/// <param name="MaxUsd">
/// How many dollars it may spend, or <see langword="null"/> for no dollar ceiling. A run
/// against unpriced models can only be bounded by calls and tokens.
/// </param>
public sealed record LlmBudget(int MaxCalls, int MaxTokens, decimal? MaxUsd)
{
    /// <summary>
    /// The default ceiling for one run.
    /// </summary>
    /// <remarks>
    /// Sized for the lifecycle this system executes: eleven stages, a handful of retries and
    /// a re-plan or two. A run that wants more than this has stopped doing what it was asked
    /// and started doing something else.
    /// </remarks>
    public static LlmBudget Default { get; } = new(MaxCalls: 60, MaxTokens: 1_500_000, MaxUsd: 5m);

    /// <summary>No ceiling. For tests, and for a deliberate decision made by a human.</summary>
    public static LlmBudget Unlimited { get; } =
        new(MaxCalls: int.MaxValue, MaxTokens: int.MaxValue, MaxUsd: null);

    /// <summary>Validates the ceilings.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A ceiling is not positive.</exception>
    public LlmBudget Validated()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTokens);

        if (MaxUsd is { } dollars && dollars <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxUsd), dollars, "A dollar ceiling must be positive, or absent.");
        }

        return this;
    }
}

/// <summary>What a run has spent so far.</summary>
/// <param name="Calls">Model calls made.</param>
/// <param name="Usage">Tokens consumed.</param>
/// <param name="NanoUsd">Cost in billionths of a dollar, of the calls that were priced.</param>
/// <param name="UnpricedCalls">
/// Calls against a model with no published price. Reported separately so a cost figure is
/// never quietly understated by the calls it could not account for.
/// </param>
public readonly record struct LlmSpend(int Calls, LlmUsage Usage, long NanoUsd, int UnpricedCalls)
{
    /// <summary>The spend in dollars.</summary>
    public decimal Usd => NanoUsd / 1_000_000_000m;

    /// <summary>A one-line summary for the operator.</summary>
    public string Summary
    {
        get
        {
            string unpriced = UnpricedCalls > 0
                ? string.Create(CultureInfo.InvariantCulture, $" (+{UnpricedCalls} unpriced)")
                : string.Empty;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{Calls} call(s), {Usage.TotalTokens:N0} tokens, ${Usd:F4}{unpriced}");
        }
    }
}

/// <summary>
/// Refuses a call that would take a run past what it was allowed to spend.
/// </summary>
/// <remarks>
/// <para>
/// A spend guardrail is a policy control, not an optimisation. An agentic system that can
/// loop can spend without bound, and "we watched the dashboard" is not a control anyone
/// should accept. This one refuses the call rather than reporting the overrun afterwards.
/// </para>
/// <para>
/// The check is made against the call's <em>worst case</em> — its input as sent, plus the
/// full output ceiling it declared — because the actual cost is not knowable until the
/// money is already spent. It errs towards refusing a call that would have fitted, which is
/// the right direction for a ceiling to err in.
/// </para>
/// <para>
/// Replayed and stubbed calls are counted too, at the same prices. The ceiling bounds what
/// the run <em>would</em> cost, not what this particular execution of it happens to bill —
/// otherwise a run that breaches its budget live would sail through in replay, and replay
/// would stop predicting anything useful about the run it is replaying. Whether the money
/// was actually spent is recorded per call, as the call's source.
/// </para>
/// </remarks>
public sealed class BudgetedLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly LlmBudget _budget;
    private readonly ModelPriceBook _prices;
    private readonly Lock _gate = new();

    private int _calls;
    private LlmUsage _usage;
    private long _nanoUsd;
    private int _unpriced;

    /// <summary>Wraps a client in a spend ceiling.</summary>
    public BudgetedLlmClient(ILlmClient inner, LlmBudget budget, ModelPriceBook prices)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(prices);

        _inner = inner;
        _budget = budget.Validated();
        _prices = prices;
    }

    /// <summary>What has been spent so far.</summary>
    public LlmSpend Spend
    {
        get
        {
            lock (_gate)
            {
                return new LlmSpend(_calls, _usage, _nanoUsd, _unpriced);
            }
        }
    }

    /// <inheritdoc />
    public string Description
    {
        get
        {
            string ceiling = _budget.MaxUsd is { } dollars
                ? string.Create(CultureInfo.InvariantCulture, $" / ${dollars:F2}")
                : string.Empty;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{_inner.Description}, capped at {_budget.MaxCalls} calls / {_budget.MaxTokens:N0} tokens{ceiling}");
        }
    }

    /// <summary>What a call would cost at worst, in billionths of a dollar.</summary>
    /// <remarks>Null when the model is unpriced, which no dollar ceiling can bound.</remarks>
    public long? WorstCaseNanoUsd(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _prices.NanoUsdFor(request.Model, WorstCaseUsage(request));
    }

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Refuse(request);

        LlmResponse response = await _inner
            .CompleteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // Charged against the model the provider actually used, not the one that was asked
        // for: an alias resolves to a dated snapshot, and it is the snapshot that is billed.
        long? cost = _prices.NanoUsdFor(response.Model, response.Usage);

        lock (_gate)
        {
            _calls++;
            _usage += response.Usage;

            if (cost is { } nanos)
            {
                _nanoUsd += nanos;
            }
            else
            {
                _unpriced++;
            }
        }

        return response;
    }

    private void Refuse(LlmRequest request)
    {
        LlmUsage worstCase = WorstCaseUsage(request);
        long? worstCost = _prices.NanoUsdFor(request.Model, worstCase);

        lock (_gate)
        {
            if (_calls >= _budget.MaxCalls)
            {
                string made = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Model call budget spent: {_calls} of {_budget.MaxCalls} calls made.");

                throw new LlmBudgetExceededException(
                    made + " The run is looping, or the workflow asks for more stages than "
                    + "the budget allows.");
            }

            long projectedTokens = (long)_usage.TotalTokens + worstCase.TotalTokens;

            if (projectedTokens > _budget.MaxTokens)
            {
                throw new LlmBudgetExceededException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Token budget would be exceeded: {_usage.TotalTokens:N0} used, this call needs up to {worstCase.TotalTokens:N0} more, ceiling is {_budget.MaxTokens:N0}."));
            }

            if (_budget.MaxUsd is { } ceiling && worstCost is { } projectedCost)
            {
                decimal projected = (_nanoUsd + projectedCost) / 1_000_000_000m;

                if (projected > ceiling)
                {
                    decimal spent = _nanoUsd / 1_000_000_000m;
                    decimal atMost = projectedCost / 1_000_000_000m;

                    throw new LlmBudgetExceededException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Spend ceiling would be exceeded: ${spent:F4} spent, this call could cost up to ${atMost:F4}, ceiling is ${ceiling:F2}."));
                }
            }
        }
    }

    /// <summary>
    /// The most a call could consume: its input as written, and its full output ceiling.
    /// </summary>
    /// <remarks>
    /// Input is estimated at four characters to the token, the published rule of thumb. It
    /// is approximate, and it does not need to be better: it bounds a ceiling, and the exact
    /// figure the provider reports is what gets charged and audited.
    /// </remarks>
    private static LlmUsage WorstCaseUsage(LlmRequest request) =>
        new(
            InputTokens:
                (request.System.Length / 4)
                + request.Messages.Sum(message => message.Text.Length / 4),
            OutputTokens: request.MaxOutputTokens,
            CacheReadTokens: 0,
            CacheWriteTokens: 0);
}

/// <summary>A run tried to spend more on models than it was allowed.</summary>
public sealed class LlmBudgetExceededException : LlmException
{
    /// <summary>Creates the exception.</summary>
    public LlmBudgetExceededException()
        : base("The run's model budget is spent.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public LlmBudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LlmBudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
