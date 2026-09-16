using Mandate.Core.Workflow;
using Mandate.Workflows.Tests.Support;

namespace Mandate.Workflows.Tests;

public sealed class MermaidWorkflowRendererTests
{
    private static readonly WorkflowGraph Shipped =
        WorkflowYamlLoader.LoadGraph(Repository.ShippedWorkflow);

    private static string Diagram => MermaidWorkflowRenderer.Render(Shipped);

    [Fact]
    public void It_renders_a_flowchart()
    {
        Diagram.ShouldStartWith("flowchart TD");
        Diagram.TrimEnd().ShouldNotEndWith("\n\n");
    }

    [Fact]
    public void Node_ids_are_sanitised_for_mermaid()
    {
        // Mermaid identifiers cannot contain dashes.
        Diagram.ShouldContain("impact_analysis[");
        Diagram.ShouldNotContain("impact-analysis[");
    }

    [Fact]
    public void Every_node_appears()
    {
        foreach (WorkflowNode node in Shipped.Nodes)
        {
            string identifier = node.Id.Value.Replace("-", "_", StringComparison.Ordinal);

            Diagram.ShouldContain(identifier, Case.Sensitive);
        }
    }

    [Fact]
    public void Stages_needing_a_human_are_shaped_differently_so_checkpoints_are_visible()
    {
        Diagram.ShouldContain("release_readiness{{");
        Diagram.ShouldContain("architecture{{");
        Diagram.ShouldContain("implement[");
    }

    [Fact]
    public void Approval_roles_are_shown_on_the_node()
    {
        Diagram.ShouldContain("release-approver");
        Diagram.ShouldContain("tech-lead");
    }

    [Fact]
    public void Autonomy_level_is_shown_on_every_node()
    {
        Diagram.ShouldContain("L0 propose");
        Diagram.ShouldContain("L1 sandbox");
        Diagram.ShouldContain("L2 auto-accept");
    }

    [Fact]
    public void Loop_backs_are_dashed_and_labelled_as_such()
    {
        Diagram.ShouldContain("test -.->|retry / re-plan| implement");
    }

    [Fact]
    public void Conditional_paths_are_labelled_with_the_guard_that_decides_them()
    {
        Diagram.ShouldContain("""|"run.has-existing-code == true"|""");
        Diagram.ShouldContain("""|"requirements.ambiguity-score > 0.3"|""");
    }

    [Fact]
    public void Unconditional_edges_carry_no_label()
    {
        Diagram.ShouldContain("intake --> requirements");
    }

    [Fact]
    public void A_legend_explains_the_autonomy_shading_and_can_be_omitted()
    {
        MermaidWorkflowRenderer.Render(Shipped, includeLegend: true).ShouldContain("legend");
        MermaidWorkflowRenderer.Render(Shipped, includeLegend: false).ShouldNotContain("legend");
    }

    [Fact]
    public void Markdown_rendering_produces_a_fenced_block_ready_to_paste()
    {
        string markdown = MermaidWorkflowRenderer.RenderMarkdown(Shipped);

        markdown.ShouldStartWith("```mermaid");
        markdown.TrimEnd().ShouldEndWith("```");
    }

    [Fact]
    public void Characters_that_would_break_a_mermaid_label_are_escaped()
    {
        WorkflowDefinition hostile = WorkflowYamlLoader.Load("""
            name: sdlc
            version: v1
            nodes:
              - id: awkward
                stage: requirements
                agent: x
                autonomy: act-in-sandbox
                description: 'Contains a "quote" and a | pipe'
                approvals:
                  - role: 'lead "the boss" | owner'
                    reason: Because.
                exit-gate:
                  - kind: approval-held
                    expression: 'lead "the boss" | owner'
                    description: Signed off.
            """);

        string diagram = MermaidWorkflowRenderer.Render(WorkflowGraph.Build(hostile));

        diagram.ShouldContain("&quot;");
        diagram.ShouldContain("&#124;");
    }
}
