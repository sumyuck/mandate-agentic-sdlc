namespace Mandate.Core.Execution;

/// <summary>
/// The kind of engineering problem a run is solving.
/// </summary>
/// <remarks>
/// Not cosmetic: the scenario is contributed to the run context before any stage executes, and
/// the lifecycle routes on it. A brownfield run earns an impact-analysis stage that a
/// greenfield run skips.
/// </remarks>
public enum ScenarioKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>New capability, with no existing implementation to reason about.</summary>
    Greenfield = 1,

    /// <summary>A change to code that already exists.</summary>
    Brownfield = 2,

    /// <summary>A request that cannot be acted on as written until a human resolves it.</summary>
    Ambiguous = 3,
}
