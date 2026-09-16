using System.ComponentModel;
using Mandate.Core.Workflow;
using Mandate.Workflows;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Mandate.Cli.Commands;

/// <summary>
/// Renders a workflow as a Mermaid diagram.
/// </summary>
/// <remarks>
/// The diagram in the architecture documentation is generated from the file the engine
/// executes, so the two cannot drift apart — which is the usual failure of a hand-drawn
/// architecture diagram.
/// </remarks>
internal sealed class RenderWorkflowCommand : Command<RenderWorkflowCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "[file]")]
        [Description("Workflow file to render. Defaults to workflows/sdlc.v1.yaml.")]
        public string File { get; init; } = DefaultWorkflow.Path;

        [CommandOption("-o|--output")]
        [Description("Write the diagram to this path instead of standard output.")]
        public string? Output { get; init; }

        [CommandOption("--markdown")]
        [Description("Wrap the diagram in a fenced Mermaid block for pasting into Markdown.")]
        public bool Markdown { get; init; }

        [CommandOption("--no-legend")]
        [Description("Omit the autonomy-level legend.")]
        public bool NoLegend { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        WorkflowGraph graph;

        try
        {
            graph = WorkflowYamlLoader.LoadGraph(settings.File);
        }
        catch (WorkflowFormatException exception)
        {
            AnsiConsole.MarkupLine($"[red]format error[/] {exception.Message.EscapeMarkup()}");
            return ExitCode.BadInput;
        }
        catch (WorkflowValidationException exception)
        {
            AnsiConsole.MarkupLine($"[red]invalid workflow[/] {exception.Message.EscapeMarkup()}");
            AnsiConsole.MarkupLine("Run [bold]mandate workflow validate[/] for the full list.");
            return ExitCode.Failed;
        }

        bool legend = !settings.NoLegend;

        string diagram = settings.Markdown
            ? MermaidWorkflowRenderer.RenderMarkdown(graph, legend)
            : MermaidWorkflowRenderer.Render(graph, legend);

        if (settings.Output is null)
        {
            // Straight to stdout: the console renderer hard-wraps, which would corrupt the
            // diagram when piped into a file.
            Console.Out.Write(diagram);
            return ExitCode.Success;
        }

        string? directory = Path.GetDirectoryName(settings.Output);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(settings.Output, diagram);
        AnsiConsole.MarkupLine(
            $"[green]wrote[/] {settings.Output.EscapeMarkup()} "
            + $"({graph.Nodes.Count()} nodes, {graph.Definition.Edges.Length} edges)");
        return ExitCode.Success;
    }
}
