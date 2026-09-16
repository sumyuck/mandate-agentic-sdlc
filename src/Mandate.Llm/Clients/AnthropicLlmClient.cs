using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Mandate.Core.Llm;

namespace Mandate.Llm.Clients;

/// <summary>
/// The live adapter: the only type in the system that knows a vendor exists.
/// </summary>
/// <remarks>
/// <para>
/// Everything vendor-shaped stops here. The engine, the agents and the tests are written
/// against <see cref="ILlmClient"/>, so swapping providers, or replacing live calls with
/// recordings, changes this file and the composition root and nothing else (ADR-0003).
/// </para>
/// <para>
/// The SDK's own retry of rate limits and 5xx responses is left on. It retries a single
/// call in seconds; the engine's retry re-runs a whole stage and costs another full prompt.
/// Letting the cheap mechanism handle transient faults keeps the expensive one for real
/// ones — and when the SDK does give up, the failure surfaces as a transient
/// <see cref="LlmException"/> so the stage's retry budget still applies.
/// </para>
/// </remarks>
public sealed class AnthropicLlmClient : ILlmClient, IDisposable
{
    /// <summary>The environment variable the key is read from.</summary>
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    private readonly AnthropicClient _client;

    /// <summary>Creates a client from the ambient API key.</summary>
    /// <exception cref="LlmException">No API key is configured.</exception>
    public AnthropicLlmClient()
        : this(Environment.GetEnvironmentVariable(ApiKeyVariable))
    {
    }

    /// <summary>Creates a client with an explicit key.</summary>
    /// <exception cref="LlmException">The key is missing.</exception>
    public AnthropicLlmClient(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Checked here rather than left to a 401 on the first call, because by then a
            // run has been planned, a workspace created and a journal opened — and the
            // operator reads "unauthorized" instead of "you have not set a key".
            throw new LlmException(
                $"No API key. Set {ApiKeyVariable} in the environment, or run with "
                + "--llm replay to use the recorded exchanges instead.");
        }

        _client = new AnthropicClient { ApiKey = apiKey };
    }

    /// <summary>Whether an API key is present in the environment.</summary>
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiKeyVariable));

    /// <inheritdoc />
    public string Description => "live Anthropic API";

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        MessageCreateParams parameters = new()
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            System = request.System,
            Messages =
            [
                .. request.Messages.Select(message => new MessageParam
                {
                    Role = message.Role == LlmRole.Assistant ? Role.Assistant : Role.User,
                    Content = message.Text,
                }),
            ],
        };

        Message message;

        try
        {
            message = await _client.Messages
                .Create(parameters, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AnthropicRateLimitException exception)
        {
            throw new LlmTransportException(
                $"Rate limited calling {request.Model}: {exception.Message}", exception, true);
        }
        catch (Anthropic5xxException exception)
        {
            throw new LlmTransportException(
                $"The provider failed calling {request.Model}: {exception.Message}",
                exception,
                true);
        }
        catch (AnthropicIOException exception)
        {
            throw new LlmTransportException(
                $"Could not reach the provider: {exception.Message}", exception, true);
        }
        catch (AnthropicApiException exception)
        {
            // A 400 or a 401 will fail again identically. Marked non-transient so the
            // stage's retry budget is not spent proving that.
            throw new LlmTransportException(
                $"The provider refused the call to {request.Model}: {exception.Message}",
                exception,
                false);
        }

        return new LlmResponse(
            TextOf(message),
            // Raw(), not ToString(): these are ApiEnum values whose ToString renders the
            // JSON form, quotes and all. A quoted model id matches no price and no cassette
            // key, so the difference is the difference between a costed run and a run whose
            // spend is silently unknown.
            ModelOf(message, request),
            StopReasonOf(message),
            UsageOf(message),
            LlmResponseSource.Live);
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static string ModelOf(Message message, LlmRequest request)
    {
        string? resolved = message.Model.Raw();
        return string.IsNullOrWhiteSpace(resolved) ? request.Model : resolved;
    }

    private static string StopReasonOf(Message message)
    {
        // Null while a streamed message is still in flight, and on some error shapes. A
        // non-committal "unknown" is safer than a default of "end_turn", which would claim
        // the model finished when nobody knows whether it did.
        string? reason = message.StopReason?.Raw();
        return string.IsNullOrWhiteSpace(reason) ? "unknown" : reason;
    }

    private static string TextOf(Message message) =>
        string.Concat(
            message.Content
                .Select(block => block.Value)
                .OfType<TextBlock>()
                .Select(text => text.Text));

    private static LlmUsage UsageOf(Message message) =>
        new(
            (int)message.Usage.InputTokens,
            (int)message.Usage.OutputTokens,
            (int)(message.Usage.CacheReadInputTokens ?? 0),
            (int)(message.Usage.CacheCreationInputTokens ?? 0));
}

/// <summary>A model call failed at the provider.</summary>
/// <remarks>
/// Carries whether another attempt could plausibly help, so the engine's retry budget is
/// spent on rate limits and outages rather than on malformed requests.
/// </remarks>
public sealed class LlmTransportException : LlmException
{
    /// <summary>Creates the exception.</summary>
    public LlmTransportException()
        : base("The provider call failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public LlmTransportException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LlmTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception, stating whether a retry could help.</summary>
    public LlmTransportException(string message, Exception innerException, bool transient)
        : base(message, innerException) => IsTransient = transient;

    /// <inheritdoc />
    public override bool IsTransient { get; }
}
