using System.Diagnostics.CodeAnalysis;

namespace Helmsman.Core.Identifiers;

/// <summary>Which class of participant performed an action.</summary>
public enum ActorKind
{
    /// <summary>Unset.</summary>
    Unknown = 0,

    /// <summary>The orchestration engine itself (scheduling, gating, projection).</summary>
    Engine = 1,

    /// <summary>A stage agent executing work under a declared autonomy level.</summary>
    Agent = 2,

    /// <summary>A named human providing oversight, approval or clarification.</summary>
    Human = 3,

    /// <summary>A policy rule reaching a verdict.</summary>
    Policy = 4,
}

/// <summary>
/// Identity of whoever performed a recorded action, as <c>kind:name</c>.
/// </summary>
/// <remarks>
/// Every audit event carries one. This is what makes segregation of duties enforceable rather
/// than aspirational: the change-control rule that an implementer may not approve or review
/// its own change is a comparison between the actor on the implementation event and the actor
/// on the approval event. Without a first-class actor on every event, that rule could only be
/// asserted in prose.
/// </remarks>
public readonly record struct Actor
{
    private Actor(ActorKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    /// <summary>The class of participant.</summary>
    public ActorKind Kind { get; }

    /// <summary>The specific participant: an agent id, a human's identifier, or a rule id.</summary>
    public string Name { get; }

    /// <summary>The orchestration engine.</summary>
    public static Actor Engine { get; } = new(ActorKind.Engine, "orchestrator");

    /// <summary>A stage agent.</summary>
    public static Actor Agent(string agentId) => Create(ActorKind.Agent, agentId);

    /// <summary>A named human.</summary>
    public static Actor Human(string identifier) => Create(ActorKind.Human, identifier);

    /// <summary>A policy rule.</summary>
    public static Actor Policy(string ruleId) => Create(ActorKind.Policy, ruleId);

    /// <summary>The canonical <c>kind:name</c> form used in logs and persisted events.</summary>
    public string Value => IsEmpty ? string.Empty : $"{Kind.ToString().ToLowerInvariant()}:{Name}";

    /// <summary>True when this actor has not been assigned.</summary>
    public bool IsEmpty => Kind == ActorKind.Unknown;

    /// <summary>Parses the canonical form, throwing when malformed.</summary>
    public static Actor Parse(string value) =>
        TryParse(value, out Actor actor)
            ? actor
            : throw new FormatException(
                $"'{value}' is not a valid actor. Expected 'engine:orchestrator', "
                + "'agent:<id>', 'human:<id>' or 'policy:<rule-id>'.");

    /// <summary>Parses the canonical form without throwing.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out Actor actor)
    {
        actor = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        if (!Enum.TryParse(value[..separator], ignoreCase: true, out ActorKind kind)
            || kind == ActorKind.Unknown)
        {
            return false;
        }

        actor = new Actor(kind, value[(separator + 1)..]);
        return true;
    }

    /// <summary>
    /// True when these two actors are the same participant, for segregation-of-duties checks.
    /// </summary>
    public bool IsSameParticipantAs(Actor other) =>
        Kind == other.Kind && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value;

    private static Actor Create(ActorKind kind, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("An actor name may not contain ':'.", nameof(name));
        }

        return new Actor(kind, name);
    }
}
