using Mandate.Core.Events;
using Mandate.Core.Runs;
using Mandate.Observability.Metrics;
using Mandate.Observability.Reporting;

namespace Mandate.Observability.Tests;

/// <summary>
/// The report a reviewer opens.
/// </summary>
/// <remarks>
/// It is evidence, so the tests are mostly about what it must not do: depend on a network,
/// run script, or say anything the log does not support.
/// </remarks>
public sealed class HtmlRunReportTests
{
    private static string Render(EventLogBuilder log, string request = "Build a URL shortener") =>
        HtmlRunReport.Render(
            log.Events,
            MetricsCalculator.Compute(log.RunId, log.Events),
            AuditChain.Verify(log.RunId, log.Events),
            request,
            "sdlc@v1",
            "Greenfield");

    private static EventLogBuilder CompleteRun() =>
        new EventLogBuilder()
            .Planned("requirements", "architecture").Started()
            .Attempt("requirements").Moves("requirements", NodeState.Running, NodeState.Succeeded)
            .Attempt("architecture")
            .AsksApproval("architecture", "tech-lead")
            .Advance(45)
            .Approves("tech-lead", "alex")
            .Moves("architecture", NodeState.Running, NodeState.Succeeded)
            .Completed(RunStatus.Succeeded);

    [Fact]
    public void It_is_a_complete_html_document()
    {
        string html = Render(CompleteRun());

        html.ShouldStartWith("<!doctype html>");
        html.TrimEnd().ShouldEndWith("</html>");
    }

    [Fact]
    public void It_fetches_nothing_and_runs_nothing()
    {
        // Evidence that only renders with network access is evidence with a dependency it
        // should not have — and it has to keep working years from now, offline, in an archive.
        string html = Render(CompleteRun());

        html.ShouldNotContain("<script");
        html.ShouldNotContain("<link");
        html.ShouldNotContain("https://", Case.Insensitive);

        // The one http reference permitted is the SVG namespace, which is an identifier
        // rather than something the browser fetches.
        html.Split("http://", StringSplitOptions.None).Length.ShouldBe(2);
        html.ShouldContain("http://www.w3.org/2000/svg");
    }

    [Fact]
    public void It_answers_what_was_asked_for()
    {
        Render(CompleteRun(), request: "Add per-link click analytics")
            .ShouldContain("Add per-link click analytics");
    }

    [Fact]
    public void It_shows_where_a_human_intervened_and_who_it_was()
    {
        string html = Render(CompleteRun());

        html.ShouldContain("Where a human intervened");
        html.ShouldContain("human:alex");
        html.ShouldContain("tech-lead");
    }

    [Fact]
    public void It_states_the_chain_verdict_rather_than_assuming_it()
    {
        Render(CompleteRun()).ShouldContain("chain intact");
    }

    [Fact]
    public void A_broken_chain_is_reported_as_broken()
    {
        EventLogBuilder log = CompleteRun();

        System.Collections.Immutable.ImmutableArray<RunEvent> tampered =
            log.Events.RemoveAt(2);

        string html = HtmlRunReport.Render(
            tampered,
            MetricsCalculator.Compute(log.RunId, tampered),
            AuditChain.Verify(log.RunId, tampered),
            "Build a URL shortener",
            "sdlc@v1",
            "Greenfield");

        html.ShouldContain("chain broken");
        html.ShouldContain("BROKEN");
    }

    [Fact]
    public void It_draws_a_timeline_for_the_stages_that_ran()
    {
        string html = Render(CompleteRun());

        html.ShouldContain("<svg");
        html.ShouldContain("requirements");
        html.ShouldContain("architecture");
    }

    [Fact]
    public void A_run_with_no_stages_says_so_rather_than_drawing_an_empty_chart()
    {
        string html = Render(new EventLogBuilder().Planned("a").Started());

        html.ShouldContain("no timeline to draw");
    }

    [Fact]
    public void It_shows_a_re_plan_with_what_it_left_alone()
    {
        EventLogBuilder log = new EventLogBuilder()
            .Planned("a", "b").Started()
            .Attempt("a").Moves("a", NodeState.Running, NodeState.Succeeded)
            .Replanned("a")
            .Completed(RunStatus.Succeeded);

        string html = Render(log);

        html.ShouldContain("Re-planning");
        html.ShouldContain("Left alone");
    }

    [Fact]
    public void Content_that_would_break_the_markup_is_escaped()
    {
        string html = Render(CompleteRun(), request: "Handle <script>alert(1)</script> & co");

        html.ShouldNotContain("<script>alert(1)</script>");
        html.ShouldContain("&lt;script&gt;");
        html.ShouldContain("&amp; co");
    }

    [Fact]
    public void It_says_that_its_figures_come_from_the_log()
    {
        // The claim the whole report rests on, stated on the page rather than only in a
        // design document nobody opens next to it.
        string html = Render(CompleteRun());

        html.ShouldContain("derived from that log");
        html.ShouldContain("only caused");
    }

    [Fact]
    public void It_names_the_engine_build_that_produced_it()
    {
        Render(CompleteRun()).ShouldContain("mandate/");
    }
}
