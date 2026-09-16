using System.Collections.Immutable;
using Mandate.Core.Execution;

namespace Mandate.Agents;

/// <summary>A registry built from a fixed set of agents.</summary>
public sealed class StageAgentRegistry : IStageAgentRegistry
{
    private readonly ImmutableDictionary<string, IStageAgent> _byId;

    /// <summary>Creates a registry, rejecting two agents claiming the same id.</summary>
    public StageAgentRegistry(IEnumerable<IStageAgent> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        ImmutableDictionary<string, IStageAgent>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, IStageAgent>(StringComparer.Ordinal);

        foreach (IStageAgent agent in agents)
        {
            if (builder.ContainsKey(agent.Id))
            {
                throw new ArgumentException(
                    $"Two agents claim the id '{agent.Id}'. Which one executed a stage would "
                    + "depend on registration order, and the audit log would name an actor that "
                    + "does not uniquely identify what ran.",
                    nameof(agents));
            }

            builder[agent.Id] = agent;
        }

        _byId = builder.ToImmutable();
        KnownAgents = _byId.Keys.ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> KnownAgents { get; }

    /// <inheritdoc />
    public IStageAgent? Resolve(string agentId) =>
        _byId.TryGetValue(agentId, out IStageAgent? agent) ? agent : null;
}
