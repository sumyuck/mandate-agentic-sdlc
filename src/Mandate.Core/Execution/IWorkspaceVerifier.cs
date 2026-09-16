namespace Mandate.Core.Execution;

/// <summary>Which check to run over a tree.</summary>
public enum VerificationKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Compile it.</summary>
    Build = 1,

    /// <summary>Compile it and run its tests, collecting coverage where possible.</summary>
    Test = 2,
}

/// <summary>What actually happened when the toolchain was run over a tree.</summary>
/// <param name="Executed">
/// Whether the check ran at all. Distinct from failing: "the compiler said no" and "there
/// was no compiler" are different facts, and conflating them reports a broken build when
/// the real problem is a broken machine.
/// </param>
/// <param name="Succeeded">Whether it passed.</param>
/// <param name="Summary">One line, for a stage's failure message and the run timeline.</param>
/// <param name="Output">The toolchain's own output, kept as evidence.</param>
/// <param name="TestsPassed">Tests that passed, when a test run produced a count.</param>
/// <param name="TestsFailed">Tests that failed, when a test run produced a count.</param>
/// <param name="LineCoverage">Measured line coverage from 0 to 1, when it was collected.</param>
/// <param name="DurationMilliseconds">How long the check took.</param>
public sealed record VerificationOutcome(
    bool Executed,
    bool Succeeded,
    string Summary,
    string Output,
    int? TestsPassed,
    int? TestsFailed,
    double? LineCoverage,
    long DurationMilliseconds)
{
    /// <summary>The check did not run.</summary>
    public static VerificationOutcome NotRun(string reason) =>
        new(false, false, reason, string.Empty, null, null, null, 0);
}

/// <summary>
/// Runs the real toolchain over what a stage proposes, and reports what it found.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a sentence in the lifecycle's own definition: the testing stage
/// "depends on the recorded result of an actual test run, never on an agent's assertion that
/// the code works". A model reporting its own coverage is grading its own homework, and
/// every gate downstream of that number inherits the grade.
/// </para>
/// <para>
/// Verification happens against a <em>copy</em> of the tree with the stage's proposed files
/// applied, before the engine commits anything. That ordering is what lets a stage report a
/// measured fact about a change that does not exist yet, and it keeps ADR-0009 intact: the
/// agent still only proposes, and a change that fails to build never becomes a commit.
/// </para>
/// <para>
/// A port rather than a direct shell-out, because the engine's own tests must not need a
/// .NET SDK, a warm package cache, or forty seconds per case.
/// </para>
/// </remarks>
public interface IWorkspaceVerifier
{
    /// <summary>How this verifier should be described in a run's evidence.</summary>
    string Description { get; }

    /// <summary>Whether this verifier can actually run anything.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Runs a check over the tree as it would be with the proposed files applied.
    /// </summary>
    /// <param name="tree">The workspace as it stands.</param>
    /// <param name="proposed">The files the stage wants to write, overlaid on the tree.</param>
    /// <param name="kind">Which check to run.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<VerificationOutcome> VerifyAsync(
        IWorkspaceReader tree,
        IReadOnlyCollection<WorkspaceFile> proposed,
        VerificationKind kind,
        CancellationToken cancellationToken);

    /// <summary>A verifier that runs nothing and says so.</summary>
    public static IWorkspaceVerifier Disabled { get; } = new DisabledWorkspaceVerifier();

    private sealed class DisabledWorkspaceVerifier : IWorkspaceVerifier
    {
        public string Description => "disabled — build and test results are unverified claims";

        public bool IsAvailable => false;

        public Task<VerificationOutcome> VerifyAsync(
            IWorkspaceReader tree,
            IReadOnlyCollection<WorkspaceFile> proposed,
            VerificationKind kind,
            CancellationToken cancellationToken) =>
            Task.FromResult(VerificationOutcome.NotRun(
                "No verifier is configured, so build and test results are the agent's own "
                + "assertion rather than a measurement."));
    }
}
