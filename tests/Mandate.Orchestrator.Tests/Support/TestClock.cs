using Mandate.Core.Time;

namespace Mandate.Orchestrator.Tests.Support;

/// <summary>
/// A clock that advances a fixed step on every read.
/// </summary>
/// <remarks>
/// Every event gets a distinct, increasing timestamp without depending on wall-clock time, so
/// the audit chain's chronology check is exercised and durations are reproducible.
/// </remarks>
internal sealed class TestClock : IClock
{
    private static readonly DateTimeOffset Start = new(2026, 9, 16, 14, 25, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private long _reads;

    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return Start.AddMilliseconds(_reads++);
            }
        }
    }
}
