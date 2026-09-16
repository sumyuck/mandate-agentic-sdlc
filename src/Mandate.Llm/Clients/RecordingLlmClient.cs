using Mandate.Core.Llm;
using Mandate.Core.Time;
using Mandate.Llm.Cassettes;

namespace Mandate.Llm.Clients;

/// <summary>
/// Calls a model and keeps what it said.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a mode inside the live client, so recording is a composition
/// choice made once at the composition root. The live client has no idea it is being
/// recorded, which is the only way to be sure the recorded exchange is the one that
/// actually happened.
/// </para>
/// <para>
/// An already-recorded call is served from the recording rather than re-asked. Re-recording
/// costs money and, worse, produces a second valid answer to the same question — so a
/// re-record would rewrite cassettes that nothing had invalidated and every scenario's
/// evidence would churn. Pass <c>refresh: true</c> to deliberately re-ask.
/// </para>
/// </remarks>
public sealed class RecordingLlmClient(
    ILlmClient inner,
    ICassetteStore cassettes,
    IClock clock,
    bool refresh = false) : ILlmClient
{
    private readonly ILlmClient _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    private readonly ICassetteStore _cassettes =
        cassettes ?? throw new ArgumentNullException(nameof(cassettes));

    private readonly IClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>How many calls this client actually sent to the provider.</summary>
    public int Recorded { get; private set; }

    /// <summary>How many calls it served from an existing recording instead.</summary>
    public int Reused { get; private set; }

    /// <inheritdoc />
    public string Description =>
        $"record ({_inner.Description}) into {_cassettes.Description}"
        + (refresh ? ", refreshing existing recordings" : string.Empty);

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!refresh)
        {
            Cassette? existing = await _cassettes
                .FindAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                Reused++;
                return existing.Replay();
            }
        }

        LlmResponse response = await _inner
            .CompleteAsync(request, cancellationToken)
            .ConfigureAwait(false);

        // Written before the response is handed back, so a stage that then throws cannot
        // lose a recording the account has already been billed for.
        await _cassettes
            .SaveAsync(Cassette.Of(request, response, _clock.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        Recorded++;
        return response;
    }
}
