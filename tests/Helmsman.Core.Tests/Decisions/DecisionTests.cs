using Helmsman.Core.Decisions;
using Helmsman.Core.Identifiers;
using Helmsman.Core.Tests.Support;

namespace Helmsman.Core.Tests.Decisions;

/// <summary>
/// The type refuses to hold an indefensible decision. These tests pin that contract.
/// </summary>
public sealed class DecisionTests
{
    private static readonly RunId Run = RunId.New(TestClock.DefaultStart, "abc123");
    private static readonly NodeId Node = NodeId.Parse("architecture");

    private static Decision Record(params DecisionOption[] options) => Decision.Record(
        "dec-001",
        Run,
        Node,
        "How should short codes be generated?",
        options,
        rationale: "Sequential ids keep the hot path allocation-free and collision-free.",
        confidence: 0.8,
        DecisionAuthority.Agent,
        Actor.Agent("architect"),
        TestClock.DefaultStart);

    private static DecisionOption Chosen(string name = "base62-sequence") =>
        new(name, "Base62 over a monotonic id.", RejectedBecause: null);

    private static DecisionOption Rejected(string name = "hash-truncation") =>
        new(name, "Truncated SHA-256 of the URL.", "Requires collision handling on the hot path.");

    [Fact]
    public void A_recorded_decision_exposes_the_option_taken_and_those_rejected()
    {
        Decision decision = Record(Chosen(), Rejected());

        decision.Chosen.Name.ShouldBe("base62-sequence");
        decision.Rejected.Select(option => option.Name).ShouldBe(["hash-truncation"]);
    }

    [Fact]
    public void A_choice_with_no_alternative_is_not_a_decision()
    {
        // The most common way a decision log becomes worthless is recording only the outcome.
        ArgumentException error = Should.Throw<ArgumentException>(() => Record(Chosen()));

        error.Message.ShouldContain("at least two options");
        error.Message.ShouldContain("nothing was weighed");
    }

    [Fact]
    public void An_unexplained_rejection_is_refused()
    {
        ArgumentException error = Should.Throw<ArgumentException>(() => Record(
            Chosen(),
            new DecisionOption("hash-truncation", "Truncated hash.", RejectedBecause: "   ")));

        error.Message.ShouldContain("rejected without a reason");
    }

    [Fact]
    public void Exactly_one_option_must_be_chosen()
    {
        Should.Throw<ArgumentException>(() => Record(Chosen("a"), Chosen("b")))
            .Message.ShouldContain("Exactly one option");

        Should.Throw<ArgumentException>(() => Record(Rejected("a"), Rejected("b")))
            .Message.ShouldContain("Exactly one option");
    }

    [Fact]
    public void Rationale_is_required()
    {
        Should.Throw<ArgumentException>(() => Decision.Record(
            "dec-002", Run, Node, "Which store?", [Chosen(), Rejected()],
            rationale: "  ", confidence: 0.9, DecisionAuthority.Agent,
            Actor.Agent("architect"), TestClock.DefaultStart));
    }

    [Fact]
    public void Authority_and_decider_are_required()
    {
        Should.Throw<ArgumentException>(() => Decision.Record(
            "dec-003", Run, Node, "Which store?", [Chosen(), Rejected()],
            "because", 0.9, DecisionAuthority.Unknown,
            Actor.Agent("architect"), TestClock.DefaultStart));

        Should.Throw<ArgumentException>(() => Decision.Record(
            "dec-004", Run, Node, "Which store?", [Chosen(), Rejected()],
            "because", 0.9, DecisionAuthority.Agent,
            default, TestClock.DefaultStart));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Confidence_outside_zero_to_one_is_refused(double confidence) =>
        Should.Throw<ArgumentOutOfRangeException>(() => Decision.Record(
            "dec-005", Run, Node, "Which store?", [Chosen(), Rejected()],
            "because", confidence, DecisionAuthority.Agent,
            Actor.Agent("architect"), TestClock.DefaultStart));

    [Fact]
    public void Low_confidence_is_detectable_so_a_gate_can_escalate_to_a_human()
    {
        Decision uncertain = Decision.Record(
            "dec-006", Run, Node, "Which store?", [Chosen(), Rejected()],
            "Weak evidence either way.", confidence: 0.4, DecisionAuthority.Agent,
            Actor.Agent("architect"), TestClock.DefaultStart);

        uncertain.IsLowConfidence().ShouldBeTrue();
        Record(Chosen(), Rejected()).IsLowConfidence().ShouldBeFalse();
    }

    [Fact]
    public void Evidence_is_de_duplicated()
    {
        Sha256Hash spec = Sha256Hash.OfUtf8("spec");

        Decision decision = Decision.Record(
            "dec-007", Run, Node, "Which store?", [Chosen(), Rejected()],
            "because", 0.9, DecisionAuthority.Agent, Actor.Agent("architect"),
            TestClock.DefaultStart, [spec, spec]);

        decision.Evidence.Length.ShouldBe(1);
    }

    [Fact]
    public void A_human_decision_records_the_person_not_just_the_role()
    {
        Decision decision = Decision.Record(
            "dec-008", Run, Node, "Approve the design?",
            [Chosen("approve"), Rejected("defer")],
            "Design matches the agreed scope.", 1.0, DecisionAuthority.Human,
            Actor.Human("muskan"), TestClock.DefaultStart);

        decision.Authority.ShouldBe(DecisionAuthority.Human);
        decision.DecidedBy.Value.ShouldBe("human:muskan");
    }
}
