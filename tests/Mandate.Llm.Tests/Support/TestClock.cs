using Mandate.Core.Time;

namespace Mandate.Llm.Tests.Support;

/// <summary>A clock the test controls, so recorded durations are exact rather than incidental.</summary>
internal sealed class TestClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public static DateTimeOffset DefaultStart { get; } =
        new(2026, 9, 16, 14, 25, 0, TimeSpan.Zero);

    public TestClock()
        : this(DefaultStart)
    {
    }

    public DateTimeOffset UtcNow => _now;

    /// <summary>Moves the clock forward and returns the new instant.</summary>
    public DateTimeOffset Advance(TimeSpan amount)
    {
        _now = _now.Add(amount);
        return _now;
    }
}
