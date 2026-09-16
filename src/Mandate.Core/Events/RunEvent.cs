using System.Globalization;
using System.Text.Json;
using Mandate.Core.Identifiers;
using Mandate.Core.Serialization;

namespace Mandate.Core.Events;

/// <summary>
/// One immutable, hash-linked entry in a run's audit log.
/// </summary>
/// <remarks>
/// <para>
/// The engine holds no mutable run state. Everything that happens is appended here, and the
/// current state of a run is a projection over its events. That is what makes resume, replay,
/// lineage and the reliability metrics fall out of one mechanism instead of four.
/// </para>
/// <para>
/// Each event commits to its predecessor via <see cref="PreviousHash"/>, and
/// <see cref="Hash"/> covers the whole envelope including that link. Editing, inserting,
/// reordering or deleting an event therefore breaks the chain at a detectable point — so the
/// log is not merely a record, it is evidence. See
/// <see cref="AuditChain"/> for verification.
/// </para>
/// <para>
/// The payload is carried as already-serialized canonical JSON rather than a polymorphic
/// object graph. Two reasons: the hashed bytes are exactly the bytes stored, with no
/// re-serialization step that could drift, and a new event kind cannot break the ability to
/// read historical events.
/// </para>
/// </remarks>
public sealed record RunEvent
{
    private RunEvent(
        long sequence,
        RunId runId,
        DateTimeOffset occurredAt,
        RunEventKind kind,
        NodeId? nodeId,
        Actor actor,
        string payloadJson,
        Sha256Hash previousHash,
        Sha256Hash hash)
    {
        Sequence = sequence;
        RunId = runId;
        OccurredAt = occurredAt;
        Kind = kind;
        NodeId = nodeId;
        Actor = actor;
        PayloadJson = payloadJson;
        PreviousHash = previousHash;
        Hash = hash;
    }

    /// <summary>Position in the run's stream. The first event is 1; there are no gaps.</summary>
    public long Sequence { get; }

    /// <summary>The run this event belongs to.</summary>
    public RunId RunId { get; }

    /// <summary>When the event occurred, in UTC, from the injected clock.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>What happened.</summary>
    public RunEventKind Kind { get; }

    /// <summary>The node concerned, or <see langword="null"/> for run-scoped events.</summary>
    public NodeId? NodeId { get; }

    /// <summary>Who performed the action.</summary>
    public Actor Actor { get; }

    /// <summary>Kind-specific detail as canonical JSON.</summary>
    public string PayloadJson { get; }

    /// <summary>Digest of the preceding event, or <see cref="Sha256Hash.Genesis"/> for the first.</summary>
    public Sha256Hash PreviousHash { get; }

    /// <summary>Digest of this event's canonical envelope, including <see cref="PreviousHash"/>.</summary>
    public Sha256Hash Hash { get; }

    /// <summary>Appends a new event to a chain, computing its digest.</summary>
    /// <param name="previous">The last event in the run's stream, or <see langword="null"/> to begin one.</param>
    public static RunEvent Append<TPayload>(
        RunEvent? previous,
        RunId runId,
        DateTimeOffset occurredAt,
        RunEventKind kind,
        NodeId? nodeId,
        Actor actor,
        TPayload payload)
    {
        if (previous is not null && previous.RunId != runId)
        {
            throw new ArgumentException(
                $"Cannot append an event for {runId} to the chain of {previous.RunId}.",
                nameof(previous));
        }

        string payloadJson = JsonSerializer.Serialize(payload, MandateJson.Canonical);

        return Create(
            sequence: (previous?.Sequence ?? 0) + 1,
            runId,
            occurredAt,
            kind,
            nodeId,
            actor,
            payloadJson,
            previousHash: previous?.Hash ?? Sha256Hash.Genesis);
    }

    /// <summary>Builds an event and computes its digest from its contents.</summary>
    public static RunEvent Create(
        long sequence,
        RunId runId,
        DateTimeOffset occurredAt,
        RunEventKind kind,
        NodeId? nodeId,
        Actor actor,
        string payloadJson,
        Sha256Hash previousHash)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);

        if (kind == RunEventKind.Unknown)
        {
            throw new ArgumentException("An event must have a known kind.", nameof(kind));
        }

        if (actor.IsEmpty)
        {
            throw new ArgumentException(
                "An event must name its actor; unattributed actions are not auditable.",
                nameof(actor));
        }

        Sha256Hash hash = ComputeHash(
            sequence, runId, occurredAt, kind, nodeId, actor, payloadJson, previousHash);

        return new RunEvent(
            sequence, runId, occurredAt, kind, nodeId, actor, payloadJson, previousHash, hash);
    }

    /// <summary>
    /// Reconstructs an event from storage, preserving the digest that was recorded.
    /// </summary>
    /// <remarks>
    /// The stored digest is kept as-is rather than recomputed, because a mismatch between
    /// stored and recomputed digests is exactly the tampering signal that
    /// <see cref="AuditChain.Verify"/> exists to surface. Recomputing here would erase it.
    /// </remarks>
    public static RunEvent Rehydrate(
        long sequence,
        RunId runId,
        DateTimeOffset occurredAt,
        RunEventKind kind,
        NodeId? nodeId,
        Actor actor,
        string payloadJson,
        Sha256Hash previousHash,
        Sha256Hash storedHash)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);

        return new RunEvent(
            sequence, runId, occurredAt, kind, nodeId, actor, payloadJson, previousHash, storedHash);
    }

    /// <summary>Recomputes this event's digest from its contents.</summary>
    public Sha256Hash RecomputeHash() => ComputeHash(
        Sequence, RunId, OccurredAt, Kind, NodeId, Actor, PayloadJson, PreviousHash);

    /// <summary>Deserializes the payload as <typeparamref name="TPayload"/>.</summary>
    public TPayload Payload<TPayload>() =>
        JsonSerializer.Deserialize<TPayload>(PayloadJson, MandateJson.Canonical)
        ?? throw new InvalidOperationException(
            $"Event {Sequence} of {RunId} has a payload that is not a {typeof(TPayload).Name}.");

    private static Sha256Hash ComputeHash(
        long sequence,
        RunId runId,
        DateTimeOffset occurredAt,
        RunEventKind kind,
        NodeId? nodeId,
        Actor actor,
        string payloadJson,
        Sha256Hash previousHash)
    {
        // A flat, explicitly ordered preimage. Every field that a reviewer would care about
        // is covered, and the round-trip ("O") timestamp format keeps the bytes stable
        // across cultures and platforms.
        Preimage preimage = new(
            Sequence: sequence,
            RunId: runId.Value,
            OccurredAt: occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Kind: kind.ToString(),
            NodeId: nodeId?.Value,
            Actor: actor.Value,
            PayloadJson: payloadJson,
            PreviousHash: previousHash.Hex);

        return Sha256Hash.OfUtf8(JsonSerializer.Serialize(preimage, MandateJson.Canonical));
    }

    private sealed record Preimage(
        long Sequence,
        string RunId,
        string OccurredAt,
        string Kind,
        string? NodeId,
        string Actor,
        string PayloadJson,
        string PreviousHash);
}
