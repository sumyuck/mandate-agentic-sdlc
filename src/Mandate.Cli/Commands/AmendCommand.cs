using System.Collections.Immutable;
using System.ComponentModel;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Persistence.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Records that an input the run already acted on has changed.
/// </summary>
/// <remarks>
/// <para>
/// This is the human half of dynamic re-planning. Amending a stage says the work done from
/// that point was based on a premise that no longer holds — so it is discarded and redone,
/// and any approval given for it stops counting.
/// </para>
/// <para>
/// The amendment is recorded but not acted on here. Deciding and executing stay separate
/// commands, and the run picks it up when it resumes.
/// </para>
/// </remarks>
internal sealed class AmendCommand : AsyncCommand<AmendCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run whose input changed.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("--stage")]
        [Description("The stage to redo. Everything built on it is redone too.")]
        public string Stage { get; init; } = "requirements";

        [CommandOption("--as")]
        [Description("The human amending. Recorded with the amendment.")]
        public string As { get; init; } = Environment.UserName;

        [CommandOption("--reason")]
        [Description("What changed, and why the earlier work no longer stands. Required.")]
        public string Reason { get; init; } = string.Empty;
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!RunId.TryParse(settings.RunId, out RunId runId))
        {
            AnsiConsole.MarkupLine($"[red]'{settings.RunId.EscapeMarkup()}' is not a run id.[/]");
            return ExitCode.BadInput;
        }

        if (!NodeId.TryParse(settings.Stage, out NodeId nodeId))
        {
            AnsiConsole.MarkupLine($"[red]'{settings.Stage.EscapeMarkup()}' is not a stage id.[/]");
            return ExitCode.BadInput;
        }

        if (string.IsNullOrWhiteSpace(settings.Reason))
        {
            // An amendment discards completed work. The record has to say what justified it.
            AnsiConsole.MarkupLine(
                "[red]an amendment needs a reason[/]: pass --reason. This discards work that "
                + "was already done, and the record has to say why.");

            return ExitCode.BadInput;
        }

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);

        ImmutableArray<RunEvent> events =
            await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

        if (events.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[red]No run {runId.Value.EscapeMarkup()} in this store.[/]");
            return ExitCode.BadInput;
        }

        RunState state = RunState.Rebuild(runId, events);
        NodeState current = state.StateOf(nodeId);

        if (current == NodeState.Unknown)
        {
            AnsiConsole.MarkupLine(
                $"[red]'{nodeId.Value.EscapeMarkup()}' is not a stage of this run.[/]");

            return ExitCode.BadInput;
        }

        if (current is NodeState.Pending or NodeState.Ready or NodeState.Running)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]'{nodeId.Value.EscapeMarkup()}' has not produced anything yet[/] "
                + $"(it is {current.ToString().ToLowerInvariant()}), so there is nothing to redo. "
                + "Amend it after it has run.");

            return ExitCode.BadInput;
        }

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous,
                runId,
                DateTimeOffset.UtcNow,
                RunEventKind.RunAmended,
                nodeId,
                Actor.Human(settings.As),
                new AmendmentPayload(settings.Reason)),
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[yellow]amended[/] '{nodeId.Value.EscapeMarkup()}' on {runId.Value.EscapeMarkup()} "
            + $"as {Actor.Human(settings.As).Value.EscapeMarkup()}");

        AnsiConsole.MarkupLine(
            "[grey]on resume, that stage and everything built on it will be redone, and any "
            + "approval given for them stops counting[/]");

        AnsiConsole.MarkupLine(
            $"[grey]continue with `mandate resume {runId.Value}`[/]");

        return ExitCode.Success;
    }
}
