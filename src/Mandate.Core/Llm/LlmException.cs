namespace Mandate.Core.Llm;

/// <summary>
/// A model call could not be completed.
/// </summary>
/// <remarks>
/// One exception type across every adapter so the engine can treat "the model layer failed"
/// as a stage failure — retried, then compensated — without knowing whether the cause was a
/// rate limit, a missing recording or an exhausted budget. The adapters distinguish those
/// by deriving; the engine deliberately does not care.
/// </remarks>
public class LlmException : Exception
{
    /// <summary>Creates the exception.</summary>
    public LlmException()
        : base("The model call could not be completed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public LlmException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public LlmException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Whether trying the same call again could plausibly succeed.
    /// </summary>
    /// <remarks>
    /// A rate limit is worth another attempt; a missing cassette is not, and burning three
    /// attempts on it only delays the message the operator actually needs to read.
    /// </remarks>
    public virtual bool IsTransient => false;
}
