using System.Collections.Immutable;
using Helmsman.Core.Identifiers;

namespace Helmsman.Core.Events;

/// <summary>Why a chain failed verification.</summary>
public enum AuditBreakReason
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>The stream does not begin at sequence 1.</summary>
    MissingOrigin = 1,

    /// <summary>A sequence number was skipped — an event was deleted or never persisted.</summary>
    SequenceGap = 2,

    /// <summary>Two events share a sequence number.</summary>
    DuplicateSequence = 3,

    /// <summary>The first event does not link to the genesis digest.</summary>
    BadGenesisLink = 4,

    /// <summary>An event's predecessor link does not match the preceding event's digest.</summary>
    BrokenLink = 5,

    /// <summary>An event's recorded digest does not match its contents — the event was edited.</summary>
    ContentAltered = 6,

    /// <summary>The stream mixes events from more than one run.</summary>
    ForeignRun = 7,

    /// <summary>An event is timestamped before its predecessor.</summary>
    TimestampRegression = 8,
}

/// <summary>A single detected defect in a chain.</summary>
/// <param name="Sequence">The sequence number at which the defect was detected.</param>
/// <param name="Reason">The category of defect.</param>
/// <param name="Detail">A reviewer-facing explanation, including expected and actual values.</param>
public sealed record AuditChainBreak(long Sequence, AuditBreakReason Reason, string Detail);

/// <summary>The outcome of verifying a run's audit log.</summary>
/// <param name="RunId">The run that was verified.</param>
/// <param name="EventCount">How many events were examined.</param>
/// <param name="Breaks">Defects found, in the order detected. Empty means the chain is intact.</param>
public sealed record AuditVerification(
    RunId RunId,
    int EventCount,
    ImmutableArray<AuditChainBreak> Breaks)
{
    /// <summary>True when no defect was found.</summary>
    public bool IsIntact => Breaks.IsEmpty;

    /// <summary>A one-line summary for CLI output.</summary>
    public string Summary => IsIntact
        ? $"{RunId}: chain intact across {EventCount} event(s)."
        : $"{RunId}: chain BROKEN — {Breaks.Length} defect(s) across {EventCount} event(s); "
          + $"first at sequence {Breaks[0].Sequence} ({Breaks[0].Reason}).";
}

/// <summary>
/// Verifies that a run's audit log is complete, ordered and unedited.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between "the log says the approval happened" and "the log provably
/// has not been altered since the approval happened". For a change-control audit in a
/// regulated environment that distinction is the whole point of keeping the log.
/// </para>
/// <para>
/// Verification reports <em>all</em> defects rather than stopping at the first, because an
/// auditor needs the shape of the damage, not just its earliest symptom. It is intentionally
/// pure: it takes a sequence of events and returns findings, with no storage dependency, so
/// it can be run against a live store, an exported file, or a hand-built stream in a test.
/// </para>
/// </remarks>
public static class AuditChain
{
    /// <summary>Verifies a run's event stream, ordered by sequence.</summary>
    public static AuditVerification Verify(RunId runId, IReadOnlyList<RunEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        ImmutableArray<AuditChainBreak>.Builder breaks = ImmutableArray.CreateBuilder<AuditChainBreak>();

        if (events.Count == 0)
        {
            return new AuditVerification(runId, 0, breaks.ToImmutable());
        }

        RunEvent? previous = null;

        for (int index = 0; index < events.Count; index++)
        {
            RunEvent current = events[index];

            VerifyRunMembership(runId, current, breaks);
            VerifySequence(index, current, previous, breaks);
            VerifyLink(index, current, previous, breaks);
            VerifyContent(current, breaks);
            VerifyChronology(current, previous, breaks);

            previous = current;
        }

        return new AuditVerification(runId, events.Count, breaks.ToImmutable());
    }

    private static void VerifyRunMembership(
        RunId runId, RunEvent current, ImmutableArray<AuditChainBreak>.Builder breaks)
    {
        if (current.RunId != runId)
        {
            breaks.Add(new AuditChainBreak(
                current.Sequence,
                AuditBreakReason.ForeignRun,
                $"Event belongs to {current.RunId}, but the stream is for {runId}."));
        }
    }

    private static void VerifySequence(
        int index, RunEvent current, RunEvent? previous, ImmutableArray<AuditChainBreak>.Builder breaks)
    {
        if (index == 0)
        {
            if (current.Sequence != 1)
            {
                breaks.Add(new AuditChainBreak(
                    current.Sequence,
                    AuditBreakReason.MissingOrigin,
                    $"The stream begins at sequence {current.Sequence}; the first event of a run is 1. "
                    + "Events before this point are missing."));
            }

            return;
        }

        long expected = previous!.Sequence + 1;

        if (current.Sequence == previous.Sequence)
        {
            breaks.Add(new AuditChainBreak(
                current.Sequence,
                AuditBreakReason.DuplicateSequence,
                $"Sequence {current.Sequence} appears more than once."));
        }
        else if (current.Sequence != expected)
        {
            breaks.Add(new AuditChainBreak(
                current.Sequence,
                AuditBreakReason.SequenceGap,
                $"Expected sequence {expected} after {previous.Sequence}, found {current.Sequence}. "
                + $"{current.Sequence - expected} event(s) are missing."));
        }
    }

    private static void VerifyLink(
        int index, RunEvent current, RunEvent? previous, ImmutableArray<AuditChainBreak>.Builder breaks)
    {
        if (index == 0)
        {
            if (current.PreviousHash != Sha256Hash.Genesis)
            {
                breaks.Add(new AuditChainBreak(
                    current.Sequence,
                    AuditBreakReason.BadGenesisLink,
                    $"The first event links to {current.PreviousHash.Abbreviated} rather than the "
                    + "genesis digest, so it is not the true start of this run's log."));
            }

            return;
        }

        if (current.PreviousHash != previous!.Hash)
        {
            breaks.Add(new AuditChainBreak(
                current.Sequence,
                AuditBreakReason.BrokenLink,
                $"Links to {current.PreviousHash.Abbreviated}, but event {previous.Sequence} "
                + $"hashes to {previous.Hash.Abbreviated}."));
        }
    }

    private static void VerifyContent(
        RunEvent current, ImmutableArray<AuditChainBreak>.Builder breaks)
    {
        Sha256Hash recomputed = current.RecomputeHash();

        if (recomputed != current.Hash)
        {
            breaks.Add(new AuditChainBreak(
                current.Sequence,
                AuditBreakReason.ContentAltered,
                $"Recorded digest {current.Hash.Abbreviated} does not match the contents, which "
                + $"hash to {recomputed.Abbreviated}. This event was modified after it was written."));
        }
    }

    private static void VerifyChronology(
        RunEvent current, RunEvent? previous, ImmutableArray<AuditChainBreak>.Builder breaks)
    {
        if (previous is not null && current.OccurredAt < previous.OccurredAt)
        {
            breaks.Add(new AuditChainBreak(
                current.Sequence,
                AuditBreakReason.TimestampRegression,
                $"Timestamped {current.OccurredAt:O}, before event {previous.Sequence} at "
                + $"{previous.OccurredAt:O}. Latency and MTTR derived from this stream would be wrong."));
        }
    }
}
