namespace Mandate.Orchestrator.Execution;

/// <summary>
/// Limits the engine applies to a run.
/// </summary>
/// <param name="MaxConcurrency">
/// How many nodes may execute at once.
/// </param>
/// <remarks>
/// <para>
/// Concurrency is bounded rather than unbounded for a reason beyond tidiness: stage agents
/// call a model, so an unbounded fan-out means unpredictable spend and rate-limit collisions
/// on exactly the runs that fan out widest. The bound is what makes a run's cost and duration
/// predictable enough to put a budget on.
/// </para>
/// </remarks>
public sealed record EngineOptions(int MaxConcurrency = 4)
{
    /// <summary>The default limits.</summary>
    public static EngineOptions Default { get; } = new();

    /// <summary>Executes one node at a time, for deterministic test and replay runs.</summary>
    public static EngineOptions Sequential { get; } = new(MaxConcurrency: 1);

    /// <summary>Validates the options, throwing when they could not be honoured.</summary>
    public EngineOptions Validated()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrency, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConcurrency, 64);
        return this;
    }
}
