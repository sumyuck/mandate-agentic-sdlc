using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Artifacts;

/// <summary>
/// A content-addressed engineering output, with the provenance of how it came to exist.
/// </summary>
/// <remarks>
/// <para>
/// Identity is the digest of the content, so the same bytes are always the same artifact and
/// a changed byte is always a different one. That single property does a lot of work:
/// deduplication is free, and "has this input changed?" — the question dynamic re-planning
/// turns on — becomes a digest comparison rather than a heuristic.
/// </para>
/// <para>
/// <see cref="DerivedFrom"/> is what makes decision lineage structural rather than
/// documentary. Because every artifact names the artifacts it was produced from, the set of
/// artifacts forms a provenance graph, and any output can be traced back to the requirement
/// it came from without anyone having written that trace down.
/// </para>
/// </remarks>
/// <param name="Hash">Digest of the content; also the artifact's identity.</param>
/// <param name="Kind">What this artifact is.</param>
/// <param name="Name">Reviewer-facing name, typically a workspace-relative path.</param>
/// <param name="MediaType">IANA media type of the content.</param>
/// <param name="SizeBytes">Size of the content in bytes.</param>
/// <param name="ProducedByNode">The workflow node that produced it.</param>
/// <param name="ProducedBy">The actor that produced it.</param>
/// <param name="ProducedAt">When it was produced.</param>
/// <param name="DerivedFrom">Digests of the artifacts this one was produced from.</param>
public sealed record Artifact(
    Sha256Hash Hash,
    ArtifactKind Kind,
    string Name,
    string MediaType,
    long SizeBytes,
    NodeId ProducedByNode,
    Actor ProducedBy,
    DateTimeOffset ProducedAt,
    ImmutableArray<Sha256Hash> DerivedFrom)
{
    /// <summary>Creates an artifact by hashing its content.</summary>
    public static Artifact FromContent(
        ArtifactKind kind,
        string name,
        string mediaType,
        ReadOnlySpan<byte> content,
        NodeId producedByNode,
        Actor producedBy,
        DateTimeOffset producedAt,
        IEnumerable<Sha256Hash>? derivedFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        if (kind == ArtifactKind.Unknown)
        {
            throw new ArgumentException(
                "An artifact must declare its kind; gates are written against kinds.", nameof(kind));
        }

        if (producedBy.IsEmpty)
        {
            throw new ArgumentException(
                "An artifact must name its producer; unattributed output has no provenance.",
                nameof(producedBy));
        }

        Sha256Hash hash = Sha256Hash.OfBytes(content);
        ImmutableArray<Sha256Hash> inputs = Normalise(derivedFrom);

        if (inputs.Contains(hash))
        {
            throw new ArgumentException(
                "An artifact cannot be derived from itself.", nameof(derivedFrom));
        }

        return new Artifact(
            hash, kind, name, mediaType, content.Length,
            producedByNode, producedBy, producedAt, inputs);
    }

    /// <summary>True when this artifact was produced from no prior artifact.</summary>
    public bool IsRoot => DerivedFrom.IsEmpty;

    /// <summary>A one-line description for logs and report tables.</summary>
    public string Describe() =>
        $"{Kind} {Name} ({Hash.Abbreviated}, {SizeBytes}B) by {ProducedBy} at {ProducedByNode}";

    private static ImmutableArray<Sha256Hash> Normalise(IEnumerable<Sha256Hash>? derivedFrom)
    {
        if (derivedFrom is null)
        {
            return [];
        }

        // Ordered and de-duplicated so that provenance is canonical: the same inputs listed
        // in a different order must not look like different provenance.
        return [.. derivedFrom.Distinct().OrderBy(hash => hash.Hex, StringComparer.Ordinal)];
    }
}
