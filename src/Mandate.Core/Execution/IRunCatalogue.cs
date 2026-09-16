using System.Collections.Immutable;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;

namespace Mandate.Core.Execution;

/// <summary>A run, as it appears in a listing.</summary>
/// <param name="RunId">The run's identifier.</param>
/// <param name="Workflow">Name and version of the workflow it executed.</param>
/// <param name="Scenario">The kind of problem it was solving.</param>
/// <param name="Request">The requirement, as the requester wrote it.</param>
/// <param name="Status">Where it ended up, or where it currently stands.</param>
/// <param name="StartedAt">When the run was planned.</param>
/// <param name="UpdatedAt">When its most recent event was recorded.</param>
/// <param name="EventCount">How many events it has produced.</param>
public sealed record RunSummary(
    RunId RunId,
    string Workflow,
    string Scenario,
    string Request,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    long EventCount)
{
    /// <summary>True when the run is waiting on a human rather than on the engine.</summary>
    public bool IsWaitingOnHuman => Status is RunStatus.AwaitingApproval or RunStatus.Blocked;
}

/// <summary>
/// Lists and locates persisted runs.
/// </summary>
/// <remarks>
/// Separate from <see cref="IRunJournal"/> because they answer different questions: the
/// journal is how a run is written and read in full, the catalogue is how a human finds one.
/// Keeping the engine's port to just the journal means the engine cannot enumerate runs it is
/// not executing.
/// </remarks>
public interface IRunCatalogue
{
    /// <summary>Lists runs, most recent first.</summary>
    Task<ImmutableArray<RunSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Summarises one run, or returns <see langword="null"/> when it is not present.</summary>
    Task<RunSummary?> FindAsync(RunId runId, CancellationToken cancellationToken);
}
