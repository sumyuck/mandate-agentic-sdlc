namespace Mandate.Workflows;

/// <summary>
/// Raised when a workflow file cannot be read into a definition.
/// </summary>
/// <remarks>
/// Distinct from <see cref="Core.Workflow.WorkflowValidationException"/> on purpose: this is
/// "the file is not a workflow" (malformed YAML, an unknown key, an unrecognised enum value),
/// whereas validation is "this workflow could not be executed". A reviewer fixing the first
/// is looking at syntax; a reviewer fixing the second is looking at the design of the graph.
/// </remarks>
public sealed class WorkflowFormatException : Exception
{
    /// <summary>Creates the exception for a problem at a known location in a known file.</summary>
    public WorkflowFormatException(string sourceName, string problem, int? line = null)
        : base(line is null ? $"{sourceName}: {problem}" : $"{sourceName}({line}): {problem}")
    {
        SourceName = sourceName;
        Problem = problem;
        Line = line;
    }

    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public WorkflowFormatException()
        : base("The workflow file could not be read.") => SourceName = string.Empty;

    /// <summary>Creates the exception with a message.</summary>
    public WorkflowFormatException(string message)
        : base(message) => SourceName = string.Empty;

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public WorkflowFormatException(string message, Exception innerException)
        : base(message, innerException) => SourceName = string.Empty;

    /// <summary>Name of the file or stream the problem was found in.</summary>
    public string SourceName { get; }

    /// <summary>The problem, without the location prefix.</summary>
    public string? Problem { get; }

    /// <summary>One-based line number, when the parser reported one.</summary>
    public int? Line { get; }
}
