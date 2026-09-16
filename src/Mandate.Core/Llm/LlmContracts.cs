using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mandate.Core.Identifiers;
using Mandate.Core.Serialization;

namespace Mandate.Core.Llm;

/// <summary>
/// How much reasoning a stage is allowed to do before answering.
/// </summary>
/// <remarks>
/// A governed property, not a tuning knob. On models that reason adaptively the thinking
/// counts against the same output ceiling as the answer, so a stage given a hard problem
/// and a modest ceiling can spend the entire budget thinking and return nothing at all —
/// which is exactly what the first live implementation run did: 48,000 output tokens, zero
/// characters of answer. Declaring effort per prompt makes that a property of the stage's
/// specification, reviewable in the same diff as the instructions it goes with.
/// </remarks>
public enum LlmEffort
{
    /// <summary>Not stated. The provider's default applies.</summary>
    Unspecified = 0,

    /// <summary>Do not reason before answering.</summary>
    None = 1,

    /// <summary>Minimal reasoning.</summary>
    Low = 2,

    /// <summary>Moderate reasoning.</summary>
    Medium = 3,

    /// <summary>The provider's usual default.</summary>
    High = 4,

    /// <summary>More than usual, for the hardest stages.</summary>
    Xhigh = 5,

    /// <summary>As much as the model will do.</summary>
    Max = 6,
}

/// <summary>Who authored a turn in a model conversation.</summary>
public enum LlmRole
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>The caller.</summary>
    User = 1,

    /// <summary>The model. Present when a stage replays or extends a prior exchange.</summary>
    Assistant = 2,
}

/// <summary>One turn of a model conversation.</summary>
/// <param name="Role">Who is speaking.</param>
/// <param name="Text">What was said.</param>
public sealed record LlmMessage(LlmRole Role, string Text)
{
    /// <summary>A validated user turn.</summary>
    public static LlmMessage User(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new LlmMessage(LlmRole.User, text);
    }

    /// <summary>A validated assistant turn.</summary>
    public static LlmMessage Assistant(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new LlmMessage(LlmRole.Assistant, text);
    }
}

/// <summary>
/// One call to a language model, in the only form the orchestrator knows about.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately smaller than any vendor's API. The engine needs a system prompt, a
/// conversation and an output ceiling; everything else a provider offers — streaming,
/// tools, sampling parameters, thinking configuration — is the adapter's business. Keeping
/// the port this narrow is what lets a run be replayed from a recording, and what keeps
/// <c>Mandate.Core</c> free of any vendor dependency (ADR-0003).
/// </para>
/// <para>
/// There is no temperature or seed here on purpose. Sampling controls do not make a model
/// reproducible, and offering them would imply a determinism the system cannot deliver.
/// Determinism comes from the cassette (ADR-0007), which is an honest mechanism: the exact
/// bytes a model returned, stored and replayed.
/// </para>
/// </remarks>
/// <param name="PromptId">Which prompt in the library produced this call.</param>
/// <param name="PromptVersion">Which version of that prompt.</param>
/// <param name="Model">The model id to call, as the workflow node declares it.</param>
/// <param name="System">The system prompt.</param>
/// <param name="Messages">The conversation, oldest first.</param>
/// <param name="MaxOutputTokens">The output ceiling for this call.</param>
/// <param name="Effort">How much reasoning the stage is allowed before answering.</param>
public sealed record LlmRequest(
    string PromptId,
    string PromptVersion,
    string Model,
    string System,
    ImmutableArray<LlmMessage> Messages,
    int MaxOutputTokens,
    LlmEffort Effort)
{
    /// <summary>Builds a validated request.</summary>
    public static LlmRequest Create(
        string promptId,
        string promptVersion,
        string model,
        string system,
        IEnumerable<LlmMessage> messages,
        int maxOutputTokens,
        LlmEffort effort = LlmEffort.Unspecified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(promptVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputTokens);

        ImmutableArray<LlmMessage> turns = [.. messages];

        if (turns.IsEmpty)
        {
            throw new ArgumentException("A request needs at least one message.", nameof(messages));
        }

        if (turns[0].Role != LlmRole.User)
        {
            throw new ArgumentException(
                "A conversation must open with a user turn.", nameof(messages));
        }

        return new LlmRequest(
            promptId, promptVersion, model, system, turns, maxOutputTokens, effort);
    }

    /// <summary>
    /// The content-addressed identity of this call.
    /// </summary>
    /// <remarks>
    /// This is the cassette key. Two calls with the same fingerprint are the same question,
    /// so the recorded answer to one is a legitimate answer to the other — and a call whose
    /// prompt, model or output ceiling changed gets a different fingerprint and therefore
    /// misses the cassette rather than silently reusing a stale answer.
    /// </remarks>
    /// <remarks>
    /// Ignored when serialising, and not merely for tidiness: the fingerprint is computed by
    /// serialising the request, so a serialisable fingerprint would recurse for ever. It is
    /// derived from the stored fields, so nothing is lost by leaving it out.
    /// </remarks>
    [JsonIgnore]
    public Sha256Hash Fingerprint =>
        Sha256Hash.OfUtf8(JsonSerializer.Serialize(this, MandateJson.Canonical));
}

/// <summary>What a call consumed, as the provider reported it.</summary>
/// <remarks>
/// Four counts rather than two because they are billed at four different rates, and a cost
/// figure computed from a total would be wrong by a factor of ten in either direction.
/// </remarks>
/// <param name="InputTokens">Uncached input tokens.</param>
/// <param name="OutputTokens">Tokens generated.</param>
/// <param name="CacheReadTokens">Input tokens served from a prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to a prompt cache.</param>
public readonly record struct LlmUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens)
{
    /// <summary>Nothing consumed.</summary>
    public static LlmUsage None => default;

    /// <summary>Every token this call touched, regardless of rate.</summary>
    [JsonIgnore]
    public int TotalTokens =>
        InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>Adds two usages, for rolling a run up.</summary>
    public static LlmUsage operator +(LlmUsage left, LlmUsage right) =>
        new(
            left.InputTokens + right.InputTokens,
            left.OutputTokens + right.OutputTokens,
            left.CacheReadTokens + right.CacheReadTokens,
            left.CacheWriteTokens + right.CacheWriteTokens);

    /// <summary>Adds two usages.</summary>
    public static LlmUsage Add(LlmUsage left, LlmUsage right) => left + right;
}

/// <summary>What a model returned.</summary>
/// <param name="Text">The concatenated text the model produced.</param>
/// <param name="Model">
/// The model that actually answered, as the provider reported it. Not necessarily the id
/// that was requested — an alias resolves to a dated snapshot — and it is the resolved id
/// that belongs in the audit log.
/// </param>
/// <param name="StopReason">Why generation ended.</param>
/// <param name="Usage">What the call consumed.</param>
/// <param name="Source">Where the answer came from.</param>
public sealed record LlmResponse(
    string Text,
    string Model,
    string StopReason,
    LlmUsage Usage,
    LlmResponseSource Source)
{
    /// <summary>
    /// Whether the model produced no answer at all.
    /// </summary>
    /// <remarks>
    /// Distinct from truncation, and the distinction is not academic. A model that reasons
    /// adaptively spends thinking tokens from the same ceiling as its answer, so on a hard
    /// task it can exhaust the budget before writing a word. "The answer was cut short" and
    /// "there was never an answer" call for different fixes — more room versus less
    /// reasoning — and reporting the second as the first sends whoever reads it the wrong way.
    /// </remarks>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// Whether the model ran out of room before it finished.
    /// </summary>
    /// <remarks>
    /// A truncated answer is a failed stage, not a short one. Parsing it would produce a
    /// plausible-looking artifact built from half a thought, which is exactly the kind of
    /// silent degradation the audit log cannot detect after the fact.
    /// </remarks>
    [JsonIgnore]
    public bool WasTruncated =>
        string.Equals(StopReason, "max_tokens", StringComparison.Ordinal);
}

/// <summary>Where a response came from, recorded so evidence can be read at face value.</summary>
public enum LlmResponseSource
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>A live provider call.</summary>
    Live = 1,

    /// <summary>A recorded call, replayed from a cassette.</summary>
    Replay = 2,

    /// <summary>A synthetic answer from the offline stub. Never a real model's judgment.</summary>
    Stub = 3,
}
