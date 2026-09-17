using Mandate.Core.Llm;
using Mandate.Llm.Cassettes;

namespace Mandate.Llm.Clients;

/// <summary>
/// Answers every call from a recording, and fails when there is none.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a run reproducible by someone who has no API key, no budget and no
/// network — which is the position a reviewer is usually in. The committed cassettes and
/// this client together mean the three scenario runs can be re-executed and re-verified
/// from the repository alone.
/// </para>
/// <para>
/// A miss is a hard failure. It would be easy to fall through to a live call, and that is
/// exactly the behaviour to refuse: the reviewer who set out to reproduce a recorded run
/// would silently get a different one, billed to someone else's account, and the evidence
/// would not say so. Failing loudly turns a wrong answer into a clear instruction.
/// </para>
/// </remarks>
public sealed class ReplayLlmClient(ICassetteStore cassettes) : ILlmClient
{
    private readonly ICassetteStore _cassettes =
        cassettes ?? throw new ArgumentNullException(nameof(cassettes));

    /// <inheritdoc />
    public string Description => $"replay from {_cassettes.Description}";

    /// <inheritdoc />
    public async Task<LlmResponse> CompleteAsync(
        LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Cassette? recorded = await _cassettes
            .FindAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return recorded is not null
            ? recorded.Replay()
            : throw new CassetteMissException(request, _cassettes.Description);
    }
}

/// <summary>Replay was asked for a call that was never recorded.</summary>
public sealed class CassetteMissException : LlmException
{
    /// <summary>Creates the exception.</summary>
    public CassetteMissException()
        : base("No recording matches this call.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public CassetteMissException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public CassetteMissException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific unrecorded call.</summary>
    public CassetteMissException(LlmRequest request, string store)
        : base(Explain(request, store))
    {
        ArgumentNullException.ThrowIfNull(request);
        Fingerprint = request.Fingerprint.Hex;
        PromptIdentity = $"{request.PromptId}.{request.PromptVersion}";
    }

    /// <summary>The fingerprint that was looked for.</summary>
    public string Fingerprint { get; } = string.Empty;

    /// <summary>The prompt whose recording is missing.</summary>
    public string PromptIdentity { get; } = string.Empty;

    private static string Explain(LlmRequest request, string store) =>
        $"No recording for {request.PromptId}.{request.PromptVersion} "
        + $"({request.Fingerprint.Abbreviated}) in {store}. The prompt, the model or the "
        + "upstream output has changed since the cassettes were recorded. Re-record with "
        + "'--llm record', or check out the revision the recordings belong to.";
}
