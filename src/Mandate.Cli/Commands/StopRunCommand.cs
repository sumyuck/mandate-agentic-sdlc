using System.ComponentModel;
using Mandate.Core.Identifiers;
using Mandate.Persistence;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Asks a run to stop at its next safe boundary.
/// </summary>
/// <remarks>
/// The request is recorded on disk rather than sent as a signal, because the operator asking
/// is usually in a different process from the run. It also means a stop that arrives while
/// nothing is running still applies when the run next executes — a stop that quietly expired
/// would be worse than no stop at all.
/// </remarks>
internal sealed class StopRunCommand : AsyncCommand<StopRunCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to stop.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("--as")]
        [Description("Who is asking. Recorded with the request.")]
        public string RequestedBy { get; init; } = Environment.UserName;

        [CommandOption("--clear")]
        [Description("Withdraw a stop request instead of making one.")]
        public bool Clear { get; init; }
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

        FileSafeStopMonitor monitor = new(FileSafeStopMonitor.DefaultDirectory);

        if (settings.Clear)
        {
            monitor.Clear(runId);
            AnsiConsole.MarkupLine($"[green]withdrawn[/] stop request for {runId.Value.EscapeMarkup()}");
            return ExitCode.Success;
        }

        await monitor.RequestAsync(runId, settings.RequestedBy, cancellationToken)
            .ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            $"[yellow]stop requested[/] for {runId.Value.EscapeMarkup()} — the run will halt at "
            + "its next safe boundary with completed work preserved.");

        return ExitCode.Success;
    }
}
