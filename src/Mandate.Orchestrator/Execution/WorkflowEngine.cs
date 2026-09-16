using System.Collections.Immutable;
using Mandate.Core.Diagnostics;
using Mandate.Core.Execution;
using Mandate.Core.Time;
using Mandate.Core.Workflow;

namespace Mandate.Orchestrator.Execution;

/// <summary>
/// Executes a workflow definition as a governed run.
/// </summary>
/// <remarks>
/// <para>
/// The engine interprets the graph; it contains no lifecycle of its own. It walks the
/// dependency graph, honours join policy and conditional paths, evaluates entry and exit
/// gates, and executes eligible nodes with bounded concurrency — recording every decision it
/// makes into the run's audit log as it goes.
/// </para>
/// <para>
/// It holds no mutable run state. Each run is executed by a private
/// <see cref="RunExecution"/> whose only way to change state is to append an event and apply
/// the event that came back, so a run can be rebuilt from its log by the same code path that
/// produced it.
/// </para>
/// <para>
/// Configuration is checked at construction: an agent the workflow names but the registry
/// cannot resolve, or a gate condition kind nothing can judge, fails here rather than
/// part-way through a run that has already produced work.
/// </para>
/// </remarks>
public sealed class WorkflowEngine
{
    private readonly WorkflowGraph _graph;
    private readonly IStageAgentRegistry _agents;
    private readonly IGateEvaluatorRegistry _gates;
    private readonly IRunJournal _journal;
    private readonly IClock _clock;
    private readonly EngineOptions _options;

    /// <summary>Creates an engine for one workflow, validating that it can be executed.</summary>
    /// <exception cref="EngineConfigurationException">Some component the workflow needs is missing.</exception>
    public WorkflowEngine(
        WorkflowGraph graph,
        IStageAgentRegistry agents,
        IGateEvaluatorRegistry gates,
        IRunJournal journal,
        IClock clock,
        EngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);

        _graph = graph;
        _agents = agents;
        _gates = gates;
        _journal = journal;
        _clock = clock;
        _options = (options ?? EngineOptions.Default).Validated();

        ImmutableArray<string> problems = FindUnmetRequirements(graph, agents, gates);

        if (!problems.IsEmpty)
        {
            throw new EngineConfigurationException(graph.Definition.Identity, problems);
        }
    }

    /// <summary>The workflow this engine executes.</summary>
    public WorkflowGraph Graph => _graph;

    /// <summary>Executes a run to completion, or to the point where it needs a human.</summary>
    public async Task<RunOutcome> RunAsync(
        RunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using RunExecution execution = new(
            _graph, _agents, _gates, _journal, _clock, _options, request);

        return await execution.ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports everything the workflow needs that the supplied components cannot provide.
    /// </summary>
    /// <remarks>
    /// All problems at once, so one pass fixes the configuration rather than discovering the
    /// next missing piece on the next attempt.
    /// </remarks>
    public static ImmutableArray<string> FindUnmetRequirements(
        WorkflowGraph graph, IStageAgentRegistry agents, IGateEvaluatorRegistry gates)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(gates);

        ImmutableArray<string>.Builder problems = ImmutableArray.CreateBuilder<string>();

        foreach (WorkflowNode node in graph.Nodes.OrderBy(node => node.Id.Value, StringComparer.Ordinal))
        {
            if (agents.Resolve(node.Agent) is null)
            {
                problems.Add(
                    $"Node '{node.Id}' names agent '{node.Agent}', which is not registered. "
                    + $"Registered agents: {Describe(agents.KnownAgents)}.");
            }

            foreach ((GateCondition condition, GatePosition position) in Conditions(node))
            {
                if (gates.Resolve(condition.Kind) is null)
                {
                    problems.Add(
                        $"The {position.ToString().ToLowerInvariant()} gate on '{node.Id}' uses "
                        + $"condition kind '{condition.Kind}', which nothing can judge. "
                        + $"Known kinds: {Describe(gates.KnownKinds)}.");
                }
            }
        }

        return problems.ToImmutable();
    }

    private static IEnumerable<(GateCondition Condition, GatePosition Position)> Conditions(
        WorkflowNode node) =>
        node.EntryGate.Select(condition => (condition, GatePosition.Entry))
            .Concat(node.ExitGate.Select(condition => (condition, GatePosition.Exit)));

    private static string Describe(IReadOnlySet<string> known) =>
        known.Count == 0
            ? "none"
            : string.Join(", ", known.OrderBy(name => name, StringComparer.Ordinal));

    /// <summary>The engine build that executes runs, stamped into every run record.</summary>
    public static string Fingerprint => BuildInfo.Fingerprint;
}
