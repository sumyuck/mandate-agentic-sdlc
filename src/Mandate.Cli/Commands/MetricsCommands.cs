using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Serialization;
using Mandate.Observability.Metrics;
using Mandate.Observability.Reporting;
using Mandate.Persistence.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Reports a run's reliability figures.
/// </summary>
/// <remarks>
/// Every number is derived from the event log rather than stored, so it cannot be set — only
/// caused — and anyone holding the exported log can recompute it and get the same answer.
/// </remarks>
internal sealed class MetricsCommand : AsyncCommand<MetricsCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to measure.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a table.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        (RunId runId, ImmutableArray<RunEvent> events, int failure) =
            await RunReader.ReadAsync(settings.RunId, settings.Store, cancellationToken)
                .ConfigureAwait(false);

        if (failure != ExitCode.Success)
        {
            return failure;
        }

        RunMetrics metrics = MetricsCalculator.Compute(runId, events);

        if (settings.Json)
        {
            // Straight to stdout: the renderer wraps, which would corrupt the document.
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    runId = metrics.RunId.Value,
                    status = metrics.Status.ToString(),
                    endToEndSeconds = metrics.EndToEnd.TotalSeconds,
                    successRate = metrics.SuccessRate,
                    retryRate = metrics.RetryRate,
                    rollbackRate = metrics.RollbackRate,
                    gateBlockRate = metrics.GateBlockRate,
                    autonomyRatio = metrics.AutonomyRatio,
                    meanTimeToRecoverySeconds = metrics.MeanTimeToRecovery?.TotalSeconds,
                    unrecoveredFailures = metrics.UnrecoveredFailures,
                    meanApprovalWaitSeconds = metrics.MeanApprovalWait?.TotalSeconds,
                    stageMedianSeconds = metrics.MedianStageDuration.TotalSeconds,
                    stageP95Seconds = metrics.NinetyFifthStageDuration.TotalSeconds,
                    stages = metrics.Stages.Length,
                    succeeded = metrics.Succeeded,
                    skipped = metrics.Skipped,
                    failed = metrics.Failed,
                    rolledBack = metrics.RolledBack,
                    attempts = metrics.Attempts,
                    retries = metrics.Retries,
                    compensations = metrics.Compensations,
                    approvalsGranted = metrics.ApprovalsGranted,
                    approvalsDenied = metrics.ApprovalsDenied,
                    policyViolationsBlocked = metrics.PolicyViolationsBlocked,
                    policyWaiversGranted = metrics.PolicyWaiversGranted,
                    replansPerformed = metrics.ReplansPerformed,
                    replansRefused = metrics.ReplansRefused,
                    eventCount = metrics.EventCount,
                },
                MandateJson.Pretty));

            return ExitCode.Success;
        }

        AnsiConsole.Write(new Rule($"[bold]{runId.Value.EscapeMarkup()}[/]").LeftJustified());

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]measure[/]")
            .AddColumn("[bold]value[/]", column => column.RightAligned())
            .AddColumn("[bold]basis[/]");

        void Row(string measure, string value, string basis) =>
            table.AddRow(measure.EscapeMarkup(), value.EscapeMarkup(), basis.EscapeMarkup());

        Row("success rate", Percent(metrics.SuccessRate),
            $"{metrics.Succeeded} of {metrics.Succeeded + metrics.Failed + metrics.RolledBack} attempted");
        Row("end to end", Duration(metrics.EndToEnd), $"{metrics.EventCount} events");
        Row("stage p50 / p95",
            $"{Duration(metrics.MedianStageDuration)} / {Duration(metrics.NinetyFifthStageDuration)}",
            $"{metrics.Stages.Length} stage(s) run, {metrics.Skipped} not required");
        Row("retry rate", Percent(metrics.RetryRate),
            $"{metrics.Retries} of {metrics.Attempts} attempts");
        Row("mean time to recovery",
            metrics.MeanTimeToRecovery is { } mttr ? Duration(mttr) : "-",
            metrics.UnrecoveredFailures > 0
                ? $"{metrics.UnrecoveredFailures} failure(s) never recovered"
                : $"{metrics.Failures.Length} failure(s)");
        Row("rollback rate", Percent(metrics.RollbackRate),
            $"{metrics.Compensations} compensation(s)");
        Row("gate block rate", Percent(metrics.GateBlockRate),
            $"{metrics.GateConditionsFailed} of {metrics.GateConditionsEvaluated} conditions");
        Row("approval wait",
            metrics.MeanApprovalWait is { } wait ? Duration(wait) : "-",
            $"{metrics.ApprovalsGranted} granted, {metrics.ApprovalsDenied} refused");
        Row("autonomy ratio", Percent(metrics.AutonomyRatio),
            $"{metrics.Attempts} agent attempt(s) against human decisions");
        Row("re-plans", metrics.ReplansPerformed.ToString(CultureInfo.InvariantCulture),
            metrics.ReplansRefused > 0 ? $"{metrics.ReplansRefused} refused" : "plan recomputed");
        Row("policy", metrics.PolicyEvaluations.ToString(CultureInfo.InvariantCulture),
            $"{metrics.PolicyViolationsBlocked} blocked, {metrics.PolicyWaiversGranted} waived");

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[grey]Derived from the run's event log. Nothing here is stored, so nothing here "
            + "can be set, only caused.[/]");

        return ExitCode.Success;
    }

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Duration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#}h"
        : duration.TotalMinutes >= 1 ? $"{duration.TotalMinutes:0.#}m"
        : duration.TotalSeconds >= 1 ? $"{duration.TotalSeconds:0.##}s"
        : $"{duration.TotalMilliseconds:0}ms";
}

/// <summary>Writes a run as a self-contained HTML page.</summary>
internal sealed class ReportCommand : AsyncCommand<ReportCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to report on.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("-o|--output")]
        [Description("Where to write the page. Defaults to runs/<runId>/report.html.")]
        public string? Output { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        (RunId runId, ImmutableArray<RunEvent> events, int failure) =
            await RunReader.ReadAsync(settings.RunId, settings.Store, cancellationToken)
                .ConfigureAwait(false);

        if (failure != ExitCode.Success)
        {
            return failure;
        }

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);
        RunSummary summary = (await journal.FindAsync(runId, cancellationToken).ConfigureAwait(false))!;

        string html = HtmlRunReport.Render(
            events,
            MetricsCalculator.Compute(runId, events),
            AuditChain.Verify(runId, events),
            summary.Request,
            summary.Workflow,
            summary.Scenario);

        string output = settings.Output
                        ?? Path.Combine("runs", runId.Value, "report.html");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(output));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(output, html, cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[green]wrote[/] {output.EscapeMarkup()} "
            + $"[grey]({html.Length / 1024}kB, self-contained, no network, no script)[/]");

        return ExitCode.Success;
    }
}

/// <summary>Reads a run's events, reporting the usual input failures consistently.</summary>
internal static class RunReader
{
    public static async Task<(RunId RunId, ImmutableArray<RunEvent> Events, int Failure)> ReadAsync(
        string rawRunId, string store, CancellationToken cancellationToken)
    {
        if (!RunId.TryParse(rawRunId, out RunId runId))
        {
            AnsiConsole.MarkupLine($"[red]'{rawRunId.EscapeMarkup()}' is not a run id.[/]");
            return (default, [], ExitCode.BadInput);
        }

        using SqliteRunJournal journal = SqliteRunJournal.Open(store);

        ImmutableArray<RunEvent> events =
            await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

        if (events.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[red]No run {runId.Value.EscapeMarkup()} in this store.[/]");
            return (runId, [], ExitCode.BadInput);
        }

        return (runId, events, ExitCode.Success);
    }
}
