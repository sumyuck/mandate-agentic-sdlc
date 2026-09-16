namespace Mandate.Core.Time;

/// <summary>
/// Source of wall-clock time for the engine.
/// </summary>
/// <remarks>
/// Time is injected rather than read from <see cref="DateTimeOffset.UtcNow"/> because it is
/// load-bearing evidence: event ordering, stage latency, MTTR and approval wait times are all
/// derived from recorded timestamps. Tests need those values to be exact, not approximately
/// right, so the clock is a port like any other.
/// </remarks>
public interface IClock
{
    /// <summary>The current instant, in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. Bound in the CLI composition root.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance; the type is stateless.</summary>
    public static SystemClock Instance { get; } = new();

    private SystemClock()
    {
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
