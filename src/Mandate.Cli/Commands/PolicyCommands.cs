using System.Collections.Immutable;
using System.ComponentModel;
using Mandate.Core.Events;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Persistence.Sqlite;
using Mandate.Persistence.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>Settings shared by the policy commands.</summary>
internal abstract class PolicySettings : StoreSettings
{
    [CommandOption("--policies")]
    [Description("Directory holding the policy packs. Defaults to workflows/policies.")]
    public string Policies { get; init; } = PolicyComposition.DefaultDirectory;
}

/// <summary>Lists the rules this system is asserting it obeys.</summary>
internal sealed class ListPolicyCommand : Command<ListPolicyCommand.Settings>
{
    internal sealed class Settings : PolicySettings
    {
        [CommandOption("--category")]
        [Description("Show only one category: security, compliance or change-control.")]
        public string? Category { get; init; }
    }

    protected override int Execute(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

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

        foreach (PolicyPack pack in packs)
        {
            IEnumerable<PolicyRule> rules = settings.Category is { } category
                ? pack.Rules.Where(rule => string.Equals(
                    rule.Category.ToString().Replace("Control", "-control", StringComparison.Ordinal),
                    category,
                    StringComparison.OrdinalIgnoreCase))
                : pack.Rules;

            if (!rules.Any())
            {
                continue;
            }

            AnsiConsole.Write(new Rule($"[bold]{pack.Identity.EscapeMarkup()}[/]").LeftJustified());

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]rule[/]")
                .AddColumn("[bold]severity[/]")
                .AddColumn("[bold]statement[/]");

            foreach (PolicyRule rule in rules)
            {
                table.AddRow(
                    rule.Id.EscapeMarkup(),
                    SeverityMarkup(rule.Severity),
                    rule.Statement.EscapeMarkup());
            }

            AnsiConsole.Write(table);
        }

        AnsiConsole.MarkupLine(
            $"[grey]{packs.Sum(pack => pack.Rules.Length)} rule(s) across "
            + $"{packs.Length} pack(s).[/]");

        return ExitCode.Success;
    }

    internal static string SeverityMarkup(PolicySeverity severity) => severity switch
    {
        PolicySeverity.Blocking => "[red]blocking[/]",
        PolicySeverity.Advisory => "[yellow]advisory[/]",
        _ => "[grey]unset[/]",
    };
}

/// <summary>Evaluates the policy packs against a recorded run.</summary>
/// <remarks>
/// Read-only. It records nothing, so a person can see where a run stands without that
/// inspection itself becoming part of the run's history.
/// </remarks>
internal sealed class CheckPolicyCommand : AsyncCommand<CheckPolicyCommand.Settings>
{
    internal sealed class Settings : PolicySettings
    {
        [CommandArgument(0, "<runId>")]
        [Description("The run to evaluate.")]
        public string RunId { get; init; } = string.Empty;

        [CommandOption("-w|--workflow")]
        [Description("Workflow the run executes. Defaults to workflows/sdlc.v1.yaml.")]
        public string Workflow { get; init; } = DefaultWorkflow.Path;

        [CommandOption("--workspace-root")]
        [Description("Where run workspaces live. Defaults to .mandate/workspaces.")]
        public string WorkspaceRoot { get; init; } = GitRunWorkspaceFactory.DefaultRoot;
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

        Policy.PolicyEngine? engine =
            PolicyComposition.TryBuild(settings.Policies, journal, out string? problem);

        if (engine is null)
        {
            AnsiConsole.MarkupLine($"[red]cannot load policies[/] {problem!.EscapeMarkup()}");
            return ExitCode.BadInput;
        }

        RunState state = RunState.Rebuild(runId, events);

        string workspacePath = Path.Combine(settings.WorkspaceRoot, runId.Value);
        Core.Execution.IRunWorkspace workspace = Directory.Exists(Path.Combine(workspacePath, ".git"))
            ? GitRunWorkspace.Open(workspacePath, runId)
            : new AbsentWorkspace();

        bool allClean = true;

        foreach (PolicyPack pack in engine.Packs)
        {
            PolicyEvaluation evaluation = await engine
                .EvaluateAsync(pack.Name, state, graph, workspace, cancellationToken)
                .ConfigureAwait(false);

            allClean &= evaluation.IsClean;

            AnsiConsole.Write(new Rule($"[bold]{evaluation.Pack.EscapeMarkup()}[/]").LeftJustified());

            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]rule[/]")
                .AddColumn("[bold]verdict[/]")
                .AddColumn("[bold]evidence[/]");

            foreach (PolicyVerdict verdict in evaluation.Verdicts)
            {
                table.AddRow(
                    verdict.Rule.Id.EscapeMarkup(),
                    Verdict(verdict),
                    verdict.Explanation.EscapeMarkup());
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]{evaluation.Summary.EscapeMarkup()}[/]");
        }

        return allClean ? ExitCode.Success : ExitCode.Failed;
    }

    private static string Verdict(PolicyVerdict verdict) => verdict switch
    {
        { Satisfied: true } => "[green]satisfied[/]",
        { IsWaived: true } => "[yellow]waived[/]",
        { Blocks: true } => "[red]blocking[/]",
        _ => "[yellow]advisory[/]",
    };

    /// <summary>Stands in where a run has no workspace on disk.</summary>
    private sealed class AbsentWorkspace : Core.Execution.IRunWorkspace
    {
        public string Root => string.Empty;

        public Task<Core.Execution.WorkspaceCommit?> CommitAsync(
            NodeId nodeId, int attempt,
            IReadOnlyCollection<Core.Execution.WorkspaceFile> files,
            string message, CancellationToken cancellationToken) =>
            Task.FromResult<Core.Execution.WorkspaceCommit?>(null);

        public Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<Core.Execution.WorkspaceStatus> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new Core.Execution.WorkspaceStatus(string.Empty, true, 0));

        public Task<ImmutableArray<Core.Execution.WorkspaceCommit>> CommitsForAsync(
            NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<Core.Execution.WorkspaceCommit>.Empty);
    }
}
