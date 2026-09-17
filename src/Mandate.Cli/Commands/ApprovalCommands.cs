using System.Collections.Immutable;
using System.ComponentModel;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Persistence.Sqlite;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>Settings shared by approving and denying.</summary>
internal abstract class ApprovalSettings : StoreSettings
{
    [CommandArgument(0, "<runId>")]
    [Description("The run holding the parked stage.")]
    public string RunId { get; init; } = string.Empty;

    [CommandOption("-r|--role")]
    [Description("The role to decide in, for example tech-lead.")]
    public string Role { get; init; } = string.Empty;

    [CommandOption("--as")]
    [Description("The human deciding. Recorded on the run and checked for conflicts.")]
    public string As { get; init; } = Environment.UserName;

    [CommandOption("-n|--note")]
    [Description("Why. Recorded with the decision.")]
    public string Note { get; init; } = string.Empty;
}

/// <summary>
/// Records a human approval against a parked stage.
/// </summary>
/// <remarks>
/// <para>
/// The checks here are the point. An approval is refused unless some stage actually asked for
/// that role, and refused outright if the approver produced the work — segregation of duties
/// enforced at the moment of the decision, where a person can be told why, rather than only
/// at the gate where it would read as an unexplained failure.
/// </para>
/// <para>
/// Approving does not continue the run. Deciding and executing are separate acts by different
/// parties, and keeping the commands separate keeps that visible in the audit log.
/// </para>
/// </remarks>
internal sealed class ApproveCommand : AsyncCommand<ApproveCommand.Settings>
{
    internal sealed class Settings : ApprovalSettings;

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return await ApprovalDecision
            .RecordAsync(settings, granted: true, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Records a human refusal against a parked stage.</summary>
internal sealed class DenyCommand : AsyncCommand<DenyCommand.Settings>
{
    internal sealed class Settings : ApprovalSettings;

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.Note))
        {
            // A refusal with no stated reason cannot be acted on by whoever has to fix it.
            AnsiConsole.MarkupLine(
                "[red]a refusal needs a reason[/]: pass --note. Whoever has to act on this "
                + "needs to know what was wrong.");

            return ExitCode.BadInput;
        }

        return await ApprovalDecision
            .RecordAsync(settings, granted: false, cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>The shared mechanics of recording a human decision.</summary>
internal static class ApprovalDecision
{
    public static async Task<int> RecordAsync(
        ApprovalSettings settings, bool granted, CancellationToken cancellationToken)
    {
        if (!RunId.TryParse(settings.RunId, out RunId runId))
        {
            AnsiConsole.MarkupLine($"[red]'{settings.RunId.EscapeMarkup()}' is not a run id.[/]");
            return ExitCode.BadInput;
        }

        if (string.IsNullOrWhiteSpace(settings.Role))
        {
            AnsiConsole.MarkupLine("[red]--role is required[/]: an approval is always in a role.");
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

        ImmutableArray<ApprovalRequestedPayload> pending =
        [
            .. events
                .Where(@event => @event.Kind == RunEventKind.ApprovalRequested)
                .Select(@event => @event.Payload<ApprovalRequestedPayload>())
                .Where(payload => string.Equals(
                    payload.Role, settings.Role, StringComparison.OrdinalIgnoreCase)),
        ];

        if (pending.IsEmpty)
        {
            IEnumerable<string> asked = events
                .Where(@event => @event.Kind == RunEventKind.ApprovalRequested)
                .Select(@event => @event.Payload<ApprovalRequestedPayload>().Role)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            AnsiConsole.MarkupLine(
                $"[red]no stage asked for '{settings.Role.EscapeMarkup()}'[/]. "
                + (asked.Any()
                    ? $"Requested roles: {string.Join(", ", asked).EscapeMarkup()}."
                    : "This run has requested no approvals."));

            return ExitCode.BadInput;
        }

        Actor decider = Actor.Human(settings.As);

        if (granted && Conflict(pending, decider, state) is { } conflict)
        {
            AnsiConsole.MarkupLine(
                $"[red]refused[/] {conflict.EscapeMarkup()}");

            return ExitCode.BadInput;
        }

        NodeId? node = FindParkedNode(state, events, settings.Role);

        await journal.AppendAsync(
            runId,
            previous => RunEvent.Append(
                previous,
                runId,
                DateTimeOffset.UtcNow,
                granted ? RunEventKind.ApprovalGranted : RunEventKind.ApprovalDenied,
                node,
                decider,
                new ApprovalDecidedPayload(settings.Role, settings.Note)),
            cancellationToken).ConfigureAwait(false);

        AnsiConsole.MarkupLine(
            granted
                ? $"[green]approved[/] '{settings.Role.EscapeMarkup()}' on "
                  + $"{runId.Value.EscapeMarkup()} as {decider.Value.EscapeMarkup()}"
                : $"[yellow]denied[/] '{settings.Role.EscapeMarkup()}' on "
                  + $"{runId.Value.EscapeMarkup()} as {decider.Value.EscapeMarkup()}");

        AnsiConsole.MarkupLine(
            $"[grey]the decision is recorded; continue the run with "
            + $"`mandate resume {runId.Value}`[/]");

        return ExitCode.Success;
    }

    /// <summary>
    /// Reports a segregation-of-duties conflict.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two separations, and the second is the one that bites. The approver must not be the
    /// participant that produced the work — which in an agent-driven lifecycle is almost
    /// always an agent, so on its own that check would be close to vacuous.
    /// </para>
    /// <para>
    /// The control that matters here is that the human who <em>asked</em> for the work cannot
    /// also be the one who signs it off. Agents do the producing, but a person requested it,
    /// and letting the requester approve their own request is exactly the maker-checker
    /// failure this rule exists to prevent.
    /// </para>
    /// <para>
    /// Roles the requester is meant to answer — a clarification put back to them — declare
    /// <c>segregation-of-duties: false</c>, because there the requester is precisely the right
    /// person and refusing them would make the stage unanswerable.
    /// </para>
    /// </remarks>
    private static string? Conflict(
        ImmutableArray<ApprovalRequestedPayload> pending, Actor decider, RunState state)
    {
        Actor? initiator =
            Actor.TryParse(
                state.Context.Latest(WorkflowContextKeys.InitiatedBy)?.Value ?? string.Empty,
                out Actor parsed)
                ? parsed
                : null;

        foreach (ApprovalRequestedPayload request in pending)
        {
            if (!request.SegregationOfDuties)
            {
                continue;
            }

            if (request.ProducedBy is { } producedBy
                && Actor.TryParse(producedBy, out Actor producer)
                && producer.IsSameParticipantAs(decider))
            {
                return $"{decider} produced this work and cannot also approve it in role "
                       + $"'{request.Role}'. Segregation of duties applies.";
            }

            if (initiator is { } requester && requester.IsSameParticipantAs(decider))
            {
                return $"{decider} requested this run and cannot also approve it in role "
                       + $"'{request.Role}'. Segregation of duties applies: the person who asks "
                       + "for the work is not the person who signs it off.";
            }
        }

        return null;
    }

    private static NodeId? FindParkedNode(
        RunState state, ImmutableArray<RunEvent> events, string role) =>
        events
            .Where(@event =>
                @event.Kind == RunEventKind.ApprovalRequested
                && @event.NodeId is not null
                && string.Equals(
                    @event.Payload<ApprovalRequestedPayload>().Role,
                    role,
                    StringComparison.OrdinalIgnoreCase))
            .Select(@event => @event.NodeId)
            .FirstOrDefault(nodeId =>
                nodeId is not null && state.StateOf(nodeId.Value) == NodeState.AwaitingApproval);
}
