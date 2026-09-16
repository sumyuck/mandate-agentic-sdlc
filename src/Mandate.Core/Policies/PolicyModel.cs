using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Policies;

/// <summary>What kind of concern a rule protects.</summary>
/// <remarks>
/// Categories are not decoration: they are how a reviewer finds the rules relevant to the
/// question they are asking, and how a violation is routed to whoever owns that concern.
/// </remarks>
public enum PolicyCategory
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Protects against the change being unsafe — secrets, injection, exposure.</summary>
    Security = 1,

    /// <summary>Protects the trail — traceability, decision records, audit integrity.</summary>
    Compliance = 2,

    /// <summary>Protects how change reaches production — approvals, evidence, separation of duty.</summary>
    ChangeControl = 3,
}

/// <summary>Whether a violation stops the run.</summary>
public enum PolicySeverity
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>Recorded and surfaced, but the run continues.</summary>
    Advisory = 1,

    /// <summary>Stops the run until it is resolved or a human waives it, on the record.</summary>
    Blocking = 2,
}

/// <summary>
/// One rule in a policy pack.
/// </summary>
/// <param name="Id">Stable identifier, cited in violations and waivers.</param>
/// <param name="Category">The concern it protects.</param>
/// <param name="Severity">Whether a violation stops the run.</param>
/// <param name="Statement">What must be true, in one sentence.</param>
/// <param name="Rationale">Why it matters. Read by whoever is deciding whether to waive it.</param>
/// <param name="Check">The check kind that evaluates it.</param>
/// <param name="Expression">The check's parameter, if it takes one.</param>
public sealed record PolicyRule(
    string Id,
    PolicyCategory Category,
    PolicySeverity Severity,
    string Statement,
    string Rationale,
    string Check,
    string Expression);

/// <summary>A named collection of rules, versioned and loaded from a file.</summary>
/// <param name="Name">The pack's name, as gates and waivers refer to it.</param>
/// <param name="Version">Version of this pack; recorded with every evaluation.</param>
/// <param name="Description">What the pack is for.</param>
/// <param name="Rules">The rules, in declaration order.</param>
public sealed record PolicyPack(
    string Name,
    string Version,
    string Description,
    ImmutableArray<PolicyRule> Rules)
{
    /// <summary>Name and version, as recorded on evaluations.</summary>
    public string Identity => $"{Name}@{Version}";
}

/// <summary>A human's decision to let a violation pass.</summary>
/// <param name="RuleId">The rule being waived.</param>
/// <param name="GrantedBy">Who waived it.</param>
/// <param name="Reason">Why. A waiver without a reason is indistinguishable from not checking.</param>
/// <param name="GrantedAt">When.</param>
public sealed record PolicyWaiver(
    string RuleId, Actor GrantedBy, string Reason, DateTimeOffset GrantedAt);

/// <summary>One rule's verdict.</summary>
/// <param name="Rule">The rule evaluated.</param>
/// <param name="Satisfied">Whether it held.</param>
/// <param name="Explanation">The evidence behind the verdict, in reviewer-facing terms.</param>
/// <param name="Waiver">The waiver that lets a violation pass, when one was granted.</param>
public sealed record PolicyVerdict(
    PolicyRule Rule, bool Satisfied, string Explanation, PolicyWaiver? Waiver = null)
{
    /// <summary>True when the rule was violated but a human allowed it through.</summary>
    public bool IsWaived => !Satisfied && Waiver is not null;

    /// <summary>True when this verdict stops the run.</summary>
    public bool Blocks =>
        !Satisfied && Waiver is null && Rule.Severity == PolicySeverity.Blocking;
}

/// <summary>The result of evaluating a pack against a run.</summary>
/// <param name="Pack">The pack's identity.</param>
/// <param name="Verdicts">Each rule's verdict, in declaration order.</param>
public sealed record PolicyEvaluation(string Pack, ImmutableArray<PolicyVerdict> Verdicts)
{
    /// <summary>True when nothing outstanding stops the run.</summary>
    public bool IsClean => !Verdicts.Any(verdict => verdict.Blocks);

    /// <summary>Violations that stop the run.</summary>
    public IEnumerable<PolicyVerdict> Blocking => Verdicts.Where(verdict => verdict.Blocks);

    /// <summary>Violations a human allowed through.</summary>
    public IEnumerable<PolicyVerdict> Waived => Verdicts.Where(verdict => verdict.IsWaived);

    /// <summary>Violations recorded but not blocking.</summary>
    public IEnumerable<PolicyVerdict> Advisory =>
        Verdicts.Where(verdict =>
            !verdict.Satisfied && verdict.Waiver is null
            && verdict.Rule.Severity == PolicySeverity.Advisory);

    /// <summary>A one-line summary for logs and CLI output.</summary>
    public string Summary
    {
        get
        {
            int blocking = Blocking.Count();
            int waived = Waived.Count();
            int advisory = Advisory.Count();

            if (blocking == 0 && waived == 0 && advisory == 0)
            {
                return $"{Pack}: all {Verdicts.Length} rule(s) satisfied.";
            }

            List<string> parts = [];

            if (blocking > 0)
            {
                parts.Add($"{blocking} blocking");
            }

            if (waived > 0)
            {
                parts.Add($"{waived} waived");
            }

            if (advisory > 0)
            {
                parts.Add($"{advisory} advisory");
            }

            return $"{Pack}: {string.Join(", ", parts)} of {Verdicts.Length} rule(s).";
        }
    }
}
