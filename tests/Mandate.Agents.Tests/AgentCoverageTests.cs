using System.Collections.Immutable;
using Mandate.Agents.Model;
using Mandate.Agents.Tests.Support;
using Mandate.Core.Artifacts;
using Mandate.Core.Execution;
using Mandate.Core.Llm;
using Mandate.Core.Workflow;
using Mandate.Llm.Clients;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;
using Mandate.Workflows;

namespace Mandate.Agents.Tests;

/// <summary>
/// The lifecycle and the prompt library have to agree, and nothing else in the build checks
/// that they do. A stage added to the workflow without a prompt, or a prompt whose declared
/// inputs no stage can supply, would otherwise fail on the day it first ran.
/// </summary>
public sealed class AgentCoverageTests
{
    private static WorkflowGraph Graph => WorkflowYamlLoader.LoadGraph(
        Path.Combine(RepositoryRoot.Path, "workflows", "sdlc.v1.yaml"));

    private static PromptLibrary Prompts => PromptLibrary.Load(
        Path.Combine(RepositoryRoot.Path, "prompts"));

    private static ModelPriceBook Prices => ModelPriceBook.Load(
        Path.Combine(RepositoryRoot.Path, "config", "model-pricing.yaml"));

    [Fact]
    public void Every_agent_the_lifecycle_names_has_a_prompt()
    {
        StageAgentRegistry registry = ModelAgents.CoveringGraph(
            Graph, Prompts, new StubLlmClient(), Prices, new TestClock());

        foreach (WorkflowNode node in Graph.Nodes)
        {
            registry.Resolve(node.Agent).ShouldNotBeNull(
                $"node '{node.Id}' names agent '{node.Agent}'");
        }
    }

    [Fact]
    public void Every_model_the_lifecycle_names_is_priced()
    {
        // An unpriced model makes the run's spend unknown and the budget ceiling non-binding
        // — a control that silently does not apply is worse than no control.
        ModelPriceBook prices = Prices;

        foreach (WorkflowNode node in Graph.Nodes)
        {
            node.Model.ShouldNotBeNullOrWhiteSpace($"node '{node.Id}' declares no model");
            prices.For(node.Model!).ShouldNotBeNull($"'{node.Model}' is not in the price list");
        }
    }

    [Fact]
    public void A_lifecycle_naming_an_agent_with_no_prompt_fails_at_composition()
    {
        PromptLibrary onlyConnectivity = PromptLibrary.Load(
            Path.Combine(RepositoryRoot.Path, "tests", "Mandate.Agents.Tests", "Fixtures", "sparse-prompts"));

        AgentCompositionException refused = Should.Throw<AgentCompositionException>(
            () => ModelAgents.CoveringGraph(
                Graph, onlyConnectivity, new StubLlmClient(), Prices, new TestClock()));

        // The message has to name the files to create; "composition failed" would send
        // someone reading source to find out which stage was missing.
        refused.Message.ShouldContain("requirements-analyst.v1.prompt.md");
    }

    [Fact]
    public async Task The_contract_stub_satisfies_every_agent_it_is_asked_to_stand_in_for()
    {
        // The offline path that lets the whole lifecycle be walked with no key and no
        // recordings. If the stub stops satisfying a node's declared contract, the engine's
        // own end-to-end tests lose their subject.
        StageAgentRegistry registry = ModelAgents.CoveringGraph(
            Graph, Prompts, new StubLlmClient(ContractStubResponder.Respond), Prices, new TestClock());

        foreach (WorkflowNode node in Graph.Nodes)
        {
            IStageAgent agent = registry.Resolve(node.Agent)!;

            StageResult result = await agent.ExecuteAsync(
                Executions.For(node), CancellationToken.None);

            result.Succeeded.ShouldBeTrue($"'{node.Id}': {result.Failure}");

            foreach (ArtifactKind kind in node.Produces)
            {
                result.Artifacts.Select(artifact => artifact.Kind).ShouldContain(kind);
            }

            foreach (string key in node.ProducesContext)
            {
                result.Facts.Select(fact => fact.Key).ShouldContain(key);
            }
        }
    }

    [Fact]
    public async Task Every_stubbed_document_says_that_no_model_produced_it()
    {
        // A stub that produced plausible-looking prose would make a stubbed run
        // indistinguishable from a real one at a glance.
        StageAgentRegistry registry = ModelAgents.CoveringGraph(
            Graph, Prompts, new StubLlmClient(ContractStubResponder.Respond), Prices, new TestClock());

        foreach (WorkflowNode node in Graph.Nodes)
        {
            StageResult result = await registry.Resolve(node.Agent)!
                .ExecuteAsync(Executions.For(node), CancellationToken.None);

            foreach (WorkspaceFile file in result.Files)
            {
                file.Content.ShouldContain(ContractStubResponder.Marker);
            }
        }
    }

    [Fact]
    public void Every_prompt_in_the_library_is_either_an_agent_or_a_tool()
    {
        // A prompt nothing invokes is either a stage someone forgot to wire up or dead
        // weight a reviewer has to read and dismiss.
        ImmutableHashSet<string> agents =
            [.. Graph.Nodes.Select(node => node.Agent)];

        foreach (string id in Prompts.Ids)
        {
            bool used = agents.Contains(id) || id == "connectivity-check";
            used.ShouldBeTrue($"prompt '{id}' is invoked by nothing");
        }
    }
}
