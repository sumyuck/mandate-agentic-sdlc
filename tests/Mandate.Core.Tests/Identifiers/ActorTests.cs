using Mandate.Core.Identifiers;

namespace Mandate.Core.Tests.Identifiers;

public sealed class ActorTests
{
    [Fact]
    public void Canonical_form_is_kind_colon_name()
    {
        Actor.Engine.Value.ShouldBe("engine:orchestrator");
        Actor.Agent("implementer").Value.ShouldBe("agent:implementer");
        Actor.Human("muskan").Value.ShouldBe("human:muskan");
        Actor.Policy("chg-003").Value.ShouldBe("policy:chg-003");
    }

    [Theory]
    [InlineData("engine:orchestrator", ActorKind.Engine, "orchestrator")]
    [InlineData("agent:implementer", ActorKind.Agent, "implementer")]
    [InlineData("human:muskan", ActorKind.Human, "muskan")]
    [InlineData("HUMAN:muskan", ActorKind.Human, "muskan")]
    public void Round_trips_through_parse(string value, ActorKind expectedKind, string expectedName)
    {
        Actor actor = Actor.Parse(value);

        actor.Kind.ShouldBe(expectedKind);
        actor.Name.ShouldBe(expectedName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("muskan")]
    [InlineData(":muskan")]
    [InlineData("human:")]
    [InlineData("unknown:muskan")]
    [InlineData("wizard:muskan")]
    public void Malformed_or_unrecognised_actors_are_rejected(string candidate)
    {
        Actor.TryParse(candidate, out _).ShouldBeFalse();
        Should.Throw<FormatException>(() => Actor.Parse(candidate));
    }

    [Fact]
    public void A_name_may_not_contain_the_separator() =>
        Should.Throw<ArgumentException>(() => Actor.Human("mus:kan"));

    [Fact]
    public void Same_participant_comparison_ignores_name_casing()
    {
        // Underpins segregation of duties: "Muskan" approving "muskan"'s change is the same
        // person, and the rule must not be defeated by how the name was typed.
        Actor.Human("Muskan").IsSameParticipantAs(Actor.Human("muskan")).ShouldBeTrue();
    }

    [Fact]
    public void Different_kinds_are_never_the_same_participant() =>
        Actor.Agent("reviewer").IsSameParticipantAs(Actor.Human("reviewer")).ShouldBeFalse();

    [Fact]
    public void Different_agents_are_different_participants() =>
        Actor.Agent("implementer").IsSameParticipantAs(Actor.Agent("reviewer")).ShouldBeFalse();

    [Fact]
    public void Default_value_is_recognisable_as_unassigned() =>
        default(Actor).IsEmpty.ShouldBeTrue();
}
