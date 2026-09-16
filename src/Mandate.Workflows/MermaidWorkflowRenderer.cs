using System.Collections.Immutable;
using System.Text;
using Mandate.Core.Identifiers;
using Mandate.Core.Workflow;

namespace Mandate.Workflows;

/// <summary>
/// Renders a workflow as a Mermaid flowchart.
/// </summary>
/// <remarks>
/// <para>
/// A declarative graph is only an advantage if someone can see it. Mermaid renders natively
/// in Markdown on GitHub, so the diagram in the architecture documentation is generated from
/// the same file the engine executes and cannot drift away from it — which is the usual fate
/// of a hand-drawn architecture diagram.
/// </para>
/// <para>
/// The rendering deliberately shows the governance, not just the topology: which stages need
/// a human, which paths are conditional and on what, which returns are loop-backs, and what
/// autonomy each stage carries.
/// </para>
/// </remarks>
public static class MermaidWorkflowRenderer
{
    /// <summary>Renders the graph as a Mermaid <c>flowchart</c> definition.</summary>
    public static string Render(WorkflowGraph graph, bool includeLegend = true)
    {
        ArgumentNullException.ThrowIfNull(graph);

        StringBuilder diagram = new();
        diagram.AppendLine("flowchart TD");

        RenderNodes(graph, diagram);
        diagram.AppendLine();
        RenderEdges(graph, diagram);
        diagram.AppendLine();
        RenderClasses(graph, diagram);

        if (includeLegend)
        {
            diagram.AppendLine();
            RenderLegend(diagram);
        }

        return diagram.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>Renders the graph as a fenced Mermaid block ready to paste into Markdown.</summary>
    public static string RenderMarkdown(WorkflowGraph graph, bool includeLegend = true)
    {
        ArgumentNullException.ThrowIfNull(graph);

        return $"```mermaid{Environment.NewLine}{Render(graph, includeLegend)}```{Environment.NewLine}";
    }

    private static void RenderNodes(WorkflowGraph graph, StringBuilder diagram)
    {
        foreach (NodeId id in graph.TopologicalOrder)
        {
            WorkflowNode node = graph.Node(id);
            string label = Label(node);

            // Hexagons mark the stages a human has to sign off, so the checkpoints are the
            // first thing visible in the diagram.
            string shape = node.RequiresApproval
                ? $"{Identifier(id)}{{{{\"{label}\"}}}}"
                : $"{Identifier(id)}[\"{label}\"]";

            diagram.AppendLine($"    {shape}");
        }
    }

    private static void RenderEdges(WorkflowGraph graph, StringBuilder diagram)
    {
        foreach (WorkflowEdge edge in graph.Definition.Edges)
        {
            string from = Identifier(edge.From);
            string to = Identifier(edge.To);

            if (edge.Kind == EdgeKind.LoopBack)
            {
                diagram.AppendLine($"    {from} -.->|retry / re-plan| {to}");
                continue;
            }

            diagram.AppendLine(edge.IsConditional
                ? $"    {from} -->|\"{Escape(edge.Guard!)}\"| {to}"
                : $"    {from} --> {to}");
        }
    }

    private static void RenderClasses(WorkflowGraph graph, StringBuilder diagram)
    {
        diagram.AppendLine("    classDef proposeOnly fill:#fff4e5,stroke:#b26a00,stroke-width:2px;");
        diagram.AppendLine("    classDef sandbox fill:#e8f0fe,stroke:#1a56db,stroke-width:1px;");
        diagram.AppendLine("    classDef autoAccept fill:#e9f7ef,stroke:#1e7e34,stroke-width:1px;");
        diagram.AppendLine("    classDef autonomous fill:#fdecea,stroke:#b71c1c,stroke-width:2px;");

        foreach ((AutonomyLevel level, string className) in AutonomyClasses)
        {
            ImmutableArray<string> members =
            [
                .. graph.TopologicalOrder
                    .Where(id => graph.Node(id).Autonomy == level)
                    .Select(Identifier),
            ];

            if (!members.IsEmpty)
            {
                diagram.AppendLine($"    class {string.Join(",", members)} {className};");
            }
        }
    }

    private static void RenderLegend(StringBuilder diagram)
    {
        diagram.AppendLine("    subgraph legend[\"legend\"]");
        diagram.AppendLine("        direction LR");
        diagram.AppendLine("        l1[\"L2 — acts, low-risk output auto-accepted\"]");
        diagram.AppendLine("        l2[\"L1 — acts in the run workspace\"]");
        diagram.AppendLine("        l3{{\"L0 — proposes only; a human decides\"}}");
        diagram.AppendLine("    end");
        diagram.AppendLine("    class l1 autoAccept;");
        diagram.AppendLine("    class l2 sandbox;");
        diagram.AppendLine("    class l3 proposeOnly;");
    }

    private static ImmutableArray<(AutonomyLevel Level, string ClassName)> AutonomyClasses =>
    [
        (AutonomyLevel.ProposeOnly, "proposeOnly"),
        (AutonomyLevel.ActInSandbox, "sandbox"),
        (AutonomyLevel.ActAndAutoAcceptLowRisk, "autoAccept"),
        (AutonomyLevel.FullyAutonomous, "autonomous"),
    ];

    private static string Label(WorkflowNode node)
    {
        StringBuilder label = new();
        label.Append(Escape(node.Id.Value));
        label.Append("<br/><i>");
        label.Append(Escape(node.Stage.ToString()));
        label.Append("</i>");
        label.Append("<br/>");
        label.Append(ShortAutonomy(node.Autonomy));

        if (node.RequiresApproval)
        {
            label.Append("<br/>⚑ ");
            label.Append(Escape(string.Join(", ", node.Approvals.Select(approval => approval.Role))));
        }

        return label.ToString();
    }

    private static string ShortAutonomy(AutonomyLevel level) => level switch
    {
        AutonomyLevel.ProposeOnly => "L0 propose",
        AutonomyLevel.ActInSandbox => "L1 sandbox",
        AutonomyLevel.ActAndAutoAcceptLowRisk => "L2 auto-accept",
        AutonomyLevel.FullyAutonomous => "L3 autonomous",
        _ => "autonomy unset",
    };

    // Mermaid identifiers cannot contain dashes, and quotes or pipes inside a label end it.
    private static string Identifier(NodeId id) =>
        id.Value.Replace("-", "_", StringComparison.Ordinal);

    private static string Escape(string text) => text
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal);
}
