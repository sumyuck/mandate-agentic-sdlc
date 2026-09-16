using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Execution;

/// <summary>
/// Everything needed to start a run.
/// </summary>
/// <param name="Id">Identifier for the run.</param>
/// <param name="Request">The requirement, as the requester wrote it.</param>
/// <param name="Scenario">Which kind of problem this is.</param>
/// <param name="InitiatedBy">The human who asked for it.</param>
/// <param name="HasExistingCode">
/// Whether the target already contains the code under change. Kept separate from
/// <paramref name="Scenario"/> because they are different questions: an ambiguous request can
/// concern existing code, and the lifecycle routes on the code, not on the ambiguity.
/// </param>
/// <param name="AdditionalContext">Extra context facts to seed the run with.</param>
public sealed record RunRequest(
    RunId Id,
    string Request,
    ScenarioKind Scenario,
    Actor InitiatedBy,
    bool HasExistingCode,
    ImmutableDictionary<string, string> AdditionalContext)
{
    /// <summary>Creates a request with no additional context.</summary>
    public static RunRequest Create(
        RunId id,
        string request,
        ScenarioKind scenario,
        Actor initiatedBy,
        bool hasExistingCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);

        if (scenario == ScenarioKind.Unknown)
        {
            throw new ArgumentException(
                "A run must declare its scenario; the lifecycle routes on it.", nameof(scenario));
        }

        if (initiatedBy.Kind != ActorKind.Human)
        {
            throw new ArgumentException(
                "A run is initiated by a named human. Runs with no accountable requester are "
                + "not auditable.",
                nameof(initiatedBy));
        }

        return new RunRequest(
            id, request, scenario, initiatedBy, hasExistingCode,
            ImmutableDictionary<string, string>.Empty);
    }
}
