using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Workflow;

/// <summary>Severity of a problem found while validating a workflow.</summary>
public enum WorkflowIssueSeverity
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>The workflow cannot be executed.</summary>
    Error = 1,

    /// <summary>The workflow can run, but it probably does not express what was intended.</summary>
    Warning = 2,
}

/// <summary>A single problem found while validating a workflow.</summary>
/// <param name="Severity">Whether this blocks execution.</param>
/// <param name="Code">Stable machine-readable code, such as <c>WF001</c>.</param>
/// <param name="Message">What is wrong and what to do about it.</param>
/// <param name="NodeId">The node concerned, where the problem is node-specific.</param>
public sealed record WorkflowIssue(
    WorkflowIssueSeverity Severity,
    string Code,
    string Message,
    NodeId? NodeId);

/// <summary>
/// An executable, validated view over a <see cref="WorkflowDefinition"/>.
/// </summary>
/// <remarks>
/// <para>
/// Building the graph validates it. A definition that cannot be executed is rejected here,
/// before a run exists, with a precise message — rather than discovered part-way through a
/// run when some work has already been done and has to be undone.
/// </para>
/// <para>
/// The forward subgraph must be acyclic. Loops are permitted only along edges explicitly
/// marked <see cref="EdgeKind.LoopBack"/>, so the lifecycle can revisit earlier stages for a
/// retry or a re-plan without any possibility of an accidental cycle in the plan itself.
/// </para>
/// </remarks>
public sealed class WorkflowGraph
{
    private readonly ImmutableDictionary<NodeId, WorkflowNode> _nodes;
    private readonly ImmutableDictionary<NodeId, ImmutableArray<WorkflowEdge>> _outbound;
    private readonly ImmutableDictionary<NodeId, ImmutableArray<WorkflowEdge>> _inbound;

    private WorkflowGraph(
        WorkflowDefinition definition,
        ImmutableDictionary<NodeId, WorkflowNode> nodes,
        ImmutableDictionary<NodeId, ImmutableArray<WorkflowEdge>> outbound,
        ImmutableDictionary<NodeId, ImmutableArray<WorkflowEdge>> inbound,
        ImmutableArray<NodeId> topologicalOrder,
        ImmutableArray<WorkflowIssue> warnings)
    {
        Definition = definition;
        _nodes = nodes;
        _outbound = outbound;
        _inbound = inbound;
        TopologicalOrder = topologicalOrder;
        Warnings = warnings;
    }

    /// <summary>The definition this graph was built from.</summary>
    public WorkflowDefinition Definition { get; }

    /// <summary>Nodes in an order where every forward dependency precedes its dependents.</summary>
    public ImmutableArray<NodeId> TopologicalOrder { get; }

    /// <summary>Non-blocking problems found during validation.</summary>
    public ImmutableArray<WorkflowIssue> Warnings { get; }

    /// <summary>All nodes.</summary>
    public IEnumerable<WorkflowNode> Nodes => _nodes.Values;

    /// <summary>Nodes with no forward dependencies; where a run begins.</summary>
    public ImmutableArray<NodeId> EntryNodes =>
        [.. TopologicalOrder.Where(id => ForwardDependenciesOf(id).IsEmpty)];

    /// <summary>Nodes no forward edge leaves; where a run ends.</summary>
    public ImmutableArray<NodeId> TerminalNodes =>
        [.. TopologicalOrder.Where(id => _outbound[id].All(edge => edge.Kind != EdgeKind.Forward))];

    /// <summary>
    /// Builds and validates a graph, throwing when the definition cannot be executed.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The definition has blocking problems.</exception>
    public static WorkflowGraph Build(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        ImmutableArray<WorkflowIssue> issues = Validate(definition);
        ImmutableArray<WorkflowIssue> errors =
            [.. issues.Where(issue => issue.Severity == WorkflowIssueSeverity.Error)];

        if (!errors.IsEmpty)
        {
            throw new WorkflowValidationException(definition.Identity, errors);
        }

        ImmutableDictionary<NodeId, WorkflowNode> nodes =
            definition.Nodes.ToImmutableDictionary(node => node.Id);

        ImmutableDictionary<NodeId, ImmutableArray<WorkflowEdge>> outbound = nodes.Keys
            .ToImmutableDictionary(
                id => id,
                id => definition.Edges.Where(edge => edge.From == id).ToImmutableArray());

        ImmutableDictionary<NodeId, ImmutableArray<WorkflowEdge>> inbound = nodes.Keys
            .ToImmutableDictionary(
                id => id,
                id => definition.Edges.Where(edge => edge.To == id).ToImmutableArray());

        return new WorkflowGraph(
            definition,
            nodes,
            outbound,
            inbound,
            SortTopologically(nodes.Keys, definition.Edges),
            [.. issues.Where(issue => issue.Severity == WorkflowIssueSeverity.Warning)]);
    }

    /// <summary>Validates a definition without building it.</summary>
    public static ImmutableArray<WorkflowIssue> Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        ImmutableArray<WorkflowIssue>.Builder issues = ImmutableArray.CreateBuilder<WorkflowIssue>();

        ValidateIdentity(definition, issues);
        HashSet<NodeId> declared = ValidateNodes(definition, issues);
        ValidateEdges(definition, declared, issues);
        ValidateAcyclic(definition, declared, issues);
        ValidateConnectivity(definition, declared, issues);

        return issues.ToImmutable();
    }

    /// <summary>Looks up a node.</summary>
    public WorkflowNode Node(NodeId id) =>
        _nodes.TryGetValue(id, out WorkflowNode? node)
            ? node
            : throw new KeyNotFoundException($"Node '{id}' is not part of {Definition.Identity}.");

    /// <summary>True when the graph contains a node.</summary>
    public bool Contains(NodeId id) => _nodes.ContainsKey(id);

    /// <summary>Forward edges arriving at a node — the dependencies it waits on.</summary>
    public ImmutableArray<WorkflowEdge> ForwardDependenciesOf(NodeId id) =>
        [.. _inbound[id].Where(edge => edge.Kind == EdgeKind.Forward)];

    /// <summary>Forward edges leaving a node — the work it unblocks.</summary>
    public ImmutableArray<WorkflowEdge> ForwardDependentsOf(NodeId id) =>
        [.. _outbound[id].Where(edge => edge.Kind == EdgeKind.Forward)];

    /// <summary>Loop-back edges leaving a node.</summary>
    public ImmutableArray<WorkflowEdge> LoopBacksFrom(NodeId id) =>
        [.. _outbound[id].Where(edge => edge.Kind == EdgeKind.LoopBack)];

    /// <summary>
    /// Every node downstream of <paramref name="id"/> along forward edges.
    /// </summary>
    /// <remarks>
    /// The invalidation set for re-planning, expressed on the graph rather than on artifacts:
    /// when a node's result is discarded, this is the work that was built on it.
    /// </remarks>
    public ImmutableHashSet<NodeId> TransitiveDependentsOf(NodeId id)
    {
        ImmutableHashSet<NodeId>.Builder reached = ImmutableHashSet.CreateBuilder<NodeId>();
        Queue<NodeId> frontier = new();
        frontier.Enqueue(id);

        while (frontier.Count > 0)
        {
            foreach (WorkflowEdge edge in ForwardDependentsOf(frontier.Dequeue()))
            {
                if (reached.Add(edge.To))
                {
                    frontier.Enqueue(edge.To);
                }
            }
        }

        return reached.ToImmutable();
    }

    /// <summary>
    /// Sets of nodes that may execute concurrently, in dependency order.
    /// </summary>
    /// <remarks>
    /// Each set is a group whose members share no dependency on one another, so they are the
    /// parallel paths of the plan. Used to render the graph and to assert in tests that the
    /// lifecycle really does fan out and synchronise rather than running as a chain.
    /// </remarks>
    public ImmutableArray<ImmutableArray<NodeId>> ParallelStages()
    {
        Dictionary<NodeId, int> depth = [];

        foreach (NodeId id in TopologicalOrder)
        {
            ImmutableArray<WorkflowEdge> dependencies = ForwardDependenciesOf(id);

            depth[id] = dependencies.IsEmpty
                ? 0
                : dependencies.Max(edge => depth[edge.From]) + 1;
        }

        return
        [
            .. depth
                .GroupBy(entry => entry.Value)
                .OrderBy(group => group.Key)
                .Select(group => group
                    .Select(entry => entry.Key)
                    .OrderBy(id => id.Value, StringComparer.Ordinal)
                    .ToImmutableArray()),
        ];
    }

    private static void ValidateIdentity(
        WorkflowDefinition definition, ImmutableArray<WorkflowIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error, "WF001", "The workflow must have a name.", null));
        }

        if (string.IsNullOrWhiteSpace(definition.Version))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF002",
                "The workflow must have a version; runs record the version they executed.",
                null));
        }
    }

    private static HashSet<NodeId> ValidateNodes(
        WorkflowDefinition definition, ImmutableArray<WorkflowIssue>.Builder issues)
    {
        HashSet<NodeId> declared = [];

        if (definition.Nodes.IsEmpty)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error, "WF003", "The workflow declares no nodes.", null));
        }

        foreach (WorkflowNode node in definition.Nodes)
        {
            if (!declared.Add(node.Id))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF004",
                    $"Node '{node.Id}' is declared more than once.",
                    node.Id));
            }

            if (node.Stage == SdlcStage.Unknown)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF005",
                    $"Node '{node.Id}' does not declare a lifecycle stage.",
                    node.Id));
            }

            if (string.IsNullOrWhiteSpace(node.Agent))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF006",
                    $"Node '{node.Id}' does not name an agent to execute it.",
                    node.Id));
            }

            if (node.Autonomy == AutonomyLevel.Unknown)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF007",
                    $"Node '{node.Id}' does not declare an autonomy level. Agents may not "
                    + "execute outside a stated boundary.",
                    node.Id));
            }

            if (node.Retry.MaxAttempts < 1)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF008",
                    $"Node '{node.Id}' permits fewer than one attempt.",
                    node.Id));
            }

            if (node.Retry.OnExhaustion == FallbackStrategy.Compensate && !node.IsCompensable)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF009",
                    $"Node '{node.Id}' falls back to compensation but declares no compensating "
                    + "action, so its effects could not actually be undone.",
                    node.Id));
            }

            if (node.Timeout <= TimeSpan.Zero)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF010",
                    $"Node '{node.Id}' has no positive timeout; an attempt could hang forever.",
                    node.Id));
            }

            if (node.Join == JoinPolicy.Quorum && node.QuorumSize < 1)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF011",
                    $"Node '{node.Id}' joins on a quorum but does not state its size.",
                    node.Id));
            }

            if (node.Autonomy == AutonomyLevel.FullyAutonomous && node.RequiresApproval)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF012",
                    $"Node '{node.Id}' is fully autonomous yet requires approval. One of the "
                    + "two is wrong, and guessing which would misstate the autonomy boundary.",
                    node.Id));
            }

            if (node.ExitGate.IsEmpty)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF101",
                    $"Node '{node.Id}' has no exit gate, so its output is accepted unchecked.",
                    node.Id));
            }
        }

        return declared;
    }

    private static void ValidateEdges(
        WorkflowDefinition definition,
        HashSet<NodeId> declared,
        ImmutableArray<WorkflowIssue>.Builder issues)
    {
        HashSet<(NodeId, NodeId, EdgeKind)> seen = [];

        foreach (WorkflowEdge edge in definition.Edges)
        {
            if (!declared.Contains(edge.From))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF020",
                    $"Edge '{edge.From}' -> '{edge.To}' starts at an undeclared node.",
                    edge.From));
            }

            if (!declared.Contains(edge.To))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF021",
                    $"Edge '{edge.From}' -> '{edge.To}' ends at an undeclared node.",
                    edge.To));
            }

            if (edge.From == edge.To)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF022",
                    $"Node '{edge.From}' depends on itself.",
                    edge.From));
            }

            if (edge.Kind == EdgeKind.Unknown)
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Error,
                    "WF023",
                    $"Edge '{edge.From}' -> '{edge.To}' does not say whether it is a forward "
                    + "dependency or a loop-back.",
                    edge.From));
            }

            if (!seen.Add((edge.From, edge.To, edge.Kind)))
            {
                issues.Add(new WorkflowIssue(
                    WorkflowIssueSeverity.Warning,
                    "WF102",
                    $"Edge '{edge.From}' -> '{edge.To}' is declared more than once.",
                    edge.From));
            }
        }
    }

    private static void ValidateAcyclic(
        WorkflowDefinition definition,
        HashSet<NodeId> declared,
        ImmutableArray<WorkflowIssue>.Builder issues)
    {
        ImmutableArray<WorkflowEdge> forward =
        [
            .. definition.Edges.Where(edge =>
                edge.Kind == EdgeKind.Forward
                && declared.Contains(edge.From)
                && declared.Contains(edge.To)),
        ];

        ImmutableArray<NodeId> order = SortTopologically(declared, forward);

        if (order.Length == declared.Count)
        {
            return;
        }

        IEnumerable<NodeId> inCycle = declared.Except(order).OrderBy(id => id.Value, StringComparer.Ordinal);

        issues.Add(new WorkflowIssue(
            WorkflowIssueSeverity.Error,
            "WF030",
            "The forward graph contains a cycle through: "
            + string.Join(", ", inCycle)
            + ". A deliberate return to an earlier stage must be declared as a loop-back edge, "
            + "which is bounded by the target node's retry budget.",
            null));
    }

    private static void ValidateConnectivity(
        WorkflowDefinition definition,
        HashSet<NodeId> declared,
        ImmutableArray<WorkflowIssue>.Builder issues)
    {
        if (declared.Count == 0)
        {
            return;
        }

        ImmutableArray<WorkflowEdge> forward =
        [
            .. definition.Edges.Where(edge =>
                edge.Kind == EdgeKind.Forward
                && declared.Contains(edge.From)
                && declared.Contains(edge.To)),
        ];

        HashSet<NodeId> hasDependency = [.. forward.Select(edge => edge.To)];

        if (declared.All(hasDependency.Contains))
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF031",
                "Every node has a forward dependency, so the run has nowhere to start.",
                null));
            return;
        }

        // Reachability is not the useful check here. Once the forward graph is known to be
        // acyclic, every node without a forward dependency is by definition an entry node, so
        // every node is reachable from some entry and a reachability test can never fail.
        // What can actually go wrong is a node wired into no forward path at all, or a file
        // that declares two unrelated lifecycles. Both are connectivity problems.
        Dictionary<NodeId, HashSet<NodeId>> neighbours =
            declared.ToDictionary(id => id, _ => new HashSet<NodeId>());

        foreach (WorkflowEdge edge in forward)
        {
            neighbours[edge.From].Add(edge.To);
            neighbours[edge.To].Add(edge.From);
        }

        // A workflow of exactly one node has no edges to be connected by, and is a valid
        // (if trivial) lifecycle — so isolation only means something once there is a flow
        // for a node to be isolated from.
        ImmutableArray<NodeId> isolated = declared.Count == 1
            ? []
            :
            [
                .. declared
                    .Where(id => neighbours[id].Count == 0)
                    .OrderBy(id => id.Value, StringComparer.Ordinal),
            ];

        foreach (NodeId island in isolated)
        {
            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF032",
                $"Node '{island}' has no forward edge in or out, so it cannot participate in "
                + "the lifecycle. A loop-back alone does not connect a node to the flow.",
                island));
        }

        ImmutableArray<ImmutableArray<NodeId>> components =
            FindComponents(declared.Except(isolated), neighbours);

        if (components.Length > 1)
        {
            IEnumerable<string> described = components
                .OrderByDescending(component => component.Length)
                .Skip(1)
                .Select(component => "{" + string.Join(", ", component) + "}");

            issues.Add(new WorkflowIssue(
                WorkflowIssueSeverity.Error,
                "WF033",
                "The forward graph splits into independent lifecycles. These are disconnected "
                + $"from the main flow: {string.Join(", ", described)}. A governed lifecycle "
                + "must converge on a single release decision.",
                null));
        }
    }

    private static ImmutableArray<ImmutableArray<NodeId>> FindComponents(
        IEnumerable<NodeId> nodes, Dictionary<NodeId, HashSet<NodeId>> neighbours)
    {
        HashSet<NodeId> candidates = [.. nodes];
        HashSet<NodeId> visited = [];
        ImmutableArray<ImmutableArray<NodeId>>.Builder components =
            ImmutableArray.CreateBuilder<ImmutableArray<NodeId>>();

        foreach (NodeId start in candidates.OrderBy(id => id.Value, StringComparer.Ordinal))
        {
            if (!visited.Add(start))
            {
                continue;
            }

            List<NodeId> component = [start];
            Queue<NodeId> frontier = new();
            frontier.Enqueue(start);

            while (frontier.Count > 0)
            {
                foreach (NodeId neighbour in neighbours[frontier.Dequeue()])
                {
                    if (candidates.Contains(neighbour) && visited.Add(neighbour))
                    {
                        component.Add(neighbour);
                        frontier.Enqueue(neighbour);
                    }
                }
            }

            components.Add([.. component.OrderBy(id => id.Value, StringComparer.Ordinal)]);
        }

        return components.ToImmutable();
    }

    private static ImmutableArray<NodeId> SortTopologically(
        IEnumerable<NodeId> nodes, IEnumerable<WorkflowEdge> edges)
    {
        ImmutableArray<NodeId> all = [.. nodes];
        ImmutableArray<WorkflowEdge> forward =
            [.. edges.Where(edge => edge.Kind == EdgeKind.Forward)];

        Dictionary<NodeId, int> remaining = all.ToDictionary(
            id => id,
            id => forward.Count(edge => edge.To == id));

        // Deterministic tie-breaking: two runs of the same workflow must produce the same
        // order, or recorded evidence would differ between replays for no real reason.
        PriorityQueue<NodeId, string> ready = new();

        foreach ((NodeId id, int dependencies) in remaining.Where(entry => entry.Value == 0))
        {
            ready.Enqueue(id, id.Value);
        }

        ImmutableArray<NodeId>.Builder order = ImmutableArray.CreateBuilder<NodeId>(all.Length);

        while (ready.Count > 0)
        {
            NodeId current = ready.Dequeue();
            order.Add(current);

            foreach (WorkflowEdge edge in forward.Where(edge => edge.From == current))
            {
                if (--remaining[edge.To] == 0)
                {
                    ready.Enqueue(edge.To, edge.To.Value);
                }
            }
        }

        return order.ToImmutable();
    }
}

/// <summary>Raised when a workflow definition cannot be executed as declared.</summary>
public sealed class WorkflowValidationException : Exception
{
    /// <summary>Creates the exception for a set of blocking problems.</summary>
    public WorkflowValidationException(string workflowIdentity, ImmutableArray<WorkflowIssue> errors)
        : base(Format(workflowIdentity, errors)) => Errors = errors;

    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public WorkflowValidationException()
        : this(string.Empty, [])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public WorkflowValidationException(string message)
        : base(message) => Errors = [];

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public WorkflowValidationException(string message, Exception innerException)
        : base(message, innerException) => Errors = [];

    /// <summary>The blocking problems found.</summary>
    public ImmutableArray<WorkflowIssue> Errors { get; }

    private static string Format(string workflowIdentity, ImmutableArray<WorkflowIssue> errors)
    {
        IEnumerable<string> lines = errors.Select(error => $"  [{error.Code}] {error.Message}");

        return $"Workflow '{workflowIdentity}' cannot be executed. "
               + $"{errors.Length} blocking problem(s):{Environment.NewLine}"
               + string.Join(Environment.NewLine, lines);
    }
}
