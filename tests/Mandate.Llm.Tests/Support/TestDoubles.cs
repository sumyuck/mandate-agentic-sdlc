using Mandate.Core.Llm;

namespace Mandate.Llm.Tests.Support;

/// <summary>A client that answers from a script and counts what it was asked.</summary>
internal sealed class FakeLlmClient(Func<LlmRequest, LlmResponse>? responder = null) : ILlmClient
{
    private readonly Func<LlmRequest, LlmResponse> _responder = responder ?? Canned;

    public int Calls { get; private set; }

    public List<LlmRequest> Requests { get; } = [];

    public string Description => "fake";

    public Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        Calls++;
        Requests.Add(request);
        return Task.FromResult(_responder(request));
    }

    /// <summary>A live-looking answer with the usage a caller asked for.</summary>
    public static LlmResponse Answer(
        string text = "answered",
        string? model = null,
        int inputTokens = 100,
        int outputTokens = 50,
        string stopReason = "end_turn") =>
        new(
            text,
            model ?? "claude-haiku-4-5",
            stopReason,
            new LlmUsage(inputTokens, outputTokens, 0, 0),
            LlmResponseSource.Live);

    private static LlmResponse Canned(LlmRequest request) => Answer($"answer to {request.PromptId}");
}

/// <summary>A client that always throws, for proving something never reached the provider.</summary>
internal sealed class ExplodingLlmClient : ILlmClient
{
    public string Description => "exploding";

    public Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The provider should not have been called.");
}

/// <summary>Builds requests without repeating the boilerplate in every test.</summary>
internal static class Requests
{
    public static LlmRequest A(
        string promptId = "requirements-analyst",
        string version = "v1",
        string model = "claude-haiku-4-5",
        string system = "You analyse requirements.",
        string user = "Shorten URLs.",
        int maxOutputTokens = 1000) =>
        LlmRequest.Create(
            promptId, version, model, system, [LlmMessage.User(user)], maxOutputTokens);
}
