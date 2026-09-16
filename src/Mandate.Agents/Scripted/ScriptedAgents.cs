using System.Collections.Immutable;
using Mandate.Core.Execution;
using Mandate.Core.Workflow;

namespace Mandate.Agents.Scripted;

/// <summary>Builds a scripted agent for every agent a workflow names.</summary>
public static class ScriptedAgents
{
    /// <summary>
    /// A registry covering every agent the graph names.
    /// </summary>
    /// <param name="graph">The workflow whose agents must be covered.</param>
    /// <param name="behaviours">
    /// Per-agent behaviour overrides, keyed by agent id. Anything not named behaves normally.
    /// </param>
    public static StageAgentRegistry CoveringGraph(
        WorkflowGraph graph, IReadOnlyDictionary<string, ScriptedBehaviour>? behaviours = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        ImmutableArray<IStageAgent> agents =
        [
            .. graph.Nodes
                .Select(node => node.Agent)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(agentId => agentId, StringComparer.Ordinal)
                .Select(agentId => (IStageAgent)new ScriptedStageAgent(
                    agentId,
                    behaviours is not null
                    && behaviours.TryGetValue(agentId, out ScriptedBehaviour? behaviour)
                        ? behaviour
                        : ScriptedBehaviour.Default)),
        ];

        return new StageAgentRegistry(agents);
    }
}
