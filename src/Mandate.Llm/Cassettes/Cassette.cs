using Mandate.Core.Llm;

namespace Mandate.Llm.Cassettes;

/// <summary>
/// One recorded model exchange: the exact question asked, and the exact answer given.
/// </summary>
/// <remarks>
/// <para>
/// The request is stored in full rather than reduced to its hash. A cassette is evidence a
/// reviewer reads — "what did you actually ask the model?" is the first question anyone
/// sensible asks of an agentic system — and a file containing only a hash answers nothing.
/// The hash is derivable from the request; the request is not derivable from the hash.
/// </para>
/// <para>
/// <c>RecordedAt</c> deliberately takes no part in the key. When the recording was made is
/// provenance, not identity.
/// </para>
/// </remarks>
/// <param name="Key">The request's fingerprint, in hex.</param>
/// <param name="RecordedAt">When the live call was made.</param>
/// <param name="Request">The exact request that was sent.</param>
/// <param name="Response">The exact response that came back.</param>
public sealed record Cassette(
    string Key,
    DateTimeOffset RecordedAt,
    LlmRequest Request,
    CassetteResponse Response)
{
    /// <summary>Builds a cassette from a live exchange.</summary>
    public static Cassette Of(LlmRequest request, LlmResponse response, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        return new Cassette(
            request.Fingerprint.Hex,
            recordedAt,
            request,
            new CassetteResponse(
                response.Text, response.Model, response.StopReason, response.Usage));
    }

    /// <summary>
    /// Replays the recording as a response.
    /// </summary>
    /// <remarks>
    /// The source is rewritten to <see cref="LlmResponseSource.Replay"/> rather than kept as
    /// whatever was recorded. A replayed answer is a replayed answer, and evidence that
    /// called it live would be a lie the audit chain would faithfully preserve.
    /// </remarks>
    public LlmResponse Replay() =>
        new(Response.Text, Response.Model, Response.StopReason, Response.Usage,
            LlmResponseSource.Replay);
}

/// <summary>The recorded half of an exchange.</summary>
/// <param name="Text">What the model said.</param>
/// <param name="Model">The model that answered, as the provider resolved it.</param>
/// <param name="StopReason">Why generation ended.</param>
/// <param name="Usage">What the live call consumed, kept so replays report honest figures.</param>
public sealed record CassetteResponse(
    string Text,
    string Model,
    string StopReason,
    LlmUsage Usage);
