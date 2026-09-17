using System.Collections.Immutable;
using System.ComponentModel;
using Mandate.Core.Workflow;
using Mandate.Workflows;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Validates a workflow file without running anything.
/// </summary>
/// <remarks>
/// Exists so that a lifecycle change can be checked in review, the way a schema migration is
/// checked before it is applied. A workflow that cannot execute should be caught here rather
/// than part-way through a run that has already produced work.
/// </remarks>
internal sealed class ValidateWorkflowCommand : Command<ValidateWorkflowCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[file]")]
        [Description("Workflow file to validate. Defaults to workflows/sdlc.v1.yaml.")]
        public string File { get; init; } = DefaultWorkflow.Path;

        [CommandOption("--quiet")]
        [Description("Print nothing on success; report only problems.")]
        public bool Quiet { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        WorkflowDefinition definition;

        try
        {
            definition = WorkflowYamlLoader.LoadFile(settings.File);
        }
        catch (WorkflowFormatException exception)
        {
            AnsiConsole.MarkupLine($"[red]format error[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.BadInput;
        }

        ImmutableArray<WorkflowIssue> issues = WorkflowGraph.Validate(definition);
        ImmutableArray<WorkflowIssue> errors =
            [.. issues.Where(issue => issue.Severity == WorkflowIssueSeverity.Error)];
        ImmutableArray<WorkflowIssue> warnings =
            [.. issues.Where(issue => issue.Severity == WorkflowIssueSeverity.Warning)];

        if (!issues.IsEmpty)
        {
            Table table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]severity[/]")
                .AddColumn("[bold]code[/]")
                .AddColumn("[bold]node[/]")
                .AddColumn("[bold]problem[/]");

            foreach (WorkflowIssue issue in issues)
            {
                string severity = issue.Severity == WorkflowIssueSeverity.Error
                    ? "[red]error[/]"
                    : "[yellow]warning[/]";

                table.AddRow(
                    severity,
                    issue.Code,
                    (issue.NodeId?.Value ?? "-").EscapeMarkup(),
                    issue.Message.EscapeMarkup());
            }

            AnsiConsole.Write(table);
        }

        if (!errors.IsEmpty)
        {
            AnsiConsole.MarkupLine(
                $"[red]{definition.Identity.EscapeMarkup()} cannot be executed[/]: "
                + $"{errors.Length} blocking problem(s).");
            return ExitCode.Failed;
        }

        if (settings.Quiet)
        {
            return ExitCode.Success;
        }

        WorkflowGraph graph = WorkflowGraph.Build(definition);
        Summarise(graph, warnings.Length);
        return ExitCode.Success;
    }

    private static void Summarise(WorkflowGraph graph, int warningCount)
    {
        int conditionalPaths = graph.Definition.Edges.Count(edge => edge.IsConditional);

        AnsiConsole.Write(new Rule($"[bold]{graph.Definition.Identity.EscapeMarkup()}[/]").LeftJustified());

        ImmutableArray<ImmutableArray<Core.Identifiers.NodeId>> stages = graph.ParallelStages();

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]depth[/]")
            .AddColumn("[bold]nodes at this depth[/]");

        for (int depth = 0; depth < stages.Length; depth++)
        {
            table.AddRow(
                depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(", ", stages[depth].Select(node => node.Value)).EscapeMarkup());
        }

        AnsiConsole.Write(table);

        if (conditionalPaths > 0)
        {
            // Depth is a dependency calculation, not a prediction: nodes at the same depth
            // have no dependency on each other, but a guarded path may not be taken at all.
            AnsiConsole.MarkupLine(
                "[grey]Nodes at the same depth have no dependency on one another. Where a path "
                + "is guarded, whether it runs at all depends on the run's context.[/]");
        }

        int approvals = graph.Nodes.Count(node => node.RequiresApproval);
        int loopBacks = graph.Definition.Edges.Count(edge => edge.Kind == EdgeKind.LoopBack);
        int widest = stages.Max(stage => stage.Length);

        AnsiConsole.MarkupLine(
            $"[green]valid[/]: {graph.Nodes.Count()} nodes, {graph.Definition.Edges.Length} edges, "
            + $"widest parallel group {widest}, {conditionalPaths} conditional path(s), "
            + $"{loopBacks} loop-back(s), {approvals} human checkpoint(s), "
            + $"{warningCount} warning(s).");
    }
}

/// <summary>The workflow used when a command is given no file.</summary>
internal static class DefaultWorkflow
{
    public const string Path = "workflows/sdlc.v1.yaml";
}
