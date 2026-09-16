using Mandate.Core.Workflow;

namespace Mandate.Workflows;

// The shapes below mirror the YAML file one-to-one, deliberately separate from the domain
// model. Deserialising straight into the domain types would mean either giving them setters
// and nullable members purely for the parser's benefit, or losing the ability to report a
// precise, file-level error. A DTO layer keeps the domain immutable and the diagnostics good.
//
// CA2227 objects to settable collection properties. That is the right rule for a domain model
// and the wrong one for a deserialisation target, which has to be populated by reflection.
#pragma warning disable CA2227

internal sealed class WorkflowDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Description { get; set; }

    public string? DefaultModel { get; set; }

    public List<NodeDocument>? Nodes { get; set; }

    public List<EdgeDocument>? Edges { get; set; }
}

internal sealed class NodeDocument
{
    public string? Id { get; set; }

    public string? Stage { get; set; }

    public string? Agent { get; set; }

    public string? Description { get; set; }

    public string? Autonomy { get; set; }

    public string? Model { get; set; }

    public string? Timeout { get; set; }

    public string? Join { get; set; }

    public int? QuorumSize { get; set; }

    public string? Compensation { get; set; }

    public List<GateDocument>? EntryGate { get; set; }

    public List<GateDocument>? ExitGate { get; set; }

    public RetryDocument? Retry { get; set; }

    public List<ApprovalDocument>? Approvals { get; set; }

    public List<string>? Produces { get; set; }

    public List<string>? ProducesContext { get; set; }
}

internal sealed class GateDocument
{
    public string? Kind { get; set; }

    public string? Expression { get; set; }

    public string? Description { get; set; }
}

internal sealed class RetryDocument
{
    public int? MaxAttempts { get; set; }

    public string? InitialBackoff { get; set; }

    public double? BackoffMultiplier { get; set; }

    public string? MaxBackoff { get; set; }

    public double? JitterRatio { get; set; }

    public string? OnExhaustion { get; set; }
}

internal sealed class ApprovalDocument
{
    public string? Role { get; set; }

    public string? Reason { get; set; }

    public bool? SegregationOfDuties { get; set; }
}

internal sealed class EdgeDocument
{
    public string? From { get; set; }

    public string? To { get; set; }

    public string? Kind { get; set; }

    public string? Guard { get; set; }

    public string? On { get; set; }
}

#pragma warning restore CA2227

/// <summary>Defaults applied where a workflow file omits an optional setting.</summary>
internal static class WorkflowDefaults
{
    public const JoinPolicy Join = JoinPolicy.All;

    public const EdgeKind Edge = EdgeKind.Forward;

    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(10);
}
