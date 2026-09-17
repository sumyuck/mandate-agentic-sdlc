using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Mandate.Core.Events;
using Mandate.Core.Runs;
using Mandate.Observability.Metrics;

namespace Mandate.Observability.Reporting;

/// <summary>
/// Renders a run as a single self-contained HTML page.
/// </summary>
/// <remarks>
/// <para>
/// The audience is someone who was not there: a reviewer, an auditor, an interviewer. They
/// should be able to open one file and answer what was asked for, what the system did, where
/// a human intervened, what was checked, and whether the record can be trusted — without
/// running anything.
/// </para>
/// <para>
/// Every figure on the page is derived from the event log rather than stored, and the page
/// says so. A report that could be written independently of what happened would be a
/// presentation, not evidence.
/// </para>
/// </remarks>
public static class HtmlRunReport
{
    /// <summary>Renders the report.</summary>
    public static string Render(
        ImmutableArray<RunEvent> events,
        RunMetrics metrics,
        AuditVerification verification,
        string request,
        string workflow,
        string scenario)
    {
        StringBuilder page = new();

        page.AppendLine("<!doctype html>");
        page.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        page.AppendLine(
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        page.AppendLine(CultureInfo.InvariantCulture,
            $"<title>Run {H(metrics.RunId.Value)}</title>");
        page.AppendLine(CultureInfo.InvariantCulture, $"<style>{ReportStyles.Css}</style>");
        page.AppendLine("</head><body>");

        Header(page, metrics, verification, request, workflow, scenario);
        Cards(page, metrics);
        Timeline(page, metrics);
        Stages(page, metrics);
        HumanDecisions(page, events);
        Policy(page, events);
        Replans(page, events);
        Decisions(page, events);
        EventLog(page, events);
        Footer(page, verification);

        page.AppendLine("</body></html>");
        return page.ToString();
    }

    private static void Header(
        StringBuilder page,
        RunMetrics metrics,
        AuditVerification verification,
        string request,
        string workflow,
        string scenario)
    {
        page.AppendLine(CultureInfo.InvariantCulture,
            $"<h1>Run <span class=\"mono\">{H(metrics.RunId.Value)}</span></h1>");

        page.AppendLine(CultureInfo.InvariantCulture,
            $"""<p class="subtitle">{H(workflow)} &middot; {H(scenario.ToLowerInvariant())} &middot; """
            + $"""started {H(metrics.StartedAt.ToString("u", CultureInfo.InvariantCulture))} &middot; """
            + $"{Status(metrics.Status)} {Chain(verification)}</p>");

        page.AppendLine(CultureInfo.InvariantCulture,
            $"""<p class="request"><strong>Requested:</strong> {H(request)}</p>""");
    }

    private static void Cards(StringBuilder page, RunMetrics metrics)
    {
        page.AppendLine("<h2>Reliability</h2>");
        page.AppendLine(
            """<p class="muted">Every figure below is computed from the run's event log. """
            + "None of them is stored, so none of them can be set, only caused.</p>");

        page.AppendLine("<div class=\"cards\">");

        Card(page, "success rate", Percent(metrics.SuccessRate),
            $"{metrics.Succeeded} of {metrics.Succeeded + metrics.Failed + metrics.RolledBack} attempted");
        Card(page, "end to end", Duration(metrics.EndToEnd), $"{metrics.EventCount} events");
        Card(page, "stages run", metrics.Stages.Length.ToString(CultureInfo.InvariantCulture),
            $"{metrics.Skipped} not required on this path");
        Card(page, "retry rate", Percent(metrics.RetryRate),
            $"{metrics.Retries} of {metrics.Attempts} attempts");
        Card(page, "mttr",
            metrics.MeanTimeToRecovery is { } mttr ? Duration(mttr) : "none",
            metrics.UnrecoveredFailures > 0
                ? $"{metrics.UnrecoveredFailures} never recovered"
                : $"{metrics.Failures.Length} failure(s)");
        Card(page, "rollback rate", Percent(metrics.RollbackRate),
            $"{metrics.Compensations} compensation(s)");
        Card(page, "gate block rate", Percent(metrics.GateBlockRate),
            $"{metrics.GateConditionsFailed} of {metrics.GateConditionsEvaluated} conditions");
        Card(page, "stage p50 / p95",
            $"{Duration(metrics.MedianStageDuration)} / {Duration(metrics.NinetyFifthStageDuration)}",
            "per stage");
        Card(page, "approval wait",
            metrics.MeanApprovalWait is { } wait ? Duration(wait) : "none",
            $"{metrics.ApprovalsGranted} granted, {metrics.ApprovalsDenied} refused");
        Card(page, "autonomy ratio", Percent(metrics.AutonomyRatio),
            $"{metrics.Attempts} agent attempt(s) vs human decisions");
        Card(page, "re-plans", metrics.ReplansPerformed.ToString(CultureInfo.InvariantCulture),
            metrics.ReplansRefused > 0
                ? $"{metrics.ReplansRefused} refused: budget spent"
                : "plan recomputed");
        Card(page, "policy", metrics.PolicyEvaluations.ToString(CultureInfo.InvariantCulture),
            $"{metrics.PolicyViolationsBlocked} blocked, {metrics.PolicyWaiversGranted} waived");

        page.AppendLine("</div>");
    }

    private static void Timeline(StringBuilder page, RunMetrics metrics)
    {
        page.AppendLine("<h2>Timeline</h2>");
        page.AppendLine(TimelineSvg.Render(metrics));
    }

    private static void Stages(StringBuilder page, RunMetrics metrics)
    {
        page.AppendLine("<h2>Stages</h2>");
        page.AppendLine("<table><thead><tr><th>stage</th><th>outcome</th>"
                        + "<th class=\"num\">attempts</th><th class=\"num\">duration</th>"
                        + "</tr></thead><tbody>");

        foreach (StageTiming stage in metrics.Stages)
        {
            page.AppendLine(CultureInfo.InvariantCulture,
                $"<tr><td class=\"mono\">{H(stage.NodeId.Value)}</td>"
                + $"<td>{NodeStatePill(stage.State)}</td>"
                + $"<td class=\"num\">{stage.Attempts}</td>"
                + $"<td class=\"num\">{Duration(stage.Duration)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    private static void HumanDecisions(StringBuilder page, ImmutableArray<RunEvent> events)
    {
        ImmutableArray<RunEvent> decisions =
        [
            .. events.Where(@event => @event.Kind is RunEventKind.ApprovalRequested
                or RunEventKind.ApprovalGranted or RunEventKind.ApprovalDenied
                or RunEventKind.PolicyWaiverGranted or RunEventKind.PolicyWaiverDenied
                or RunEventKind.RunAmended),
        ];

        page.AppendLine("<h2>Where a human intervened</h2>");

        if (decisions.IsEmpty)
        {
            page.AppendLine(
                """<p class="muted">No human decision was recorded for this run.</p>""");
            return;
        }

        page.AppendLine("<table><thead><tr><th>when</th><th>what</th><th>who</th>"
                        + "<th>detail</th></tr></thead><tbody>");

        foreach (RunEvent @event in decisions)
        {
            (string what, string detail) = Describe(@event);

            page.AppendLine(CultureInfo.InvariantCulture,
                $"<tr><td class=\"mono\">{H(@event.OccurredAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}</td>"
                + $"<td>{what}</td>"
                + $"<td class=\"mono\">{H(@event.Actor.Value)}</td>"
                + $"<td>{H(detail)}</td></tr>");
        }

        page.AppendLine("</tbody></table>");
    }

    private static (string What, string Detail) Describe(RunEvent @event) => @event.Kind switch
    {
        RunEventKind.ApprovalRequested =>
            ("<span class=\"pill info\">asked</span>",
                $"{@event.Payload<ApprovalRequestedPayload>().Role}: "
                + @event.Payload<ApprovalRequestedPayload>().Reason),

        RunEventKind.ApprovalGranted =>
            ("<span class=\"pill ok\">approved</span>",
                $"{@event.Payload<ApprovalDecidedPayload>().Role}: "
                + @event.Payload<ApprovalDecidedPayload>().Note),

        RunEventKind.ApprovalDenied =>
            ("<span class=\"pill bad\">refused</span>",
                $"{@event.Payload<ApprovalDecidedPayload>().Role}: "
                + @event.Payload<ApprovalDecidedPayload>().Note),

        RunEventKind.PolicyWaiverGranted =>
            ("<span class=\"pill warn\">waived</span>",
                $"{@event.Payload<PolicyWaiverPayload>().RuleId}: "
                + @event.Payload<PolicyWaiverPayload>().Reason),

        RunEventKind.PolicyWaiverDenied =>
            ("<span class=\"pill neutral\">waiver withdrawn</span>",
                @event.Payload<PolicyWaiverPayload>().RuleId),

        RunEventKind.RunAmended =>
            ("<span class=\"pill warn\">amended</span>",
                $"{@event.NodeId?.Value}: {@event.Payload<AmendmentPayload>().Reason}"),

        _ => (@event.Kind.ToString(), string.Empty),
    };

    private static void Policy(StringBuilder page, ImmutableArray<RunEvent> events)
    {
        RunEvent? latest = events
            .Where(@event => @event.Kind == RunEventKind.PolicyEvaluated)
            .LastOrDefault();

        page.AppendLine("<h2>Policy</h2>");

        if (latest is null)
        {
            page.AppendLine("""<p class="muted">No policy pack was evaluated.</p>""");
            return;
        }

        foreach (RunEvent evaluation in events
                     .Where(@event => @event.Kind == RunEventKind.PolicyEvaluated)
                     .GroupBy(@event => @event.Payload<PolicyEvaluatedPayload>().Pack)
                     .Select(group => group.Last()))
        {
            PolicyEvaluatedPayload payload = evaluation.Payload<PolicyEvaluatedPayload>();

            page.AppendLine(CultureInfo.InvariantCulture,
                $"<h3>{H(payload.Pack)} {(payload.Clean ? Pill("ok", "clean") : Pill("bad", "blocking"))}</h3>");

            page.AppendLine("<table><thead><tr><th>rule</th><th>verdict</th>"
                            + "<th>evidence</th></tr></thead><tbody>");

            foreach (PolicyVerdictPayload verdict in payload.Verdicts)
            {
                string pill = verdict switch
                {
                    { Satisfied: true } => Pill("ok", "satisfied"),
                    { Waived: true } => Pill("warn", "waived"),
                    { Severity: "Advisory" } => Pill("neutral", "advisory"),
                    _ => Pill("bad", "blocking"),
                };

                page.AppendLine(CultureInfo.InvariantCulture,
                    $"<tr><td class=\"mono\">{H(verdict.RuleId)}</td><td>{pill}</td>"
                    + $"<td>{H(verdict.Explanation)}</td></tr>");
            }

            page.AppendLine("</tbody></table>");
        }
    }

    private static void Replans(StringBuilder page, ImmutableArray<RunEvent> events)
    {
        ImmutableArray<RunEvent> replans =
        [
            .. events.Where(@event => @event.Kind is RunEventKind.ReplanPerformed
                or RunEventKind.ReplanRefused),
        ];

        if (replans.IsEmpty)
        {
            return;
        }

        page.AppendLine("<h2>Re-planning</h2>");
        page.AppendLine(
            """<p class="muted">What each re-plan undid, and what it deliberately left """
            + "alone because its input had not changed.</p>");

        foreach (RunEvent @event in replans)
        {
            ReplanPayload payload = @event.Payload<ReplanPayload>();
            bool refused = @event.Kind == RunEventKind.ReplanRefused;

            page.AppendLine("<div class=\"decision\">");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"<div class=\"q\">{(refused ? Pill("neutral", "refused") : Pill("info", $"re-plan {payload.ReplanNumber}"))} "
                + $"triggered by <span class=\"mono\">{H(payload.Trigger)}</span>, "
                + $"by <span class=\"mono\">{H(@event.Actor.Value)}</span></div>");
            page.AppendLine(CultureInfo.InvariantCulture, $"<p>{H(payload.Reason)}</p>");
            page.AppendLine("<ul>");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"<li><strong>Redone:</strong> {H(Join(payload.Invalidated))}</li>");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"<li><strong>Left alone:</strong> {H(Join(payload.Unaffected))}</li>");

            if (!payload.ApprovalsRevoked.IsEmpty)
            {
                page.AppendLine(CultureInfo.InvariantCulture,
                    $"<li><strong>Approvals withdrawn:</strong> {H(Join(payload.ApprovalsRevoked))}</li>");
            }

            page.AppendLine("</ul></div>");
        }
    }

    private static void Decisions(StringBuilder page, ImmutableArray<RunEvent> events)
    {
        ImmutableArray<RunEvent> decisions =
            [.. events.Where(@event => @event.Kind == RunEventKind.DecisionRecorded)];

        page.AppendLine("<h2>Decisions</h2>");

        if (decisions.IsEmpty)
        {
            page.AppendLine("""<p class="muted">No design decision was recorded.</p>""");
            return;
        }

        foreach (RunEvent @event in decisions)
        {
            DecisionRecordedPayload payload = @event.Payload<DecisionRecordedPayload>();

            page.AppendLine("<div class=\"decision\">");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"<div class=\"q\">{H(payload.Question)}</div>");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"""<p><strong>{H(payload.Chosen)}</strong>: {H(payload.Rationale)}</p>""");
            page.AppendLine(CultureInfo.InvariantCulture,
                $"""<p class="muted">by {H(@event.Actor.Value)}, confidence """
                + $"{payload.Confidence.ToString("0.##", CultureInfo.InvariantCulture)}</p>");

            page.AppendLine("<ul>");

            foreach (DecisionOptionPayload option in payload.Options
                         .Where(option => option.RejectedBecause is not null))
            {
                page.AppendLine(CultureInfo.InvariantCulture,
                    $"<li>Rejected <strong>{H(option.Name)}</strong>: {H(option.RejectedBecause!)}</li>");
            }

            page.AppendLine("</ul></div>");
        }
    }

    private static void EventLog(StringBuilder page, ImmutableArray<RunEvent> events)
    {
        page.AppendLine("<h2>Audit log</h2>");
        page.AppendLine(CultureInfo.InvariantCulture,
            $"<details><summary>{events.Length} events, each committing to its predecessor</summary>");

        page.AppendLine("<table><thead><tr><th class=\"num\">#</th><th>event</th><th>stage</th>"
                        + "<th>actor</th><th>digest</th></tr></thead><tbody>");

        foreach (RunEvent @event in events)
        {
            page.AppendLine(CultureInfo.InvariantCulture,
                $"<tr><td class=\"num\">{@event.Sequence}</td><td>{H(@event.Kind.ToString())}</td>"
                + $"<td class=\"mono\">{H(@event.NodeId?.Value ?? "&ndash;")}</td>"
                + $"<td class=\"mono\">{H(@event.Actor.Value)}</td>"
                + $"<td class=\"mono\">{H(@event.Hash.Abbreviated)}</td></tr>");
        }

        page.AppendLine("</tbody></table></details>");
    }

    private static void Footer(StringBuilder page, AuditVerification verification)
    {
        // Concatenation would bind to the wrong StringBuilder overload, so the whole
        // sentence is one interpolated string.
        string footnote =
            $"""<p class="footnote">{H(verification.Summary)} Every figure in this report is derived from that log rather than recorded alongside it, so the report cannot say anything the log does not support. Generated by {H(Core.Diagnostics.BuildInfo.Fingerprint)}.</p>""";

        page.AppendLine(footnote);
    }

    private static void Card(StringBuilder page, string label, string value, string note)
    {
        page.AppendLine(CultureInfo.InvariantCulture,
            $"""<div class="card"><div class="label">{H(label)}</div>"""
            + $"""<div class="value">{value}</div><div class="note">{H(note)}</div></div>""");
    }

    private static string Pill(string kind, string text) =>
        $"<span class=\"pill {kind}\">{H(text)}</span>";

    private static string Status(RunStatus status) => status switch
    {
        RunStatus.Succeeded => Pill("ok", "succeeded"),
        RunStatus.AwaitingApproval => Pill("warn", "awaiting approval"),
        RunStatus.Blocked => Pill("warn", "blocked"),
        RunStatus.SafeStopped => Pill("warn", "safe-stopped"),
        RunStatus.RolledBack => Pill("bad", "rolled back"),
        RunStatus.Failed => Pill("bad", "failed"),
        _ => Pill("neutral", status.ToString().ToLowerInvariant()),
    };

    private static string NodeStatePill(NodeState state) => state switch
    {
        NodeState.Succeeded => Pill("ok", "succeeded"),
        NodeState.Skipped => Pill("neutral", "not required"),
        NodeState.AwaitingApproval => Pill("warn", "awaiting approval"),
        NodeState.Blocked => Pill("warn", "blocked"),
        NodeState.RolledBack => Pill("bad", "rolled back"),
        NodeState.Failed => Pill("bad", "failed"),
        _ => Pill("neutral", state.ToString().ToLowerInvariant()),
    };

    private static string Chain(AuditVerification verification) =>
        verification.IsIntact ? Pill("ok", "chain intact") : Pill("bad", "chain broken");

    private static string Join(ImmutableArray<string> values) =>
        values.IsEmpty ? "nothing" : string.Join(", ", values);

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#}h"
        : duration.TotalMinutes >= 1 ? $"{duration.TotalMinutes:0.#}m"
        : duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.##}s"
        : $"{duration.TotalMilliseconds:0}ms";

    private static string H(string? text) => (text ?? string.Empty)
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
