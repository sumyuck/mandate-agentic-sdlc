using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Decisions;

/// <summary>One candidate answer that was on the table when a decision was made.</summary>
/// <param name="Name">Short label for the option.</param>
/// <param name="Summary">What choosing it would have meant.</param>
/// <param name="RejectedBecause">
/// Why it was not chosen. <see langword="null"/> for the chosen option.
/// </param>
public sealed record DecisionOption(string Name, string Summary, string? RejectedBecause)
{
    /// <summary>True when this is the option that was taken.</summary>
    public bool WasChosen => RejectedBecause is null;
}

/// <summary>Who made a decision.</summary>
public enum DecisionAuthority
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>An agent decided within its autonomy boundary.</summary>
    Agent = 1,

    /// <summary>A human decided, through an approval or a clarification.</summary>
    Human = 2,

    /// <summary>A policy rule forced the outcome.</summary>
    Policy = 3,
}

/// <summary>
/// A choice made during a run, recorded with the options rejected and the reasoning.
/// </summary>
/// <remarks>
/// <para>
/// The constructor refuses to build a decision that names only one option, or that rejects an
/// option without saying why, or that has no rationale. That is deliberate: "clarity and
/// defensibility of decisions" cannot be retrofitted, so the type makes an indefensible
/// record impossible to create rather than merely discouraged.
/// </para>
/// <para>
/// Architecture decision records are generated from these, which is why rationale is captured
/// at the moment of choosing rather than reconstructed from memory when documentation is due.
/// </para>
/// </remarks>
public sealed record Decision
{
    private Decision(
        string id,
        RunId runId,
        NodeId nodeId,
        string question,
        ImmutableArray<DecisionOption> options,
        string rationale,
        double confidence,
        DecisionAuthority authority,
        Actor decidedBy,
        DateTimeOffset decidedAt,
        ImmutableArray<Sha256Hash> evidence)
    {
        Id = id;
        RunId = runId;
        NodeId = nodeId;
        Question = question;
        Options = options;
        Rationale = rationale;
        Confidence = confidence;
        Authority = authority;
        DecidedBy = decidedBy;
        DecidedAt = decidedAt;
        Evidence = evidence;
    }

    /// <summary>Stable identifier, unique within the run.</summary>
    public string Id { get; }

    /// <summary>The run in which the decision was made.</summary>
    public RunId RunId { get; }

    /// <summary>The node at which it was made.</summary>
    public NodeId NodeId { get; }

    /// <summary>What was being decided.</summary>
    public string Question { get; }

    /// <summary>Every option considered, including the one taken.</summary>
    public ImmutableArray<DecisionOption> Options { get; }

    /// <summary>Why the chosen option was chosen.</summary>
    public string Rationale { get; }

    /// <summary>How confident the decider was, from 0 to 1.</summary>
    public double Confidence { get; }

    /// <summary>Whether an agent, a human or a policy determined the outcome.</summary>
    public DecisionAuthority Authority { get; }

    /// <summary>The specific participant who decided.</summary>
    public Actor DecidedBy { get; }

    /// <summary>When the decision was made.</summary>
    public DateTimeOffset DecidedAt { get; }

    /// <summary>Artifacts the decision was based on.</summary>
    public ImmutableArray<Sha256Hash> Evidence { get; }

    /// <summary>The option that was taken.</summary>
    public DecisionOption Chosen => Options.First(option => option.WasChosen);

    /// <summary>The options that were not taken.</summary>
    public IEnumerable<DecisionOption> Rejected => Options.Where(option => !option.WasChosen);

    /// <summary>True when the decider's confidence was below the given threshold.</summary>
    public bool IsLowConfidence(double threshold = 0.6) => Confidence < threshold;

    /// <summary>Records a decision, rejecting any record that would not be defensible.</summary>
    /// <exception cref="ArgumentException">The record would be incomplete or self-contradictory.</exception>
    public static Decision Record(
        string id,
        RunId runId,
        NodeId nodeId,
        string question,
        IEnumerable<DecisionOption> options,
        string rationale,
        double confidence,
        DecisionAuthority authority,
        Actor decidedBy,
        DateTimeOffset decidedAt,
        IEnumerable<Sha256Hash>? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(rationale);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(confidence, 0d);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(confidence, 1d);

        if (authority == DecisionAuthority.Unknown)
        {
            throw new ArgumentException(
                "A decision must record who had the authority to make it.", nameof(authority));
        }

        if (decidedBy.IsEmpty)
        {
            throw new ArgumentException(
                "A decision must name its decider.", nameof(decidedBy));
        }

        ImmutableArray<DecisionOption> considered = [.. options];

        if (considered.Length < 2)
        {
            throw new ArgumentException(
                "A decision must record at least two options. A choice with no alternative is "
                + "not a decision, and recording it as one hides the fact that nothing was weighed.",
                nameof(options));
        }

        int chosenCount = considered.Count(option => option.WasChosen);

        if (chosenCount != 1)
        {
            throw new ArgumentException(
                $"Exactly one option must be chosen; {chosenCount} were. An option is chosen "
                + "when it has no rejection reason.",
                nameof(options));
        }

        DecisionOption? unexplained = considered.FirstOrDefault(option =>
            !option.WasChosen && string.IsNullOrWhiteSpace(option.RejectedBecause));

        if (unexplained is not null)
        {
            throw new ArgumentException(
                $"Option '{unexplained.Name}' was rejected without a reason. An unexplained "
                + "rejection cannot be defended in review.",
                nameof(options));
        }

        return new Decision(
            id, runId, nodeId, question, considered, rationale, confidence,
            authority, decidedBy, decidedAt,
            evidence is null ? [] : [.. evidence.Distinct()]);
    }
}
