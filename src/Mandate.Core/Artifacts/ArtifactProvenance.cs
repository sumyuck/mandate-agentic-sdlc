using System.Collections.Immutable;
using Mandate.Core.Identifiers;

namespace Mandate.Core.Artifacts;

/// <summary>
/// The provenance graph over a run's artifacts.
/// </summary>
/// <remarks>
/// <para>
/// Two of the brief's requirements are answered by this one structure.
/// </para>
/// <para>
/// <b>Decision lineage.</b> <see cref="Ancestors"/> walks an artifact back to the roots it
/// was produced from, so the release checklist can be traced to the requirement that
/// justified it without anyone maintaining a trace document.
/// </para>
/// <para>
/// <b>Dynamic re-planning.</b> <see cref="Dependents"/> answers the question re-planning
/// turns on: if this input changed, what downstream output is no longer valid? Because
/// artifacts are content-addressed, "changed" is an exact digest comparison, so invalidation
/// is precise — only genuinely affected work is redone, rather than restarting the run.
/// </para>
/// <para>
/// The graph is acyclic by construction: an edge always points from a later artifact to the
/// earlier artifact it was derived from, and an artifact's digest cannot be known before its
/// content exists. Traversals still guard against revisiting nodes, so malformed input
/// degrades into a wrong answer rather than a hang.
/// </para>
/// </remarks>
public sealed class ArtifactProvenance
{
    private readonly ImmutableDictionary<Sha256Hash, Artifact> _byHash;
    private readonly ImmutableDictionary<Sha256Hash, ImmutableArray<Sha256Hash>> _dependents;

    private ArtifactProvenance(
        ImmutableDictionary<Sha256Hash, Artifact> byHash,
        ImmutableDictionary<Sha256Hash, ImmutableArray<Sha256Hash>> dependents)
    {
        _byHash = byHash;
        _dependents = dependents;
    }

    /// <summary>An empty graph.</summary>
    public static ArtifactProvenance Empty { get; } = new(
        ImmutableDictionary<Sha256Hash, Artifact>.Empty,
        ImmutableDictionary<Sha256Hash, ImmutableArray<Sha256Hash>>.Empty);

    /// <summary>Builds a graph over a set of artifacts.</summary>
    public static ArtifactProvenance Build(IEnumerable<Artifact> artifacts)
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        ImmutableDictionary<Sha256Hash, Artifact>.Builder byHash =
            ImmutableDictionary.CreateBuilder<Sha256Hash, Artifact>();

        Dictionary<Sha256Hash, List<Sha256Hash>> dependents = [];

        foreach (Artifact artifact in artifacts)
        {
            // Content addressing makes re-adding the same artifact a no-op rather than a conflict.
            byHash[artifact.Hash] = artifact;

            foreach (Sha256Hash input in artifact.DerivedFrom)
            {
                if (!dependents.TryGetValue(input, out List<Sha256Hash>? consumers))
                {
                    consumers = [];
                    dependents[input] = consumers;
                }

                if (!consumers.Contains(artifact.Hash))
                {
                    consumers.Add(artifact.Hash);
                }
            }
        }

        return new ArtifactProvenance(
            byHash.ToImmutable(),
            dependents.ToImmutableDictionary(
                entry => entry.Key,
                entry => entry.Value.ToImmutableArray()));
    }

    /// <summary>How many artifacts the graph holds.</summary>
    public int Count => _byHash.Count;

    /// <summary>Every artifact in the graph.</summary>
    public IEnumerable<Artifact> All => _byHash.Values;

    /// <summary>Looks up an artifact by its digest.</summary>
    public Artifact? Find(Sha256Hash hash) =>
        _byHash.TryGetValue(hash, out Artifact? artifact) ? artifact : null;

    /// <summary>Artifacts produced from no prior artifact.</summary>
    public IEnumerable<Artifact> Roots => _byHash.Values.Where(artifact => artifact.IsRoot);

    /// <summary>
    /// Every artifact <paramref name="hash"/> was derived from, transitively, nearest first.
    /// </summary>
    public ImmutableArray<Artifact> Ancestors(Sha256Hash hash) =>
        Traverse(hash, static (graph, current) =>
            graph.Find(current)?.DerivedFrom ?? ImmutableArray<Sha256Hash>.Empty);

    /// <summary>
    /// Every artifact derived from <paramref name="hash"/>, transitively, nearest first.
    /// </summary>
    /// <remarks>
    /// This is the invalidation set for re-planning: if the artifact at
    /// <paramref name="hash"/> is superseded, everything returned here was produced from
    /// stale input and must be reconsidered.
    /// </remarks>
    public ImmutableArray<Artifact> Dependents(Sha256Hash hash) =>
        Traverse(hash, static (graph, current) =>
            graph._dependents.TryGetValue(current, out ImmutableArray<Sha256Hash> consumers)
                ? consumers
                : ImmutableArray<Sha256Hash>.Empty);

    /// <summary>
    /// The nodes whose output is invalidated when the given artifacts change.
    /// </summary>
    /// <remarks>
    /// Collapses the artifact-level invalidation set onto workflow nodes, which is the form
    /// the scheduler needs in order to re-plan.
    /// </remarks>
    public ImmutableHashSet<NodeId> NodesInvalidatedBy(IEnumerable<Sha256Hash> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);

        ImmutableHashSet<NodeId>.Builder affected = ImmutableHashSet.CreateBuilder<NodeId>();

        foreach (Sha256Hash hash in changed)
        {
            foreach (Artifact dependent in Dependents(hash))
            {
                affected.Add(dependent.ProducedByNode);
            }
        }

        return affected.ToImmutable();
    }

    /// <summary>
    /// The lineage of an artifact as a reviewer-facing chain, from roots to the artifact.
    /// </summary>
    public ImmutableArray<Artifact> Lineage(Sha256Hash hash)
    {
        Artifact? target = Find(hash);

        if (target is null)
        {
            return [];
        }

        // Ancestors come back nearest-first; reversing puts the originating requirement
        // first, which is the order a reviewer reads a trace in.
        return [.. Ancestors(hash).Reverse(), target];
    }

    private ImmutableArray<Artifact> Traverse(
        Sha256Hash start,
        Func<ArtifactProvenance, Sha256Hash, ImmutableArray<Sha256Hash>> nextHops)
    {
        HashSet<Sha256Hash> seen = [start];
        Queue<Sha256Hash> frontier = new();
        ImmutableArray<Artifact>.Builder result = ImmutableArray.CreateBuilder<Artifact>();

        foreach (Sha256Hash hop in nextHops(this, start))
        {
            frontier.Enqueue(hop);
        }

        while (frontier.Count > 0)
        {
            Sha256Hash current = frontier.Dequeue();

            if (!seen.Add(current))
            {
                continue;
            }

            if (Find(current) is { } artifact)
            {
                result.Add(artifact);
            }

            foreach (Sha256Hash hop in nextHops(this, current))
            {
                frontier.Enqueue(hop);
            }
        }

        return result.ToImmutable();
    }
}
