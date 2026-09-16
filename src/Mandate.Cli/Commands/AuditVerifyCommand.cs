using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Persistence.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Proves that a run's audit log has not been altered since it was written.
/// </summary>
/// <remarks>
/// <para>
/// This is the command that turns the log from a record into evidence. Every event commits to
/// its predecessor's digest, and its own digest covers the whole envelope including that
/// link, so an edit, deletion, reordering, duplication, backdating or splice breaks the chain
/// at a point this command can name.
/// </para>
/// <para>
/// It reports every defect rather than stopping at the first, because an auditor needs the
/// shape of the damage and not only its earliest symptom.
/// </para>
/// </remarks>
internal sealed class AuditVerifyCommand : AsyncCommand<AuditVerifyCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "[runId]")]
        [Description("The run to verify. Omit to verify every run in the store.")]
        public string? RunId { get; init; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);

        ImmutableArray<RunId> runIds;

        if (settings.RunId is null)
        {
            runIds =
            [
                .. (await journal.ListAsync(int.MaxValue, cancellationToken).ConfigureAwait(false))
                    .Select(run => run.RunId),
            ];
        }
        else if (RunId.TryParse(settings.RunId, out RunId parsed))
        {
            runIds = [parsed];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]'{settings.RunId.EscapeMarkup()}' is not a run id.[/]");
            return 2;
        }

        if (runIds.IsEmpty)
        {
            AnsiConsole.MarkupLine("[grey]No runs recorded yet.[/]");
            return 0;
        }

        int broken = 0;

        foreach (RunId runId in runIds)
        {
            ImmutableArray<RunEvent> events =
                await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

            AuditVerification verification = AuditChain.Verify(runId, events);

            if (verification.IsIntact)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] {verification.Summary.EscapeMarkup()}");
                continue;
            }

            broken++;
            AnsiConsole.MarkupLine($"[red]✗[/] {verification.Summary.EscapeMarkup()}");

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]at[/]", column => column.RightAligned())
                .AddColumn("[bold]defect[/]")
                .AddColumn("[bold]detail[/]");

            foreach (AuditChainBreak defect in verification.Breaks)
            {
                table.AddRow(
                    defect.Sequence.ToString(CultureInfo.InvariantCulture),
                    defect.Reason.ToString(),
                    defect.Detail.EscapeMarkup());
            }

            AnsiConsole.Write(table);
        }

        if (broken == 0)
        {
            AnsiConsole.MarkupLine(
                $"[green]{runIds.Length} run(s) verified.[/] "
                + "[grey]Each event commits to its predecessor, so any edit, deletion or "
                + "reordering would have been detected.[/]");

            return 0;
        }

        AnsiConsole.MarkupLine($"[red]{broken} of {runIds.Length} run(s) failed verification.[/]");
        return 1;
    }
}
