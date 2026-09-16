namespace Mandate.Core.Llm;

/// <summary>
/// The orchestrator's only route to a language model.
/// </summary>
/// <remarks>
/// <para>
/// One method, no streaming, no tools. Every model-backed agent depends on this and nothing
/// else, which is what makes "run the whole lifecycle offline from recordings" a change of
/// one binding rather than a mode the agents have to implement.
/// </para>
/// <para>
/// Implementations compose: the live client is wrapped by a recorder, the recorder by a
/// budget. Each decorator is one concern, and the run's evidence shows which combination
/// produced it.
/// </para>
/// </remarks>
public interface ILlmClient
{
    /// <summary>How this client should be described in a run's evidence.</summary>
    /// <remarks>
    /// Surfaced rather than inferred because "which model layer produced this run" is a
    /// governance question. A reviewer reading a run must be able to tell a live answer
    /// from a replayed one from a stubbed one without reading the invocation.
    /// </remarks>
    string Description { get; }

    /// <summary>Asks the model, and returns what it said.</summary>
    /// <exception cref="LlmException">
    /// The call could not be completed: the provider refused it, the recording was missing,
    /// or a budget was exhausted.
    /// </exception>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
