using System.Collections.Immutable;
using System.ComponentModel;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Persistence.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Lets a human allow a policy violation through, on the record.
/// </summary>
/// <remarks>
/// <para>
/// A waiver does not switch a rule off. The rule is still evaluated and the violation still
/// reported; it simply stops blocking. That is deliberate — the trail has to be able to
/// answer "what did we knowingly let through, and who said so?", and a waiver that suppressed
/// the check would erase exactly the thing an auditor came for.
/// </para>
/// <para>
/// Only blocking rules can be waived, and only ones that exist. Waiving a rule that was
/// already satisfied, or one nobody declared, would put a decision in the record that
/// answered no question.
/// </para>
/// </remarks>
internal sealed class WaiveCommand : AsyncCommand<WaiveCommand.Settings>
{
    internal sealed class Settings : PolicySettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run the waiver applies to.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("-r|--rule")]
        [Description("The rule id to waive, for example CHG-003.")]
        public string Rule { get; init; } = string.Empty;

        [CommandOption("--as")]
        [Description("The human granting it. Recorded with the waiver.")]
        public string As { get; init; } = Environment.UserName;

        [CommandOption("--reason")]
        [Description("Why the violation is acceptable. Required.")]
        public string Reason { get; init; } = string.Empty;

        [CommandOption("--withdraw")]
        [Description("Withdraw a waiver previously granted for this rule.")]
        public bool Withdraw { get; init; }
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

        if (string.IsNullOrWhiteSpace(settings.Rule))
        {
            AnsiConsole.MarkupLine("[red]--rule is required[/]: a waiver applies to one rule.");
            return ExitCode.BadInput;
        }

        if (!settings.Withdraw && string.IsNullOrWhiteSpace(settings.Reason))
        {
            // A waiver with no stated reason is indistinguishable from not having checked.
            AnsiConsole.MarkupLine(
                "[red]a waiver needs a reason[/]: pass --reason. Overriding a control without "
                + "saying why is indistinguishable from not having the control.");

            return ExitCode.BadInput;
        }

        ImmutableArray<PolicyPack> packs;

        try
        {
            packs = Policy.PolicyPackLoader.LoadDirectory(settings.Policies);
        }
        catch (Policy.PolicyFormatException exception)
        {
            AnsiConsole.MarkupLine($"[red]cannot load policies[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.BadInput;
        }

        PolicyRule? rule = packs
            .SelectMany(pack => pack.Rules.Select(candidate => (Pack: pack, Rule: candidate)))
            .Where(entry => string.Equals(
                entry.Rule.Id, settings.Rule, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Rule)
            .FirstOrDefault();

        if (rule is null)
        {
            AnsiConsole.MarkupLine(
                $"[red]no rule '{settings.Rule.EscapeMarkup()}'[/] in the loaded packs. "
                + "Run `mandate policy list` to see them.");

            return ExitCode.BadInput;
        }

        if (rule.Severity != PolicySeverity.Blocking && !settings.Withdraw)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]'{rule.Id.EscapeMarkup()}' is advisory[/] and does not block, so there "
                + "is nothing for a waiver to permit.");

            return ExitCode.BadInput;
        }

        string packName = packs
            .First(pack => pack.Rules.Any(candidate => candidate.Id == rule.Id))
            .Name;

        using SqliteRunJournal journal = SqliteRunJournal.Open(settings.Store);

        ImmutableArray<RunEvent> events =
            await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

        if (events.IsEmpty)
        {
            AnsiConsole.MarkupLine($"[red]No run {runId.Value.EscapeMarkup()} in this store.[/]");
            return ExitCode.BadInput;
        }

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous,
                runId,
                DateTimeOffset.UtcNow,
                settings.Withdraw
                    ? RunEventKind.PolicyWaiverDenied
                    : RunEventKind.PolicyWaiverGranted,
                null,
                Actor.Human(settings.As),
                new PolicyWaiverPayload(rule.Id, packName, settings.Reason)),
            cancellationToken).ConfigureAwait(false);

        if (settings.Withdraw)
        {
            AnsiConsole.MarkupLine(
                $"[green]withdrawn[/] the waiver for {rule.Id.EscapeMarkup()} on "
                + runId.Value.EscapeMarkup());

            return ExitCode.Success;
        }

        AnsiConsole.MarkupLine(
            $"[yellow]waived[/] {rule.Id.EscapeMarkup()} on {runId.Value.EscapeMarkup()} "
            + $"as {Actor.Human(settings.As).Value.EscapeMarkup()}");

        AnsiConsole.MarkupLine(
            $"[grey]{rule.Statement.EscapeMarkup()}[/]");
        AnsiConsole.MarkupLine(
            "[grey]the rule is still evaluated and still reported; it no longer blocks[/]");

        return ExitCode.Success;
    }
}
