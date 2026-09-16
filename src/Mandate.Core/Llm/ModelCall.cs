using System.Text.Json.Serialization;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Llm;

/// <summary>
/// A single model call, as a stage reports it for the audit log.
/// </summary>
/// <remarks>
/// <para>
/// Recorded per call rather than summarised per stage. A stage that made four calls and a
/// stage that made one are different engineering behaviours, and a per-stage total would
/// hide the difference. It also means a run's cost is reconstructible from its log by the
/// same fold that produces everything else — no separate ledger to keep in step.
/// </para>
/// <para>
/// Cost is carried in nano-dollars as an integer, not as a decimal. Decimal values carry
/// scale, so <c>0.10</c> and <c>0.1</c> serialise differently and would produce different
/// audit hashes for the same amount; an integer cannot.
/// </para>
/// </remarks>
/// <param name="PromptId">Which prompt in the library produced the call.</param>
/// <param name="PromptVersion">Which version of that prompt.</param>
/// <param name="Model">The model that answered, as the provider reported it.</param>
/// <param name="Source">Live, replayed or stubbed.</param>
/// <param name="Fingerprint">The request's content address — the cassette key.</param>
/// <param name="Usage">What the call consumed.</param>
/// <param name="CostNanoUsd">
/// What it cost, in billionths of a dollar, or <see langword="null"/> when no price is
/// published for the model. Null rather than zero: an unpriced call is unknown, not free.
/// </param>
/// <param name="StopReason">Why generation ended.</param>
/// <param name="DurationMilliseconds">How long the call took.</param>
public sealed record ModelCall(
    string PromptId,
    string PromptVersion,
    string Model,
    LlmResponseSource Source,
    Sha256Hash Fingerprint,
    LlmUsage Usage,
    long? CostNanoUsd,
    string StopReason,
    long DurationMilliseconds)
{
    /// <summary>The cost in dollars, for display. Null when the model has no published price.</summary>
    [JsonIgnore]
    public decimal? CostUsd => CostNanoUsd is { } nanos ? nanos / 1_000_000_000m : null;
}
