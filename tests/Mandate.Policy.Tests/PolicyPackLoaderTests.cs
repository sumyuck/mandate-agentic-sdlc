using System.Collections.Immutable;
using Mandate.Core.Policies;

namespace Mandate.Policy.Tests;

public sealed class PolicyPackLoaderTests
{
    private const string Minimal = """
        name: change-control
        version: v1
        rules:
          - id: CHG-001
            category: change-control
            severity: blocking
            statement: Every required approval is held.
            rationale: A checkpoint that can be skipped is decorative.
            check: required-approvals-held
        """;

    [Fact]
    public void A_minimal_pack_loads()
    {
        PolicyPack pack = PolicyPackLoader.Load(Minimal);

        pack.Identity.ShouldBe("change-control@v1");
        pack.Rules.ShouldHaveSingleItem();
        pack.Rules[0].Category.ShouldBe(PolicyCategory.ChangeControl);
        pack.Rules[0].Severity.ShouldBe(PolicySeverity.Blocking);
    }

    [Fact]
    public void An_unknown_key_is_rejected_rather_than_ignored()
    {
        // A rule silently dropped because its key was misspelled is a control that stops
        // applying without anyone being told.
        Should.Throw<PolicyFormatException>(() => PolicyPackLoader.Load("""
            name: change-control
            version: v1
            severty: blocking
            rules:
              - id: CHG-001
                category: change-control
                severity: blocking
                statement: s
                rationale: r
                check: required-approvals-held
            """));
    }

    [Fact]
    public void A_rule_without_a_rationale_is_rejected()
    {
        // Whoever is deciding whether to waive it needs to know what they are overriding.
        PolicyFormatException error = Should.Throw<PolicyFormatException>(
            () => PolicyPackLoader.Load("""
                name: change-control
                version: v1
                rules:
                  - id: CHG-001
                    category: change-control
                    severity: blocking
                    statement: Something must hold.
                    check: required-approvals-held
                """));

        error.Message.ShouldContain("rationale");
    }

    [Fact]
    public void Two_rules_sharing_an_id_are_rejected()
    {
        PolicyFormatException error = Should.Throw<PolicyFormatException>(
            () => PolicyPackLoader.Load(Minimal + """

                  - id: CHG-001
                    category: security
                    severity: advisory
                    statement: Something else.
                    rationale: Because.
                    check: no-secrets-in-workspace
                """));

        error.Message.ShouldContain("A waiver naming one would be ambiguous");
    }

    [Fact]
    public void A_pack_with_no_rules_is_rejected() =>
        Should.Throw<PolicyFormatException>(() => PolicyPackLoader.Load("""
            name: empty
            version: v1
            rules: []
            """))
            .Message.ShouldContain("enforce nothing");

    [Fact]
    public void An_unrecognised_severity_lists_what_is_accepted()
    {
        PolicyFormatException error = Should.Throw<PolicyFormatException>(
            () => PolicyPackLoader.Load(Minimal.Replace(
                "severity: blocking", "severity: quite-important", StringComparison.Ordinal)));

        error.Message.ShouldContain("quite-important");
        error.Message.ShouldContain("blocking");
        error.Message.ShouldContain("advisory");
    }

    [Fact]
    public void A_missing_pack_is_reported_as_such() =>
        Should.Throw<PolicyFormatException>(
                () => PolicyPackLoader.LoadFile("does/not/exist.yaml"))
            .Message.ShouldContain("does not exist");

    // ---- the shipped packs ----

    [Fact]
    public void The_shipped_packs_load()
    {
        ImmutableArray<PolicyPack> packs = PolicyPackLoader.LoadDirectory(
            Path.Combine(RepositoryRoot.Path, "workflows", "policies"));

        packs.Select(pack => pack.Name)
            .ShouldBe(["change-control", "compliance", "security"], ignoreOrder: true);
    }

    [Fact]
    public void Every_shipped_rule_states_what_must_hold_and_why()
    {
        // A rule nobody can read is a rule nobody can apply, and one nobody can weigh is one
        // nobody can responsibly waive.
        foreach (PolicyPack pack in Shipped())
        {
            foreach (PolicyRule rule in pack.Rules)
            {
                rule.Statement.ShouldNotBeNullOrWhiteSpace($"{rule.Id} has no statement.");
                rule.Rationale.ShouldNotBeNullOrWhiteSpace($"{rule.Id} has no rationale.");
                rule.Category.ShouldNotBe(PolicyCategory.Unknown);
                rule.Severity.ShouldNotBe(PolicySeverity.Unknown);
            }
        }
    }

    [Fact]
    public void The_shipped_packs_cover_all_three_concerns_the_brief_names()
    {
        Shipped().SelectMany(pack => pack.Rules).Select(rule => rule.Category).Distinct()
            .ShouldBe(
                [PolicyCategory.ChangeControl, PolicyCategory.Compliance, PolicyCategory.Security],
                ignoreOrder: true);
    }

    [Fact]
    public void Every_shipped_rule_has_a_unique_id()
    {
        IEnumerable<string> ids = Shipped().SelectMany(pack => pack.Rules).Select(rule => rule.Id);

        ids.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(ids.Count());
    }

    private static ImmutableArray<PolicyPack> Shipped() =>
        PolicyPackLoader.LoadDirectory(
            Path.Combine(RepositoryRoot.Path, "workflows", "policies"));
}
