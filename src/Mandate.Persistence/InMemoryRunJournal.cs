using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;

namespace Mandate.Persistence;

/// <summary>
/// A volatile journal, for tests and for runs whose evidence is not being kept.
/// </summary>
/// <remarks>
/// Implements the same contract as the durable store, including serialising appends so the
/// hash chain cannot be broken by concurrently executing nodes. Useful precisely because it
/// is interchangeable: the engine cannot behave differently against a real store.
/// </remarks>
public sealed class InMemoryRunJournal : IRunJournal
{
    private readonly Lock _gate = new();
    private readonly List<RunEvent> _events = [];

    /// <summary>Every event appended so far, in order.</summary>
    public ImmutableArray<RunEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <inheritdoc />
    public Task<RunEvent> AppendAsync(
        RunId runId, Func<RunEvent?, RunEvent> build, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            RunEvent? tail = null;

            for (int index = _events.Count - 1; index >= 0; index--)
            {
                if (_events[index].RunId == runId)
                {
                    tail = _events[index];
                    break;
                }
            }

            RunEvent appended = build(tail);
            _events.Add(appended);
            return Task.FromResult(appended);
        }
    }

    /// <inheritdoc />
    public Task<ImmutableArray<RunEvent>> ReadAsync(
        RunId runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult<ImmutableArray<RunEvent>>(
                [.. _events.Where(@event => @event.RunId == runId).OrderBy(@event => @event.Sequence)]);
        }
    }
}
