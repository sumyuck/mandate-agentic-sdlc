using System.Collections.Immutable;
using Mandate.Agents;
using Mandate.Agents.Scripted;
using Mandate.Core.Events;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;
using Mandate.Orchestrator.Compensation;
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
    private readonly IRunWorkspaceFactory _workspaces;
    private readonly ISafeStopMonitor _safeStop;

    private EngineHarness(
        WorkflowGraph graph,
        StageAgentRegistry agents,
        EngineOptions options,
        IRunWorkspaceFactory workspaces,
        ISafeStopMonitor safeStop)
    {
        _graph = graph;
        _agents = agents;
        _options = options;
        _workspaces = workspaces;
        _safeStop = safeStop;
    }

    /// <summary>Durations the engine asked to wait between attempts.</summary>
    public FakeDelay Delay { get; } = new();

    /// <summary>The workspace the run wrote into, once it has run.</summary>
    public IRunWorkspace? Workspace { get; private set; }

    public InMemoryRunJournal Journal { get; } = new();

    public TestClock Clock { get; } = new();

    public static EngineHarness For(
        WorkflowDefinition definition,
        IReadOnlyDictionary<string, ScriptedBehaviour>? behaviours = null,
        EngineOptions? options = null,
        IEnumerable<IStageAgent>? extraAgents = null,
        IRunWorkspaceFactory? workspaces = null,
        ISafeStopMonitor? safeStop = null)
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
            options ?? EngineOptions.Sequential,
            workspaces ?? NullWorkspaces.Instance,
            safeStop ?? new NoStop());
    }

    public WorkflowGraph Graph => _graph;

    public async Task<RunOutcome> RunAsync(
        ScenarioKind scenario = ScenarioKind.Greenfield,
        bool hasExistingCode = false,
        string request = "Do the thing.")
    {
        RecordingWorkspaceFactory recording = new(_workspaces);

        WorkflowEngine engine = new(
            _graph,
            _agents,
            BuiltInGateEvaluators.CreateRegistry(),
            Journal,
            Clock,
            _options,
            CompensationRegistry.BuiltIn(),
            recording,
            Delay,
            _safeStop);

        RunRequest runRequest = RunRequest.Create(
            RunId.New(Clock.UtcNow, "test01"),
            request,
            scenario,
            Actor.Human("tester"),
            hasExistingCode);

        RunOutcome outcome = await engine.RunAsync(runRequest, CancellationToken.None);
        Workspace = recording.Created;

        return outcome;
    }

    public ImmutableArray<RunEvent> EventsOfKind(RunEventKind kind) =>
        [.. Journal.Events.Where(@event => @event.Kind == kind)];

    public static NodeState StateOf(RunOutcome outcome, string nodeId) =>
        outcome.State.StateOf(NodeId.Parse(nodeId));
}


/// <summary>Remembers the workspace it created, so a test can inspect the tree afterwards.</summary>
internal sealed class RecordingWorkspaceFactory(IRunWorkspaceFactory inner) : IRunWorkspaceFactory
{
    public IRunWorkspace? Created { get; private set; }

    public async Task<IRunWorkspace> CreateAsync(RunId runId, CancellationToken cancellationToken)
    {
        Created = await inner.CreateAsync(runId, cancellationToken);
        return Created;
    }
}

/// <summary>A factory for runs that write no files.</summary>
internal sealed class NullWorkspaces : IRunWorkspaceFactory
{
    public static NullWorkspaces Instance { get; } = new();

    public Task<IRunWorkspace> CreateAsync(RunId runId, CancellationToken cancellationToken) =>
        Task.FromResult<IRunWorkspace>(new Empty());

    private sealed class Empty : IRunWorkspace
    {
        public string Root => string.Empty;

        public Task<WorkspaceCommit?> CommitAsync(
            NodeId nodeId, int attempt, IReadOnlyCollection<WorkspaceFile> files,
            string message, CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceCommit?>(null);

        public Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task DiscardUncommittedAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<WorkspaceStatus> StatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceStatus(string.Empty, true, 0));

        public Task<ImmutableArray<WorkspaceCommit>> CommitsForAsync(
            NodeId nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<WorkspaceCommit>.Empty);
    }
}
