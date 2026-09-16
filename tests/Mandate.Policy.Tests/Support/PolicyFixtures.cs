using System.Collections.Immutable;
using System.Text;
using Mandate.Core.Artifacts;
using Mandate.Core.Context;
using Mandate.Core.Execution;
using Mandate.Core.Identifiers;
using Mandate.Core.Policies;
using Mandate.Core.Runs;
using Mandate.Core.Workflow;

namespace Mandate.Policy.Tests.Support;

/// <summary>A run view a test can assemble piece by piece.</summary>
internal sealed class FakeRun : IRunView
{
    private readonly Dictionary<NodeId, NodeState> _states = [];
    private readonly Dictionary<NodeId, Actor> _producers = [];

    public RunId RunId { get; } = RunId.New(DateTimeOffset.UnixEpoch, "test01");

    public RunStatus Status { get; set; } = RunStatus.Running;

    public RunContext Context { get; private set; } = RunContext.Empty;

    public ImmutableArray<Artifact> Artifacts { get; private set; } = [];

    public ImmutableDictionary<string, Actor> HeldApprovals { get; private set; } =
        ImmutableDictionary<string, Actor>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public ImmutableDictionary<string, Actor> DeniedApprovals { get; } =
        ImmutableDictionary<string, Actor>.Empty;

    public ImmutableDictionary<string, PolicyWaiver> Waivers { get; private set; } =
        ImmutableDictionary<string, PolicyWaiver>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);

    public NodeState StateOf(NodeId nodeId) =>
        _states.TryGetValue(nodeId, out NodeState state) ? state : NodeState.Pending;

    public Actor? ProducerOf(NodeId nodeId) =>
        _producers.TryGetValue(nodeId, out Actor actor) ? actor : null;

    public FakeRun WithFact(string key, string value)
    {
        Context = Context.Contribute(ContextFact.Create(
            key, value, NodeId.Parse("fixture"), Actor.Agent("fixture"), DateTimeOffset.UnixEpoch));

        return this;
    }

    public FakeRun WithArtifact(
        ArtifactKind kind, string name, string node = "fixture", params Sha256Hash[] derivedFrom)
    {
        Artifacts = Artifacts.Add(Artifact.FromContent(
            kind,
            name,
            "text/plain",
            Encoding.UTF8.GetBytes($"{kind}:{name}"),
            NodeId.Parse(node),
            Actor.Agent(node),
            DateTimeOffset.UnixEpoch,
            derivedFrom));

        return this;
    }

    public Sha256Hash HashOf(string name) =>
        Artifacts.First(artifact => artifact.Name == name).Hash;

    public FakeRun WithApproval(string role, Actor approver)
    {
        HeldApprovals = HeldApprovals.SetItem(role, approver);
        return this;
    }

    public FakeRun WithWaiver(string ruleId, string by = "alex", string reason = "Accepted risk.")
    {
        Waivers = Waivers.SetItem(
            ruleId,
            new PolicyWaiver(ruleId, Actor.Human(by), reason, DateTimeOffset.UnixEpoch));

        return this;
    }

    public FakeRun WithNode(string nodeId, NodeState state, string? producedBy = null)
    {
        NodeId id = NodeId.Parse(nodeId);
        _states[id] = state;

        if (producedBy is not null)
        {
            _producers[id] = Actor.Agent(producedBy);
        }

        return this;
    }
}

/// <summary>Small workflows for exercising rules about the lifecycle itself.</summary>
internal static class GraphFixtures
{
    public static WorkflowGraph WithNodes(params WorkflowNode[] nodes) =>
        WorkflowGraph.Build(new WorkflowDefinition("fixture", "v1", "Policy fixture.", [.. nodes], []));

    public static WorkflowNode Node(
        string id,
        AutonomyLevel autonomy = AutonomyLevel.ActInSandbox,
        IEnumerable<ApprovalRequirement>? approvals = null)
    {
        ImmutableArray<ApprovalRequirement> required = approvals?.ToImmutableArray() ?? [];

        return new WorkflowNode(
            NodeId.Parse(id),
            SdlcStage.Implementation,
            $"{id}-agent",
            $"Fixture {id}.",
            EntryGate: [],
            ExitGate:
            [
                new GateCondition("artifact-exists", "source-patch", "Produced."),
                .. required.Select(approval => new GateCondition(
                    "approval-held", approval.Role, "Signed off.")),
            ],
            Retry: RetryPolicy.None,
            Autonomy: autonomy,
            Approvals: required,
            Join: JoinPolicy.All,
            QuorumSize: 0,
            Timeout: TimeSpan.FromMinutes(1),
            Compensation: null,
            Model: "claude-sonnet-5",
            Produces: [ArtifactKind.SourcePatch],
            ProducesContext: []);
    }
}

/// <summary>A workspace backed by a real directory, for the rules that read the tree.</summary>
internal sealed class ScratchWorkspace : IRunWorkspace, IDisposable
{
    public ScratchWorkspace(bool clean = true)
    {
        Root = Path.Combine(Path.GetTempPath(), $"mandate-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        IsClean = clean;
    }

    public string Root { get; }

    public bool IsClean { get; set; }

    public void Write(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public Task<WorkspaceCommit?> CommitAsync(
        NodeId nodeId, int attempt, IReadOnlyCollection<WorkspaceFile> files,
        string message, CancellationToken cancellationToken) =>
        Task.FromResult<WorkspaceCommit?>(null);

    public Task<int> RevertNodeAsync(NodeId nodeId, CancellationToken cancellationToken) =>
        Task.FromResult(0);

    public Task DiscardUncommittedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<WorkspaceStatus> StatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new WorkspaceStatus(new string('a', 40), IsClean, 3));

    public Task<ImmutableArray<WorkspaceCommit>> CommitsForAsync(
        NodeId nodeId, CancellationToken cancellationToken) =>
        Task.FromResult(ImmutableArray<WorkspaceCommit>.Empty);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
