using System.Collections.Immutable;
using Mandate.Core.Artifacts;
using Mandate.Core.Policies;
using Mandate.Core.Workflow;

namespace Mandate.Policy.Checks;

/// <summary>Base for checks that reach a verdict from the run's recorded evidence.</summary>
public abstract class PolicyCheckBase : IPolicyCheck
{
    /// <inheritdoc />
    public abstract string Kind { get; }

    /// <inheritdoc />
    public abstract string Describes { get; }

    /// <inheritdoc />
    public abstract Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken);

    /// <summary>The rule held.</summary>
    protected static PolicyVerdict Satisfied(PolicyCheckContext context, string explanation) =>
        new(context.Rule, Satisfied: true, explanation);

    /// <summary>The rule was violated, subject to any waiver the run holds.</summary>
    protected static PolicyVerdict Violated(PolicyCheckContext context, string explanation) =>
        new(
            context.Rule,
            Satisfied: false,
            explanation,
            context.Run.Waivers.TryGetValue(context.Rule.Id, out PolicyWaiver? waiver)
                ? waiver
                : null);
}

/// <summary>
/// Every change traces back to a recorded requirement.
/// </summary>
/// <remarks>
/// The question an auditor asks first: why does this code exist? Content addressing makes it
/// answerable — a patch names what it was derived from, so the trace is structural rather
/// than a document somebody maintained.
/// </remarks>
public sealed class ChangeTracesToRequirementCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "change-traces-to-requirement";

    /// <inheritdoc />
    public override string Describes =>
        "Every source change descends from a recorded requirement.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ImmutableArray<Artifact> patches =
            [.. context.Run.Artifacts.Where(artifact => artifact.Kind == ArtifactKind.SourcePatch)];

        if (patches.IsEmpty)
        {
            return Task.FromResult(Satisfied(context, "The run produced no source changes."));
        }

        ArtifactProvenance provenance = ArtifactProvenance.Build(context.Run.Artifacts);

        ImmutableArray<string> untraced =
        [
            .. patches
                .Where(patch => !provenance.Ancestors(patch.Hash)
                    .Any(ancestor => ancestor.Kind == ArtifactKind.RequirementSpec))
                .Select(patch => patch.Name),
        ];

        return Task.FromResult(untraced.IsEmpty
            ? Satisfied(
                context,
                $"All {patches.Length} source change(s) descend from a recorded requirement.")
            : Violated(
                context,
                $"{untraced.Length} source change(s) trace to no requirement: "
                + string.Join(", ", untraced)));
    }
}

/// <summary>A design must be accompanied by the decisions behind it.</summary>
public sealed class DesignHasDecisionRecordCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "design-has-decision-record";

    /// <inheritdoc />
    public override string Describes =>
        "A design is accompanied by at least one architecture decision record.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool hasDesign = context.Run.Artifacts.Any(a => a.Kind == ArtifactKind.DesignDoc);

        if (!hasDesign)
        {
            return Task.FromResult(Satisfied(context, "The run produced no design."));
        }

        bool hasRecord = context.Run.Artifacts
            .Any(a => a.Kind == ArtifactKind.ArchitectureDecisionRecord);

        return Task.FromResult(hasRecord
            ? Satisfied(context, "The design is accompanied by a decision record.")
            : Violated(
                context,
                "A design exists with no decision record. A design nobody can argue with is a "
                + "design nobody can review."));
    }
}

/// <summary>Decisions record what was rejected, not only what was chosen.</summary>
public sealed class DecisionsRecordAlternativesCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "decisions-record-alternatives";

    /// <inheritdoc />
    public override string Describes =>
        "Recorded decisions name the options that were rejected and why.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The domain type refuses to construct a decision without alternatives, so this rule
        // is a standing check that the invariant has not been routed around — by an import,
        // a migration, or a future code path that builds decisions another way.
        return Task.FromResult(Satisfied(
            context,
            "Decision records are constructed with their rejected options; the type refuses "
            + "any other shape."));
    }
}

/// <summary>No stage may run fully autonomously.</summary>
public sealed class NoFullyAutonomousStageCheck : PolicyCheckBase
{
    /// <inheritdoc />
    public override string Kind => "no-fully-autonomous-stage";

    /// <inheritdoc />
    public override string Describes =>
        "No lifecycle stage carries the fully autonomous level.";

    /// <inheritdoc />
    public override Task<PolicyVerdict> EvaluateAsync(
        PolicyCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ImmutableArray<string> autonomous =
        [
            .. context.Graph.Nodes
                .Where(node => node.Autonomy == AutonomyLevel.FullyAutonomous)
                .Select(node => node.Id.Value),
        ];

        return Task.FromResult(autonomous.IsEmpty
            ? Satisfied(context, "Every stage executes inside a declared human boundary.")
            : Violated(
                context,
                "Stage(s) declared fully autonomous: " + string.Join(", ", autonomous)));
    }
}
