using System.Collections.Immutable;

namespace Mandate.Orchestrator.Execution;

/// <summary>
/// Raised when the engine cannot execute a workflow with the components it was given.
/// </summary>
/// <remarks>
/// Thrown at construction, before a run exists. A workflow naming an agent that is not
/// registered, or a gate condition kind nothing can judge, would otherwise fail part-way
/// through a run that had already produced work — and a gate nothing can judge is
/// indistinguishable, in the audit log, from a gate that passed.
/// </remarks>
public sealed class EngineConfigurationException : Exception
{
    /// <summary>Creates the exception for a set of unmet requirements.</summary>
    public EngineConfigurationException(string workflowIdentity, ImmutableArray<string> problems)
        : base($"The engine cannot execute '{workflowIdentity}'. "
               + $"{problems.Length} unmet requirement(s):{Environment.NewLine}"
               + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem))) =>
        Problems = problems;

    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public EngineConfigurationException()
        : base("The engine is not configured to execute this workflow.") => Problems = [];

    /// <summary>Creates the exception with a message.</summary>
    public EngineConfigurationException(string message)
        : base(message) => Problems = [];

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public EngineConfigurationException(string message, Exception innerException)
        : base(message, innerException) => Problems = [];

    /// <summary>The unmet requirements.</summary>
    public ImmutableArray<string> Problems { get; }
}
