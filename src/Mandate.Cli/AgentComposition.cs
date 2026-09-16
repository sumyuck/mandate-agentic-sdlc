using Mandate.Agents.Model;
using Mandate.Agents.Scripted;
using Mandate.Cli.Commands;
using Mandate.Core.Execution;
using Mandate.Core.Llm;
using Mandate.Core.Time;
using Mandate.Core.Workflow;
using Mandate.Llm;
using Mandate.Llm.Clients;
using Mandate.Llm.Pricing;
using Mandate.Llm.Prompts;

namespace Mandate.Cli;

/// <summary>
/// Binds a workflow's named agents to something that can execute them.
/// </summary>
/// <remarks>
/// Shared by <c>run</c> and <c>resume</c> because a run must be resumed by the same kind of
/// agents that started it. Two copies of this decision would eventually disagree, and the
/// symptom would be a run whose second half was executed by a different system than its
/// first — with nothing in the evidence saying so.
/// </remarks>
internal static class AgentComposition
{
    /// <summary>What was composed, and why it failed when it did.</summary>
    /// <param name="Registry">The agents, when composition succeeded.</param>
    /// <param name="Models">The model layer, when model agents were asked for.</param>
    /// <param name="Problem">Why composition failed, when it did.</param>
    internal sealed record Result(
        IStageAgentRegistry? Registry, LlmLayer? Models, string? Problem)
    {
        public bool Succeeded => Registry is not null;
    }

    /// <summary>
    /// Composes the agents a run will execute with.
    /// </summary>
    /// <remarks>
    /// Failures here are returned rather than thrown, because every one of them is an
    /// operator mistake with a sentence that fixes it — a missing prompt, an unreadable
    /// price list, no API key. A stack trace would bury the sentence.
    /// </remarks>
    public static Result Build(
        WorkflowGraph graph,
        LlmSettings settings,
        bool useModels,
        decimal budgetUsd,
        IClock clock,
        IWorkspaceVerifier verifier,
        IReadOnlyDictionary<string, ScriptedBehaviour>? scriptedBehaviours = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);

        if (!useModels)
        {
            return new Result(
                ScriptedAgents.CoveringGraph(graph, scriptedBehaviours), null, null);
        }

        LlmBudget budget = new(
            LlmBudget.Default.MaxCalls, LlmBudget.Default.MaxTokens, budgetUsd);

        LlmOptions options = settings.ToOptions(budget) with
        {
            // The stub only knows how to answer usefully because the agents tell it what
            // their contract is. Mandate.Llm must not learn that; it would be a dependency
            // from the model layer onto the thing that uses it.
            StubResponder = ContractStubResponder.Respond,
        };

        LlmLayer models;

        try
        {
            models = LlmComposition.Build(options, clock);
        }
        catch (Exception exception) when (
            exception is PromptFormatException or ModelPricingException or LlmException)
        {
            return new Result(null, null, exception.Message);
        }

        try
        {
            return new Result(
                ModelAgents.CoveringGraph(
                    graph, models.Prompts, models.Client, models.Prices, clock, verifier),
                models,
                null);
        }
        catch (AgentCompositionException exception)
        {
            models.Dispose();
            return new Result(null, null, exception.Message);
        }
    }
}
