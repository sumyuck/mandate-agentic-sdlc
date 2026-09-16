using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Core.Policies;
using Mandate.Core.Workflow;
using Mandate.Policy.Checks;

namespace Mandate.Policy;

/// <summary>A registry built from a fixed set of checks.</summary>
public sealed class PolicyCheckRegistry : IPolicyCheckRegistry
{
    private readonly ImmutableDictionary<string, IPolicyCheck> _byKind;

    /// <summary>Creates a registry, rejecting two checks claiming the same kind.</summary>
    public PolicyCheckRegistry(IEnumerable<IPolicyCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        ImmutableDictionary<string, IPolicyCheck>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, IPolicyCheck>(StringComparer.Ordinal);

        foreach (IPolicyCheck check in checks)
        {
            if (builder.ContainsKey(check.Kind))
            {
                throw new ArgumentException(
                    $"Two checks claim the kind '{check.Kind}'. Which one evaluated a rule "
                    + "would depend on registration order.",
                    nameof(checks));
            }

            builder[check.Kind] = check;
        }

        _byKind = builder.ToImmutable();
        KnownKinds = _byKind.Keys.ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> KnownKinds { get; }

    /// <inheritdoc />
    public IPolicyCheck? Resolve(string kind) =>
        _byKind.TryGetValue(kind, out IPolicyCheck? check) ? check : null;

    /// <summary>
    /// The checks this build ships.
    /// </summary>
    /// <param name="verifyChain">
    /// How to verify a run's audit chain. Supplied by the composition root because it needs
    /// the journal, which policy has no business reaching into itself.
    /// </param>
    public static PolicyCheckRegistry BuiltIn(
        Func<Core.Identifiers.RunId, CancellationToken, Task<Core.Events.AuditVerification>> verifyChain) =>
        new(
        [
            new ChangeTracesToRequirementCheck(),
            new DesignHasDecisionRecordCheck(),
            new DecisionsRecordAlternativesCheck(),
            new NoFullyAutonomousStageCheck(),
            new RequiredApprovalsHeldCheck(),
            new SegregationOfDutiesUpheldCheck(),
            new TestsExecutedCheck(),
            new WorkspaceIsCleanCheck(),
            new NoSecretsInWorkspaceCheck(),
            new AuditChainIntactCheck(verifyChain),
        ]);
}

/// <summary>
/// Evaluates policy packs against a run.
/// </summary>
/// <remarks>
/// <para>
/// Every rule is evaluated, including ones already known to be waived. A waiver stops a
/// violation from blocking; it does not stop it being checked or reported. That distinction
/// is what lets the trail answer "what did we knowingly let through, and who said so?" — a
/// waiver that suppressed the check would erase the very thing an auditor came for.
/// </para>
/// <para>
/// A rule whose check is not registered is a violation, not a skip. A control nobody can
/// evaluate is not a control that passed.
/// </para>
/// </remarks>
public sealed class PolicyEngine(
    ImmutableArray<PolicyPack> packs, IPolicyCheckRegistry checks) : IPolicyEngine
{
    /// <inheritdoc />
    public ImmutableArray<PolicyPack> Packs { get; } = packs;

    /// <summary>Rules whose check kind nothing can evaluate.</summary>
    /// <remarks>
    /// Reported at composition time so an unusable pack is caught before a run needs it.
    /// </remarks>
    public ImmutableArray<string> FindUnmetRequirements()
    {
        ImmutableArray<string>.Builder problems = ImmutableArray.CreateBuilder<string>();

        foreach (PolicyPack pack in Packs)
        {
            foreach (PolicyRule rule in pack.Rules.Where(
                         rule => checks.Resolve(rule.Check) is null))
            {
                problems.Add(
                    $"Rule '{rule.Id}' in pack '{pack.Name}' uses check '{rule.Check}', which "
                    + $"nothing can evaluate. Known checks: "
                    + string.Join(", ", checks.KnownKinds.Order(StringComparer.Ordinal)) + ".");
            }
        }

        return problems.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<PolicyEvaluation> EvaluateAsync(
        string pack,
        IRunView run,
        WorkflowGraph graph,
        IRunWorkspace workspace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(workspace);

        PolicyPack resolved = Packs.FirstOrDefault(candidate =>
                                  string.Equals(candidate.Name, pack, StringComparison.OrdinalIgnoreCase))
                              ?? throw new KeyNotFoundException(
                                  $"No policy pack named '{pack}' is loaded. Loaded: "
                                  + (Packs.IsEmpty
                                      ? "none"
                                      : string.Join(", ", Packs.Select(candidate => candidate.Name)))
                                  + ".");

        ImmutableArray<PolicyVerdict>.Builder verdicts =
            ImmutableArray.CreateBuilder<PolicyVerdict>(resolved.Rules.Length);

        foreach (PolicyRule rule in resolved.Rules)
        {
            IPolicyCheck? check = checks.Resolve(rule.Check);

            if (check is null)
            {
                verdicts.Add(new PolicyVerdict(
                    rule,
                    Satisfied: false,
                    $"No check is registered for '{rule.Check}', so this rule cannot be "
                    + "evaluated. A control nobody can evaluate is not a control that passed.",
                    run.Waivers.TryGetValue(rule.Id, out PolicyWaiver? waived) ? waived : null));

                continue;
            }

            verdicts.Add(await check
                .EvaluateAsync(new PolicyCheckContext(rule, run, graph, workspace), cancellationToken)
                .ConfigureAwait(false));
        }

        return new PolicyEvaluation(resolved.Identity, verdicts.ToImmutable());
    }
}
