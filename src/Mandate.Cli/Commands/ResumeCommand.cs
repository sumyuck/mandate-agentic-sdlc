using System.Collections.Immutable;
using System.ComponentModel;
using Mandate.Agents.Scripted;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Compensation;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Gates;
using Mandate.Persistence;
using Mandate.Persistence.Sqlite;
using Mandate.Persistence.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Continues a run that stopped, from its own recorded log.
/// </summary>
/// <remarks>
/// Both the run's state and its original request are reconstructed from the events, so
/// resuming cannot quietly change what was asked for. Approvals recorded while the run was
/// parked are acted on first, and nothing is re-executed on the strength of a signature: the
/// work was finished before the approval was sought.
/// </remarks>
internal sealed class ResumeCommand : AsyncCommand<ResumeCommand.Settings>
{
    internal sealed class Settings : StoreSettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to continue.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("-w|--workflow")]
        [Description("Workflow file the run executes. Defaults to workflows/sdlc.v1.yaml.")]
        public string Workflow { get; init; } = DefaultWorkflow.Path;

        [CommandOption("--workspace-root")]
        [Description("Where run workspaces live. Defaults to .mandate/workspaces.")]
        public string WorkspaceRoot { get; init; } = GitRunWorkspaceFactory.DefaultRoot;

        [CommandOption("--policies")]
        [Description("Directory holding the policy packs. Defaults to workflows/policies.")]
        public string Policies { get; init; } = PolicyComposition.DefaultDirectory;

        [CommandOption("--template")]
        [Description("Tree new workspaces are seeded from. Defaults to templates/service.")]
        public string Template { get; init; } = GitRunWorkspaceFactory.DefaultTemplate;

        [CommandOption("-c|--max-concurrency")]
        [Description("How many stages may execute at once. Defaults to 4.")]
        public int MaxConcurrency { get; init; } = EngineOptions.Default.MaxConcurrency;
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

        WorkflowGraph graph;

        try
        {
            graph = Workflows.WorkflowYamlLoader.LoadGraph(settings.Workflow);
        }
        catch (Exception exception) when (
            exception is Workflows.WorkflowFormatException or WorkflowValidationException)
        {
            AnsiConsole.MarkupLine($"[red]cannot load workflow[/] {exception.Message.EscapeMarkup()}");
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

        ResumePoint resume;

        try
        {
            resume = ResumePoint.FromEvents(runId, events);
        }
        catch (InvalidOperationException exception)
        {
            AnsiConsole.MarkupLine($"[red]cannot resume[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.BadInput;
        }

        if (!resume.HasOutstandingWork)
        {
            AnsiConsole.MarkupLine(
                $"[green]nothing to do[/] — {runId.Value.EscapeMarkup()} has no outstanding work.");

            return ExitCode.Success;
        }

        // Policies are required only if the lifecycle actually gates on one. A workflow with
        // no policy gate should not be blocked by the absence of packs it never consults.
        bool needsPolicies = graph.Nodes.Any(node =>
            node.EntryGate.Concat(node.ExitGate).Any(condition =>
                string.Equals(condition.Kind, "policy-clean", StringComparison.Ordinal)));

        Policy.PolicyEngine? policyEngine =
            PolicyComposition.TryBuild(settings.Policies, journal, out string? policyProblem);

        if (policyEngine is null && needsPolicies)
        {
            AnsiConsole.MarkupLine(
                $"[red]cannot load policies[/] {policyProblem!.EscapeMarkup()}");
            AnsiConsole.MarkupLine(
                "[grey]this lifecycle gates on a policy pack, so it cannot run without one — "
                + "run from the repository root, or pass --policies[/]");

            return ExitCode.BadInput;
        }

        WorkflowEngine engine = new(
            graph,
            ScriptedAgents.CoveringGraph(graph),
            BuiltInGateEvaluators.CreateRegistry(),
            journal,
            SystemClock.Instance,
            new EngineOptions(settings.MaxConcurrency),
            CompensationRegistry.BuiltIn(),
            new GitRunWorkspaceFactory(settings.WorkspaceRoot, settings.Template),
            RealDelay.Instance,
            new FileSafeStopMonitor(FileSafeStopMonitor.DefaultDirectory),
            policyEngine);

        AnsiConsole.Write(new Rule($"[bold]resuming {runId.Value.EscapeMarkup()}[/]").LeftJustified());

        RunOutcome outcome = await engine
            .ResumeAsync(runId, events, cancellationToken)
            .ConfigureAwait(false);

        WriteStages(graph, outcome);

        ImmutableArray<RunEvent> after =
            await journal.ReadAsync(runId, cancellationToken).ConfigureAwait(false);

        AuditVerification verification = AuditChain.Verify(runId, after);

        AnsiConsole.MarkupLine(verification.IsIntact
            ? $"[green]audit[/] {verification.Summary.EscapeMarkup()}"
            : $"[red]audit[/] {verification.Summary.EscapeMarkup()}");

        AnsiConsole.MarkupLine(
            $"{RunStatusMarkup.For(outcome.Status)} {outcome.Reason.EscapeMarkup()}");

        return outcome.Status switch
        {
            RunStatus.Succeeded => ExitCode.Success,
            RunStatus.AwaitingApproval or RunStatus.Blocked => ExitCode.AwaitingHuman,
            _ => ExitCode.Failed,
        };
    }

    private static void WriteStages(WorkflowGraph graph, RunOutcome outcome)
    {
        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]stage[/]")
            .AddColumn("[bold]state[/]")
            .AddColumn("[bold]detail[/]");

        foreach (NodeId id in graph.TopologicalOrder)
        {
            NodeExecutionState node = outcome.State.Nodes[id];

            table.AddRow(
                id.Value.EscapeMarkup(),
                NodeStateMarkup.For(node.State),
                Truncate(node.Detail).EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    private static string Truncate(string? detail) =>
        detail is null ? string.Empty
        : detail.Length <= 80 ? detail
        : detail[..77] + "...";
}
