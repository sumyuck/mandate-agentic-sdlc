namespace Mandate.Core.Artifacts;

/// <summary>
/// The kinds of engineering output the orchestrator produces and gates on.
/// </summary>
/// <remarks>
/// Typed rather than free-form because gates are written against kinds: the release gate
/// asks whether a passing <see cref="TestReport"/> and a clean <see cref="SecurityReport"/>
/// exist, not whether some file happens to be present.
/// </remarks>
public enum ArtifactKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>The original request, captured verbatim.</summary>
    Request = 1,

    /// <summary>Normalised problem statement with scope, acceptance criteria and NFRs.</summary>
    RequirementSpec = 2,

    /// <summary>Ambiguities found in a requirement, with candidate interpretations.</summary>
    AmbiguityReport = 3,

    /// <summary>A question put to a human because the requirement could not be resolved.</summary>
    ClarificationRequest = 4,

    /// <summary>A human's answer to a clarification question.</summary>
    ClarificationResponse = 5,

    /// <summary>A working assumption recorded where a question went unanswered.</summary>
    Assumption = 6,

    /// <summary>Impacted modules, APIs and data flows for a change to existing code.</summary>
    ImpactAnalysis = 7,

    /// <summary>Design of the solution, including component and sequence views.</summary>
    DesignDoc = 8,

    /// <summary>An architecture decision record, generated from a recorded decision.</summary>
    ArchitectureDecisionRecord = 9,

    /// <summary>An interface contract, such as an OpenAPI document or a message schema.</summary>
    ApiContract = 10,

    /// <summary>A code change as a diff against the run workspace.</summary>
    SourcePatch = 11,

    /// <summary>Generated tests.</summary>
    TestSuite = 12,

    /// <summary>Results of actually executing tests, including coverage.</summary>
    TestReport = 13,

    /// <summary>Findings from reviewing a change.</summary>
    ReviewReport = 14,

    /// <summary>Findings from secret, dependency and pattern scanning.</summary>
    SecurityReport = 15,

    /// <summary>User- or operator-facing documentation.</summary>
    Documentation = 16,

    /// <summary>The assembled go/no-go evidence pack for a release.</summary>
    ReleaseChecklist = 17,

    /// <summary>Derived metrics for a run.</summary>
    MetricsReport = 18,
}
