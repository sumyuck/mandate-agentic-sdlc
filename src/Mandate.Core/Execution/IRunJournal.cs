using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Execution;

/// <summary>
/// The append-only log a run is recorded into.
/// </summary>
/// <remarks>
/// <para>
/// The engine writes here and nowhere else. It holds no mutable run state of its own: the
/// current state of a run is a projection over this log, which is what makes resume, replay,
/// lineage and the reliability metrics one mechanism rather than four.
/// </para>
/// <para>
/// The port takes a <em>factory</em> rather than a finished event, because the hash chain
/// requires each event to commit to its immediate predecessor. Only the journal knows what
/// that predecessor is, and letting a caller supply it would make it possible to append an
/// event linked to the wrong place in the chain.
/// </para>
/// </remarks>
public interface IRunJournal
{
    /// <summary>
    /// Appends an event to a run, linking it to the current tail of that run's chain.
    /// </summary>
    /// <param name="runId">
    /// The run to append to. Passed explicitly because a store holds many runs and each has
    /// its own chain: linking an event to the tail of some other run would produce a log that
    /// verifies against nothing.
    /// </param>
    /// <param name="build">
    /// Builds the event, given that run's current tail. Called while the journal holds its
    /// write lock, so appends from concurrently executing nodes cannot interleave and break
    /// the chain.
    /// </param>
    /// <returns>The appended event, including its assigned sequence number and digest.</returns>
    Task<RunEvent> AppendAsync(
        RunId runId, Func<RunEvent?, RunEvent> build, CancellationToken cancellationToken);

    /// <summary>Reads a run's events in sequence order.</summary>
    Task<ImmutableArray<RunEvent>> ReadAsync(RunId runId, CancellationToken cancellationToken);
}
