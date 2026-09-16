using System.Collections.Immutable;
using Mandate.Agents;
using Mandate.Agents.Scripted;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Execution;
using Mandate.Orchestrator.Gates;
using Mandate.Persistence;

namespace Mandate.Orchestrator.Tests.Support;

/// <summary>
/// Runs a workflow against scripted agents and the in-memory journal.
/// </summary>
/// <remarks>
/// Deliberately assembles the real components rather than mocks: the real gate evaluators, the
/// real journal contract, the real scheduler. Only the engineering judgment inside a stage is
/// scripted, so what these tests pin is the engine's behaviour and not a stand-in for it.
/// </remarks>
internal sealed class EngineHarness
{
    private readonly WorkflowGraph _graph;
    private readonly StageAgentRegistry _agents;
    private readonly EngineOptions _options;

    private EngineHarness(
        WorkflowGraph graph, StageAgentRegistry agents, EngineOptions options)
    {
        _graph = graph;
        _agents = agents;
        _options = options;
    }

    public InMemoryRunJournal Journal { get; } = new();

    public TestClock Clock { get; } = new();

    public static EngineHarness For(
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, ScriptedBehaviour>? behaviours = null,
        EngineOptions? options = null,
        IEnumerable<IStageAgent>? extraAgents = null)
    {
        WorkflowGraph graph = WorkflowGraph.Build(definition);

        ImmutableArray<IStageAgent> agents =
        [
            .. graph.Nodes
                .Select(node => node.Agent)
                .Distinct(StringComparer.Ordinal)
                .Where(agentId => extraAgents?.Any(agent =>
                    string.Equals(agent.Id, agentId, StringComparison.Ordinal)) != true)
                .OrderBy(agentId => agentId, StringComparer.Ordinal)
                .Select(agentId => (IStageAgent)new ScriptedStageAgent(
                    agentId,
                    behaviours is not null
                    && behaviours.TryGetValue(agentId, out ScriptedBehaviour? behaviour)
                        ? behaviour
                        : ScriptedBehaviour.Default)),
            .. extraAgents ?? [],
        ];

        return new EngineHarness(
            graph,
            new StageAgentRegistry(agents),
            options ?? EngineOptions.Sequential);
    }

    public WorkflowGraph Graph => _graph;

    public async Task<RunOutcome> RunAsync(
        ScenarioKind scenario = ScenarioKind.Greenfield,
        bool hasExistingCode = false,
        string request = "Do the thing.")
    {
        WorkflowEngine engine = new(
            _graph,
            _agents,
            BuiltInGateEvaluators.CreateRegistry(),
            Journal,
            Clock,
            _options);

        RunRequest runRequest = RunRequest.Create(
            RunId.New(Clock.UtcNow, "test01"),
            request,
            scenario,
            Actor.Human("tester"),
            hasExistingCode);

        return await engine.RunAsync(runRequest, CancellationToken.None);
    }

    public ImmutableArray<RunEvent> EventsOfKind(RunEventKind kind) =>
        [.. Journal.Events.Where(@event => @event.Kind == kind)];

    public static NodeState StateOf(RunOutcome outcome, string nodeId) =>
        outcome.State.StateOf(NodeId.Parse(nodeId));
}
