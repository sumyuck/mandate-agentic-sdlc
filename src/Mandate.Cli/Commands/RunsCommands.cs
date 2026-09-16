using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Persistence;
using Mandate.Persistence.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>Settings shared by every command that reads the run store.</summary>
internal abstract class StoreSettings : CommandSettings
{
    [CommandOption("--store")]
    [Description("Path to the run store. Defaults to .mandate/runs.db.")]
    public string Store { get; init; } = SqliteRunJournal.DefaultPath;
}

/// <summary>Lists persisted runs.</summary>
internal sealed class ListRunsCommand : AsyncCommand<ListRunsCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandOption("-n|--limit")]
        [Description("How many runs to show. Defaults to 20.")]
        public int Limit { get; init; } = 20;

        [CommandOption("--ids")]
        [Description("Print only run ids, one per line, for scripting.")]
        public bool IdsOnly { get; init; }

        [CommandOption("--json")]
        [Description("Emit machine-readable JSON instead of a table.")]
        public bool Json { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);

        ImmutableArray<RunSummary> runs =
            await journal.ListAsync(settings.Limit, cancellationToken).ConfigureAwait(false);

        if (settings.IdsOnly)
        {
            // Straight to stdout: the console renderer wraps at terminal width, which would
            // split a run id across lines and make it unusable in a pipeline.
            foreach (RunSummary run in runs)
            {
                Console.Out.WriteLine(run.RunId.Value);
            }

            return 0;
        }

        if (settings.Json)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(
                runs.Select(run => new
                {
                    runId = run.RunId.Value,
                    workflow = run.Workflow,
                    scenario = run.Scenario,
                    status = run.Status.ToString(),
                    request = run.Request,
                    startedAt = run.StartedAt,
                    updatedAt = run.UpdatedAt,
                    eventCount = run.EventCount,
                    waitingOnHuman = run.IsWaitingOnHuman,
                }),
                Core.Serialization.MandateJson.Pretty));

            return 0;
        }

        if (runs.IsEmpty)
        {
            AnsiConsole.MarkupLine("[grey]No runs recorded yet.[/]");
            return 0;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]run[/]")
            .AddColumn("[bold]scenario[/]")
            .AddColumn("[bold]status[/]")
            .AddColumn("[bold]events[/]", column => column.RightAligned())
            .AddColumn("[bold]request[/]");

        foreach (RunSummary run in runs)
        {
            table.AddRow(
                run.RunId.Value.EscapeMarkup(),
                run.Scenario.ToLowerInvariant().EscapeMarkup(),
                RunStatusMarkup.For(run.Status),
                run.EventCount.ToString(CultureInfo.InvariantCulture),
                Shorten(run.Request).EscapeMarkup());
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static string Shorten(string request) =>
        request.Length <= 56 ? request : request[..53] + "...";
}

/// <summary>Rebuilds a run from its persisted log and shows where it stands.</summary>
/// <remarks>
/// The output is produced by folding the same reducer the engine uses over the stored events,
/// so what is shown here is what the engine would resume from — not a separate summary that
/// could disagree with it.
/// </remarks>
internal sealed class ShowRunCommand : AsyncCommand<ShowRunCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to show.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("--events")]
        [Description("Show the full audit log instead of the stage summary.")]
        public bool ShowEvents { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!RunId.TryParse(settings.RunId, out RunId runId))
        {
            AnsiConsole.MarkupLine($"[red]'{settings.RunId.EscapeMarkup()}' is not a run id.[/]");
            return 2;
        }

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);

        RunSummary? summary = await journal.FindAsync(runId, cancellationToken).ConfigureAwait(false);

        if (summary is null)
        {
            AnsiConsole.MarkupLine($"[red]No run {runId.Value.EscapeMarkup()} in this store.[/]");
            return 2;
        }

        ImmutableArray<RunEvent> events =
            await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

        RunState state = RunState.Rebuild(runId, events);

        AnsiConsole.Write(new Rule($"[bold]{runId.Value.EscapeMarkup()}[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[grey]{summary.Workflow.EscapeMarkup()} · {summary.Scenario.ToLowerInvariant()} · "
            + $"{summary.EventCount} events[/]");
        AnsiConsole.MarkupLine($"[grey]{summary.Request.EscapeMarkup()}[/]");

        if (settings.ShowEvents)
        {
            RunEventTable.Write(events);
        }
        else
        {
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]stage[/]")
                .AddColumn("[bold]state[/]")
                .AddColumn("[bold]attempts[/]", column => column.RightAligned())
                .AddColumn("[bold]detail[/]");

            foreach (NodeExecutionState node in state.Nodes.Values
                         .OrderBy(node => node.Id.Value, StringComparer.Ordinal))
            {
                table.AddRow(
                    node.Id.Value.EscapeMarkup(),
                    NodeStateMarkup.For(node.State),
                    node.AttemptsMade.ToString(CultureInfo.InvariantCulture),
                    (node.Detail ?? string.Empty).EscapeMarkup());
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine(
            $"{RunStatusMarkup.For(summary.Status)} "
            + $"[grey]{state.Artifacts.Length} artifact(s), {state.Context.Count} context fact(s)[/]");

        return 0;
    }
}

/// <summary>Exports a run's evidence to a reviewable directory.</summary>
internal sealed class ExportRunCommand : AsyncCommand<ExportRunCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to export.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("--to")]
        [Description("Directory to export into. Defaults to runs/.")]
        public string Destination { get; init; } = "runs";
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!RunId.TryParse(settings.RunId, out RunId runId))
        {
            AnsiConsole.MarkupLine($"[red]'{settings.RunId.EscapeMarkup()}' is not a run id.[/]");
            return 2;
        }

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);

        RunSummary? summary = await journal.FindAsync(runId, cancellationToken).ConfigureAwait(false);

        if (summary is null)
        {
            AnsiConsole.MarkupLine($"[red]No run {runId.Value.EscapeMarkup()} in this store.[/]");
            return 2;
        }

        ImmutableArray<RunEvent> events =
            await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

        EvidenceExport export = await RunEvidenceWriter
            .WriteAsync(settings.Destination, summary, events, cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[green]exported[/] {export.EventCount} event(s) to "
            + $"{export.Directory.EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            "[grey]events.jsonl · run.json · timeline.md[/]");

        return export.ChainIntact ? 0 : 1;
    }
}

/// <summary>Shared rendering helpers.</summary>
internal static class RunStatusMarkup
{
    public static string For(RunStatus status) => status switch
    {
        RunStatus.Succeeded => "[green]succeeded[/]",
        RunStatus.AwaitingApproval => "[yellow]awaiting approval[/]",
        RunStatus.Blocked => "[yellow]blocked[/]",
        RunStatus.Running => "[blue]running[/]",
        RunStatus.SafeStopped => "[yellow]safe-stopped[/]",
        RunStatus.Failed => "[red]failed[/]",
        RunStatus.RolledBack => "[red]rolled back[/]",
        _ => $"[grey]{status.ToString().ToLowerInvariant()}[/]",
    };
}

internal static class NodeStateMarkup
{
    public static string For(NodeState state) => state switch
    {
        NodeState.Succeeded => "[green]succeeded[/]",
        NodeState.Skipped => "[grey]skipped[/]",
        NodeState.AwaitingApproval => "[yellow]awaiting approval[/]",
        NodeState.Blocked => "[yellow]blocked[/]",
        NodeState.Failed => "[red]failed[/]",
        NodeState.RolledBack => "[red]rolled back[/]",
        _ => $"[grey]{state.ToString().ToLowerInvariant()}[/]",
    };
}

internal static class RunEventTable
{
    public static void Write(ImmutableArray<RunEvent> events)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]#[/]", column => column.RightAligned())
            .AddColumn("[bold]event[/]")
            .AddColumn("[bold]node[/]")
            .AddColumn("[bold]actor[/]")
            .AddColumn("[bold]digest[/]");

        foreach (RunEvent @event in events)
        {
            table.AddRow(
                @event.Sequence.ToString(CultureInfo.InvariantCulture),
                @event.Kind.ToString(),
                (@event.NodeId?.Value ?? "-").EscapeMarkup(),
                @event.Actor.Value.EscapeMarkup(),
                @event.Hash.Abbreviated);
        }

        AnsiConsole.Write(table);
    }
}
