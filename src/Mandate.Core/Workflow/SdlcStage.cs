namespace Mandate.Core.Workflow;

/// <summary>
/// The lifecycle stages the orchestrator governs.
/// </summary>
/// <remarks>
/// This is a closed set on purpose. A governed lifecycle whose stages can be invented in
/// configuration is not governed: policy packs, autonomy levels and release gates are all
/// written against specific stages, so adding one has to be a reviewed change to the engine
/// and its policies rather than a new string in a YAML file.
/// </remarks>
public enum SdlcStage
{
    /// <summary>Unset. Present so that a missing value fails validation instead of defaulting to a real stage.</summary>
    Unknown = 0,

    /// <summary>Accepts the raw request and establishes run context and scenario type.</summary>
    Intake = 1,

    /// <summary>Normalises intent into an engineering problem; detects and surfaces ambiguity.</summary>
    Requirements = 2,

    /// <summary>Brownfield only: identifies impacted modules, APIs and data flows, and blast radius.</summary>
    ImpactAnalysis = 3,

    /// <summary>Produces design, interface contracts and architecture decision records.</summary>
    Architecture = 4,

    /// <summary>Writes code into the run workspace.</summary>
    Implementation = 5,

    /// <summary>Generates and executes tests; the gate depends on real results, not claims.</summary>
    Testing = 6,

    /// <summary>Reviews the change. Performed by an actor distinct from the implementer.</summary>
    CodeReview = 7,

    /// <summary>Scans for secrets, vulnerable dependencies and insecure patterns.</summary>
    SecurityScan = 8,

    /// <summary>Produces user- and operator-facing documentation from run artifacts.</summary>
    Documentation = 9,

    /// <summary>Assembles the go/no-go evidence pack and requires human sign-off.</summary>
    ReleaseReadiness = 10,
}
