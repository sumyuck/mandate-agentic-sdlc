using System.Collections.Immutable;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Policy;

namespace Mandate.Cli;

/// <summary>
/// Builds the policy engine the CLI runs with.
/// </summary>
/// <remarks>
/// The audit-integrity check needs to read the run's log, which policy has no business
/// reaching into itself. The composition root supplies that capability as a function, which
/// keeps the dependency pointing the right way.
/// </remarks>
internal static class PolicyComposition
{
    /// <summary>Where policy packs live by default.</summary>
    public const string DefaultDirectory = "workflows/policies";

    /// <summary>
    /// Loads the packs and builds an engine, or returns <see langword="null"/> with a reason
    /// when the packs cannot be used.
    /// </summary>
    public static PolicyEngine? TryBuild(
        string directory, IRunJournal journal, out string? problem)
    {
        ImmutableArray<PolicyPack> packs;

        try
        {
            packs = PolicyPackLoader.LoadDirectory(directory);
        }
        catch (PolicyFormatException exception)
        {
            problem = exception.Message;
            return null;
        }

        PolicyEngine engine = new(
            packs,
            PolicyCheckRegistry.BuiltIn(async (runId, cancellationToken) =>
                AuditChain.Verify(
                    runId,
                    await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false))));

        ImmutableArray<string> unmet = engine.FindUnmetRequirements();

        if (!unmet.IsEmpty)
        {
            // A pack that cannot be evaluated is worse than no pack: every rule in it would
            // report a violation for want of a check rather than for any real reason.
            problem = string.Join(Environment.NewLine, unmet);
            return null;
        }

        problem = null;
        return engine;
    }
}
