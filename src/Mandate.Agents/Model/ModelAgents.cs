using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Core.Llm;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;

namespace Mandate.Agents.Model;

/// <summary>Builds a model-backed agent for every agent the workflow names.</summary>
/// <remarks>
/// An agent's id <em>is</em> its prompt's id. That convention removes a mapping table that
/// would otherwise sit between the workflow and the prompt library and go stale — and it
/// makes the failure mode legible: adding a stage to the lifecycle without writing its
/// prompt fails at composition with a sentence naming the file to create, rather than at
/// the moment that stage first runs.
/// </remarks>
public static class ModelAgents
{
    /// <summary>A registry covering every agent the graph names.</summary>
    /// <exception cref="AgentCompositionException">
    /// The prompt library does not cover the workflow.
    /// </exception>
    public static StageAgentRegistry CoveringGraph(
        WorkflowGraph graph,
        PromptLibrary prompts,
        ILlmClient client,
        ModelPriceBook prices,
        IClock clock,
        IWorkspaceVerifier? verifier = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(clock);

        ImmutableArray<string> required =
        [
            .. graph.Nodes
                .Select(node => node.Agent)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        ImmutableArray<string> missing =
            [.. required.Where(agent => !prompts.Contains(agent))];

        if (!missing.IsEmpty)
        {
            throw new AgentCompositionException(
                $"The lifecycle names {missing.Length} agent(s) with no prompt: "
                + $"{string.Join(", ", missing)}. Create "
                + $"{string.Join(", ", missing.Select(agent => $"{prompts.Directory}/{agent}.v1{PromptLibrary.Extension}"))}.");
        }

        return new StageAgentRegistry(
        [
            .. required.Select(agent => (IStageAgent)new ModelStageAgent(
                agent, prompts.Get(agent), client, prices, clock, verifier)),
        ]);
    }
}

/// <summary>The agents could not be composed for a workflow.</summary>
public sealed class AgentCompositionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentCompositionException()
        : base("The agents could not be composed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public AgentCompositionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public AgentCompositionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
