using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using Mandate.Agents.Scripted;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Gates;
using Mandate.Persistence;
using Mandate.Persistence.Sqlite;
using Mandate.Workflows;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Executes the lifecycle against scripted agents.
/// </summary>
/// <remarks>
/// Scripted agents produce genuine content-addressed artifacts, contribute the context facts
/// their nodes declare, and record decisions — so the graph walk, the join policies, the
/// guards, the gates and the audit chain are all doing real work. What is not yet real is the
/// engineering judgment inside each stage; that arrives with the model-backed agents.
/// </remarks>
internal sealed class RunCommand : AsyncCommand<RunCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<request>")]
        [Description("The requirement to execute, as the requester would write it.")]
        public string Request { get; init; } = string.Empty;

        [CommandOption("-s|--scenario")]
        [Description("greenfield, brownfield or ambiguous.")]
        public string Scenario { get; init; } = "greenfield";

        [CommandOption("--existing-code")]
        [Description("The target already contains the code under change.")]
        public bool ExistingCode { get; init; }

        [CommandOption("--as")]
        [Description("The human initiating the run. Recorded on the run.")]
        public string InitiatedBy { get; init; } = Environment.UserName;

        [CommandOption("-c|--max-concurrency")]
        [Description("How many stages may execute at once. Defaults to 4.")]
        public int MaxConcurrency { get; init; } = EngineOptions.Default.MaxConcurrency;

        [CommandOption("-w|--workflow")]
        [Description("Workflow file to execute. Defaults to workflows/sdlc.v1.yaml.")]
        public string Workflow { get; init; } = DefaultWorkflow.Path;

        [CommandOption("--events")]
        [Description("Print the full audit log rather than the stage summary.")]
        public bool ShowEvents { get; init; }

        [CommandOption("--store")]
        [Description("Path to the run store. Defaults to .mandate/runs.db.")]
        public string Store { get; init; } = SqliteRunJournal.DefaultPath;

        [CommandOption("--ephemeral")]
        [Description("Do not persist the run. Nothing to inspect, verify or resume afterwards.")]
        public bool Ephemeral { get; init; }

        public override ValidationResult Validate() =>
            Enum.TryParse(Scenario, ignoreCase: true, out ScenarioKind parsed)
            && parsed != ScenarioKind.Unknown
                ? ValidationResult.Success()
                : ValidationResult.Error(
                    $"'{Scenario}' is not a scenario. Expected greenfield, brownfield or ambiguous.");
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        WorkflowGraph graph;

        try
        {
            graph = WorkflowYamlLoader.LoadGraph(settings.Workflow);
        }
        catch (Exception exception) when (
            exception is WorkflowFormatException or WorkflowValidationException)
        {
            AnsiConsole.MarkupLine($"[red]cannot load workflow[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.BadInput;
        }

        ScenarioKind scenario = Enum.Parse<ScenarioKind>(settings.Scenario, ignoreCase: true);

        // Brownfield means there is existing code, by definition. Accepting the flag
        // separately lets an ambiguous request concern existing code too.
        bool hasExistingCode = settings.ExistingCode || scenario == ScenarioKind.Brownfield;

        // Runs persist by default. An unrecorded run cannot be inspected, verified or
        // resumed, so not recording one has to be something you ask for.
        SqliteRunJournal? store = settings.Ephemeral ? null : SqliteRunJournal.Open(settings.Store);
        using IDisposable? storeLifetime = store;

        InMemoryRunJournal? scratch = settings.Ephemeral ? new InMemoryRunJournal() : null;
        IRunJournal journal = store is not null ? store : scratch!;

        SystemClock clock = SystemClock.Instance;

        RunId runId = RunId.New(clock.UtcNow, Guid.NewGuid().ToString("N")[..6]);

        WorkflowEngine engine;

        try
        {
            engine = new WorkflowEngine(
                graph,
                ScriptedAgents.CoveringGraph(graph),
                BuiltInGateEvaluators.CreateRegistry(),
                journal,
                clock,
                new EngineOptions(settings.MaxConcurrency));
        }
        catch (Exception exception) when (
            exception is EngineConfigurationException or ArgumentOutOfRangeException)
        {
            AnsiConsole.MarkupLine($"[red]engine not configured[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.BadInput;
        }

        RunRequest request = RunRequest.Create(
            runId, settings.Request, scenario, Actor.Human(settings.InitiatedBy), hasExistingCode);

        AnsiConsole.Write(new Rule($"[bold]{runId.Value.EscapeMarkup()}[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"[grey]{graph.Definition.Identity.EscapeMarkup()} · {scenario.ToString().ToLowerInvariant()} · "
            + $"existing code: {hasExistingCode} · max concurrency {settings.MaxConcurrency}[/]");

        RunOutcome outcome = await engine.RunAsync(request, cancellationToken).ConfigureAwait(false);

        ImmutableArray<RunEvent> events = await journal
            .ReadAsync(runId, cancellationToken)
            .ConfigureAwait(false);

        if (settings.ShowEvents)
        {
            RunEventTable.Write(events);
        }
        else
        {
            WriteStages(graph, outcome);
        }

        WriteAudit(runId, events);
        WriteOutcome(outcome);

        if (store is not null)
        {
            AnsiConsole.MarkupLine($"[grey]recorded in {settings.Store.EscapeMarkup()}[/]");

            // Unwrapped, so it can be copied or piped. AnsiConsole would hard-wrap it.
            Console.Out.WriteLine($"run id: {runId.Value}");
        }

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
            .AddColumn("[bold]model[/]")
            .AddColumn("[bold]ms[/]", column => column.RightAligned())
            .AddColumn("[bold]detail[/]");

        foreach (NodeId id in graph.TopologicalOrder)
        {
            NodeExecutionState node = outcome.State.Nodes[id];
            WorkflowNode declared = graph.Node(id);

            string elapsed = node.Elapsed is { } span
                ? ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                : "-";

            table.AddRow(
                id.Value.EscapeMarkup(),
                Colour(node.State),
                (node.State == NodeState.Skipped ? "-" : declared.Model ?? "-").EscapeMarkup(),
                elapsed,
                Truncate(node.Detail).EscapeMarkup());
        }

        AnsiConsole.Write(table);
    }

    private static void WriteAudit(RunId runId, ImmutableArray<RunEvent> events)
    {
        AuditVerification verification = AuditChain.Verify(runId, events);

        AnsiConsole.MarkupLine(verification.IsIntact
            ? $"[green]audit[/] {verification.Summary.EscapeMarkup()}"
            : $"[red]audit[/] {verification.Summary.EscapeMarkup()}");
    }

    private static void WriteOutcome(RunOutcome outcome)
    {
        string colour = outcome.Status switch
        {
            RunStatus.Succeeded => "green",
            RunStatus.AwaitingApproval => "yellow",
            RunStatus.Blocked => "yellow",
            _ => "red",
        };

        AnsiConsole.MarkupLine(
            $"[{colour}]{outcome.Status.ToString().ToLowerInvariant()}[/] "
            + outcome.Reason.EscapeMarkup());
    }

    private static string Colour(NodeState state) => state switch
    {
        NodeState.Succeeded => "[green]succeeded[/]",
        NodeState.Skipped => "[grey]skipped[/]",
        NodeState.AwaitingApproval => "[yellow]awaiting approval[/]",
        NodeState.Blocked => "[yellow]blocked[/]",
        NodeState.Failed => "[red]failed[/]",
        NodeState.RolledBack => "[red]rolled back[/]",
        _ => $"[grey]{state.ToString().ToLowerInvariant()}[/]",
    };

    private static string Truncate(string? detail) =>
        detail is null ? string.Empty
        : detail.Length <= 90 ? detail
        : detail[..87] + "...";
}
