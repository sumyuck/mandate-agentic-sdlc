using System.Globalization;
using System.Text;
using Mandate.Core.Llm;

namespace Mandate.Llm.Clients;

/// <summary>
/// Answers without a model at all, deterministically.
/// </summary>
/// <remarks>
/// <para>
/// For the engine's own tests, and for anyone who wants to walk the lifecycle without a key
/// or a recording. Every response is a pure function of the request, so a test that depends
/// on one is pinned to the request rather than to a model's mood.
/// </para>
/// <para>
/// Responses are marked <see cref="LlmResponseSource.Stub"/> and cost nothing, and that
/// marking travels all the way into the audit log. A stubbed run is not a cheap run — it is
/// a run in which no engineering judgment was exercised, and its evidence has to say so or
/// it is worse than no evidence at all.
/// </para>
/// </remarks>
public sealed class StubLlmClient(Func<LlmRequest, string>? responder = null) : ILlmClient
{
    private readonly Func<LlmRequest, string> _responder = responder ?? Echo;

    /// <summary>How many calls it has answered.</summary>
    public int Calls { get; private set; }

    /// <inheritdoc />
    public string Description => "offline stub (no model is consulted)";

    /// <inheritdoc />
    public Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Calls++;
        string text = _responder(request);

        return Task.FromResult(new LlmResponse(
            text,
            request.Model,
            "end_turn",
            // Counted, not measured: a stub has no tokenizer. Four characters to the token
            // is the published rule of thumb, and it is enough for the budget machinery to
            // be exercised without pretending to an accuracy nothing here has.
            new LlmUsage(
                InputTokens: Estimate(request.System) + request.Messages.Sum(m => Estimate(m.Text)),
                OutputTokens: Estimate(text),
                CacheReadTokens: 0,
                CacheWriteTokens: 0),
            LlmResponseSource.Stub));
    }

    private static int Estimate(string text) => Math.Max(1, text.Length / 4);

    /// <summary>
    /// The default answer: a readable description of what was asked.
    /// </summary>
    /// <remarks>
    /// Deliberately not plausible prose. A stub that produced convincing-looking
    /// requirements would make a stubbed run indistinguishable from a real one at a glance,
    /// and someone would eventually mistake one for the other.
    /// </remarks>
    private static string Echo(LlmRequest request)
    {
        StringBuilder text = new();

        text.AppendLine(CultureInfo.InvariantCulture, $"STUB RESPONSE: no model was called.");
        text.AppendLine(CultureInfo.InvariantCulture, $"prompt: {request.PromptId}.{request.PromptVersion}");
        text.AppendLine(CultureInfo.InvariantCulture, $"model requested: {request.Model}");
        text.AppendLine(CultureInfo.InvariantCulture, $"request fingerprint: {request.Fingerprint.Hex}");
        text.AppendLine(CultureInfo.InvariantCulture, $"user turn, first 400 characters:");
        text.AppendLine(Truncate(request.Messages[^1].Text, 400));

        return text.ToString();
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";
}
