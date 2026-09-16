using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Tests.Support;

namespace Mandate.Core.Tests.Events;

public sealed class RunEventTests
{
    private static readonly RunId Run = RunId.New(TestClock.DefaultStart, "abc123");
    private static readonly NodeId Node = NodeId.Parse("requirements");

    private static RunEvent First(object? payload = null) => RunEvent.Append(
        previous: null,
        Run,
        TestClock.DefaultStart,
        RunEventKind.RunPlanned,
        nodeId: null,
        Actor.Engine,
        payload ?? new { workflow = "sdlc", version = "v1" });

    [Fact]
    public void The_first_event_of_a_run_is_sequence_one_linked_to_genesis()
    {
        RunEvent origin = First();

        origin.Sequence.ShouldBe(1);
        origin.PreviousHash.ShouldBe(Sha256Hash.Genesis);
    }

    [Fact]
    public void Appending_links_each_event_to_its_predecessor()
    {
        RunEvent origin = First();
        RunEvent second = RunEvent.Append(
            origin, Run, TestClock.DefaultStart.AddSeconds(1), RunEventKind.NodeStateChanged,
            Node, Actor.Engine, new { from = "Pending", to = "Ready" });

        second.Sequence.ShouldBe(2);
        second.PreviousHash.ShouldBe(origin.Hash);
        second.Hash.ShouldNotBe(origin.Hash);
    }

    [Fact]
    public void The_digest_covers_the_payload()
    {
        RunEvent a = First(new { workflow = "sdlc", version = "v1" });
        RunEvent b = First(new { workflow = "sdlc", version = "v2" });

        b.Hash.ShouldNotBe(a.Hash);
    }

    [Fact]
    public void The_digest_covers_the_actor_so_attribution_cannot_be_swapped()
    {
        RunEvent byEngine = RunEvent.Create(
            1, Run, TestClock.DefaultStart, RunEventKind.ApprovalGranted, Node,
            Actor.Human("muskan"), "{}", Sha256Hash.Genesis);

        RunEvent byOther = RunEvent.Create(
            1, Run, TestClock.DefaultStart, RunEventKind.ApprovalGranted, Node,
            Actor.Human("someone-else"), "{}", Sha256Hash.Genesis);

        byOther.Hash.ShouldNotBe(byEngine.Hash);
    }

    [Fact]
    public void The_digest_covers_the_predecessor_link_so_events_cannot_be_reordered()
    {
        RunEvent linkedToGenesis = RunEvent.Create(
            2, Run, TestClock.DefaultStart, RunEventKind.NodeStateChanged, Node,
            Actor.Engine, "{}", Sha256Hash.Genesis);

        RunEvent linkedElsewhere = RunEvent.Create(
            2, Run, TestClock.DefaultStart, RunEventKind.NodeStateChanged, Node,
            Actor.Engine, "{}", Sha256Hash.OfUtf8("somewhere else"));

        linkedElsewhere.Hash.ShouldNotBe(linkedToGenesis.Hash);
    }

    [Fact]
    public void Identical_content_produces_an_identical_digest_so_replay_is_reproducible()
    {
        First().Hash.ShouldBe(First().Hash);
    }

    [Fact]
    public void Payloads_round_trip_through_their_declared_type()
    {
        RunEvent recorded = RunEvent.Append(
            previous: null, Run, TestClock.DefaultStart, RunEventKind.NodeStateChanged,
            Node, Actor.Engine, new StateChange("Pending", "Ready", 1));

        StateChange payload = recorded.Payload<StateChange>();

        payload.ShouldBe(new StateChange("Pending", "Ready", 1));
    }

    [Fact]
    public void An_event_must_name_its_actor()
    {
        // An unattributed action is not auditable, so the type refuses to represent one.
        ArgumentException error = Should.Throw<ArgumentException>(() => RunEvent.Create(
            1, Run, TestClock.DefaultStart, RunEventKind.RunPlanned, null,
            default, "{}", Sha256Hash.Genesis));

        error.Message.ShouldContain("auditable");
    }

    [Fact]
    public void An_event_must_have_a_known_kind() =>
        Should.Throw<ArgumentException>(() => RunEvent.Create(
            1, Run, TestClock.DefaultStart, RunEventKind.Unknown, null,
            Actor.Engine, "{}", Sha256Hash.Genesis));

    [Fact]
    public void Sequence_numbering_starts_at_one() =>
        Should.Throw<ArgumentOutOfRangeException>(() => RunEvent.Create(
            0, Run, TestClock.DefaultStart, RunEventKind.RunPlanned, null,
            Actor.Engine, "{}", Sha256Hash.Genesis));

    [Fact]
    public void Appending_across_runs_is_refused()
    {
        RunEvent origin = First();
        RunId otherRun = RunId.New(TestClock.DefaultStart, "def456");

        Should.Throw<ArgumentException>(() => RunEvent.Append(
            origin, otherRun, TestClock.DefaultStart, RunEventKind.RunStarted,
            null, Actor.Engine, new { }));
    }

    [Fact]
    public void Rehydration_preserves_the_stored_digest_rather_than_recomputing_it()
    {
        // Recomputing on load would erase the very mismatch that reveals tampering.
        Sha256Hash wrongHash = Sha256Hash.OfUtf8("not the real digest");

        RunEvent loaded = RunEvent.Rehydrate(
            1, Run, TestClock.DefaultStart, RunEventKind.RunPlanned, null,
            Actor.Engine, "{}", Sha256Hash.Genesis, wrongHash);

        loaded.Hash.ShouldBe(wrongHash);
        loaded.RecomputeHash().ShouldNotBe(wrongHash);
    }

    private sealed record StateChange(string From, string To, int Attempt);
}
