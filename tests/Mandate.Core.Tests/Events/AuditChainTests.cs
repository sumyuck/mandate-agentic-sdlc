using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Tests.Support;

namespace Mandate.Core.Tests.Events;

/// <summary>
/// Each test here is a tampering scenario an auditor would want detected.
/// </summary>
public sealed class AuditChainTests
{
    private static readonly RunId Run = RunId.New(TestClock.DefaultStart, "abc123");
    private static readonly NodeId Node = NodeId.Parse("implement");

    private static List<RunEvent> BuildChain(int length)
    {
        List<RunEvent> events = [];
        RunEvent? previous = null;

        for (int index = 0; index < length; index++)
        {
            previous = RunEvent.Append(
                previous,
                Run,
                TestClock.DefaultStart.AddSeconds(index),
                index == 0 ? RunEventKind.RunPlanned : RunEventKind.NodeStateChanged,
                index == 0 ? null : Node,
                Actor.Engine,
                new { step = index });

            events.Add(previous);
        }

        return events;
    }

    [Fact]
    public void An_untouched_chain_verifies()
    {
        AuditVerification result = AuditChain.Verify(Run, BuildChain(5));

        result.IsIntact.ShouldBeTrue();
        result.EventCount.ShouldBe(5);
        result.Summary.ShouldContain("intact");
    }

    [Fact]
    public void An_empty_stream_verifies_vacuously()
    {
        AuditChain.Verify(Run, []).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public void Editing_an_event_is_detected()
    {
        List<RunEvent> events = BuildChain(4);
        RunEvent original = events[2];

        // Rewrite the payload while keeping the recorded digest: the classic quiet edit.
        events[2] = RunEvent.Rehydrate(
            original.Sequence, original.RunId, original.OccurredAt, original.Kind,
            original.NodeId, original.Actor, """{"step":999}""",
            original.PreviousHash, original.Hash);

        AuditVerification result = AuditChain.Verify(Run, events);

        result.IsIntact.ShouldBeFalse();
        result.Breaks.ShouldContain(defect =>
            defect.Reason == AuditBreakReason.ContentAltered && defect.Sequence == 3);
    }

    [Fact]
    public void Reattributing_an_approval_to_another_person_is_detected()
    {
        List<RunEvent> events = BuildChain(3);
        RunEvent original = events[1];

        events[1] = RunEvent.Rehydrate(
            original.Sequence, original.RunId, original.OccurredAt, RunEventKind.ApprovalGranted,
            original.NodeId, Actor.Human("someone-else"), original.PayloadJson,
            original.PreviousHash, original.Hash);

        AuditChain.Verify(Run, events).Breaks
            .ShouldContain(defect => defect.Reason == AuditBreakReason.ContentAltered);
    }

    [Fact]
    public void Deleting_an_event_is_detected_as_both_a_gap_and_a_broken_link()
    {
        List<RunEvent> events = BuildChain(4);
        events.RemoveAt(2);

        AuditVerification result = AuditChain.Verify(Run, events);

        result.Breaks.ShouldContain(defect => defect.Reason == AuditBreakReason.SequenceGap);
        result.Breaks.ShouldContain(defect => defect.Reason == AuditBreakReason.BrokenLink);
    }

    [Fact]
    public void Truncating_the_start_of_a_log_is_detected()
    {
        // Removing the beginning would otherwise leave a self-consistent chain.
        List<RunEvent> events = BuildChain(4);
        events.RemoveAt(0);

        AuditVerification result = AuditChain.Verify(Run, events);

        result.Breaks.ShouldContain(defect => defect.Reason == AuditBreakReason.MissingOrigin);
        result.Breaks.ShouldContain(defect => defect.Reason == AuditBreakReason.BadGenesisLink);
    }

    [Fact]
    public void Reordering_events_is_detected()
    {
        List<RunEvent> events = BuildChain(4);
        (events[1], events[2]) = (events[2], events[1]);

        AuditChain.Verify(Run, events).IsIntact.ShouldBeFalse();
    }

    [Fact]
    public void A_duplicated_event_is_detected()
    {
        List<RunEvent> events = BuildChain(3);
        events.Insert(2, events[1]);

        AuditChain.Verify(Run, events).Breaks
            .ShouldContain(defect => defect.Reason == AuditBreakReason.DuplicateSequence);
    }

    [Fact]
    public void Events_spliced_in_from_another_run_are_detected()
    {
        List<RunEvent> events = BuildChain(3);
        RunId otherRun = RunId.New(TestClock.DefaultStart, "def456");
        RunEvent original = events[1];

        events[1] = RunEvent.Rehydrate(
            original.Sequence, otherRun, original.OccurredAt, original.Kind, original.NodeId,
            original.Actor, original.PayloadJson, original.PreviousHash, original.Hash);

        AuditChain.Verify(Run, events).Breaks
            .ShouldContain(defect => defect.Reason == AuditBreakReason.ForeignRun);
    }

    [Fact]
    public void A_backdated_event_is_detected_because_it_would_corrupt_the_metrics()
    {
        List<RunEvent> events = BuildChain(3);
        RunEvent original = events[2];

        RunEvent backdated = RunEvent.Create(
            original.Sequence, original.RunId, TestClock.DefaultStart.AddMinutes(-10),
            original.Kind, original.NodeId, original.Actor, original.PayloadJson,
            original.PreviousHash);

        events[2] = backdated;

        AuditVerification result = AuditChain.Verify(Run, events);

        result.Breaks.ShouldContain(defect =>
            defect.Reason == AuditBreakReason.TimestampRegression);
        result.Breaks.First(defect => defect.Reason == AuditBreakReason.TimestampRegression)
            .Detail.ShouldContain("MTTR");
    }

    [Fact]
    public void All_defects_are_reported_not_just_the_first()
    {
        // An auditor needs the shape of the damage, not only its earliest symptom.
        List<RunEvent> events = BuildChain(6);
        events.RemoveAt(4);
        events.RemoveAt(1);

        AuditChain.Verify(Run, events).Breaks.Length.ShouldBeGreaterThan(2);
    }

    [Fact]
    public void The_summary_points_at_the_first_defect()
    {
        List<RunEvent> events = BuildChain(4);
        events.RemoveAt(2);

        AuditChain.Verify(Run, events).Summary.ShouldContain("BROKEN");
    }
}
