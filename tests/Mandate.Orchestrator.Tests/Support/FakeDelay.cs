using System.Collections.Immutable;
using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Tests.Support;

/// <summary>
/// Records what the engine asked to wait for, and waits for none of it.
/// </summary>
/// <remarks>
/// A test that actually sat out an exponential backoff would be slow enough that nobody would
/// run it — and a backoff nobody runs is a backoff nobody has checked is bounded. Recording
/// the requested durations tests the policy rather than the sleeping.
/// </remarks>
internal sealed class FakeDelay : IDelay
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _waits = [];

    public ImmutableArray<TimeSpan> Waits
    {
        get
        {
            lock (_gate)
            {
                return [.. _waits];
            }
        }
    }

    public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _waits.Add(duration);
        }

        return Task.CompletedTask;
    }
}

/// <summary>A stop monitor a test can switch on, optionally after a number of checks.</summary>
internal sealed class StubSafeStop(int requestAfterChecks = 0) : ISafeStopMonitor
{
    private int _checks;

    public int Checks => _checks;

    public Task<bool> IsStopRequestedAsync(
        Core.Identifiers.RunId runId, CancellationToken cancellationToken) =>
        Task.FromResult(Interlocked.Increment(ref _checks) > requestAfterChecks);
}

/// <summary>A stop monitor that never asks anything to stop.</summary>
internal sealed class NoStop : ISafeStopMonitor
{
    public Task<bool> IsStopRequestedAsync(
        Core.Identifiers.RunId runId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
