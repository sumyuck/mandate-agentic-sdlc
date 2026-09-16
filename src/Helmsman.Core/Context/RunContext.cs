using System.Collections.Immutable;

namespace Helmsman.Core.Context;

/// <summary>
/// The append-only shared context that carries information across stages of a run.
/// </summary>
/// <remarks>
/// <para>
/// This is how "preserve cross-stage context" is implemented: instead of each stage passing a
/// bespoke payload to the next, stages contribute attributed facts and read the facts they are
/// entitled to. A stage therefore does not need to know which stage produced what, which is
/// what allows the graph to be re-planned and re-ordered without rewriting the agents.
/// </para>
/// <para>
/// Nothing is ever overwritten or removed. <see cref="Contribute"/> returns a new context with
/// the fact appended, so a context value is auditable — every revision, its author and its
/// supporting artifacts remain readable.
/// </para>
/// </remarks>
public sealed class RunContext
{
    private readonly ImmutableArray<ContextFact> _facts;

    private RunContext(ImmutableArray<ContextFact> facts) => _facts = facts;

    /// <summary>A context with no facts.</summary>
    public static RunContext Empty { get; } = new([]);

    /// <summary>Every fact, in the order contributed.</summary>
    public ImmutableArray<ContextFact> Facts => _facts;

    /// <summary>The number of facts, counting revisions separately.</summary>
    public int Count => _facts.Length;

    /// <summary>Distinct keys present, in alphabetical order.</summary>
    public ImmutableArray<string> Keys =>
        [.. _facts.Select(fact => fact.Key).Distinct().OrderBy(key => key, StringComparer.Ordinal)];

    /// <summary>Appends a fact, returning the resulting context.</summary>
    public RunContext Contribute(ContextFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return new RunContext(_facts.Add(fact));
    }

    /// <summary>The most recent value contributed for a key, or <see langword="null"/>.</summary>
    public ContextFact? Latest(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        ContextFact? latest = null;

        foreach (ContextFact fact in _facts)
        {
            if (string.Equals(fact.Key, key, StringComparison.Ordinal))
            {
                latest = fact;
            }
        }

        return latest;
    }

    /// <summary>Every revision of a key, oldest first.</summary>
    public ImmutableArray<ContextFact> History(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return [.. _facts.Where(fact => string.Equals(fact.Key, key, StringComparison.Ordinal))];
    }

    /// <summary>True when a key has been revised since it was first contributed.</summary>
    public bool WasRevised(string key) => History(key).Length > 1;

    /// <summary>
    /// A view restricted to the keys a stage is entitled to read.
    /// </summary>
    /// <remarks>
    /// Agents receive a scoped view rather than the whole context. Two reasons, one
    /// engineering and one security: a stage that cannot see unrelated facts cannot develop a
    /// hidden dependency on them, and a stage whose prompt is assembled from context cannot
    /// leak information it was never entitled to. Least privilege applies to context, not just
    /// to credentials.
    /// </remarks>
    /// <param name="patterns">
    /// Keys or prefix patterns, where <c>requirements.*</c> matches every key in the
    /// <c>requirements</c> namespace and <c>*</c> matches everything.
    /// </param>
    public RunContext ScopedTo(IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);

        ImmutableArray<string> allowed = [.. patterns];

        return new RunContext(
            [.. _facts.Where(fact => allowed.Any(pattern => Matches(fact.Key, pattern)))]);
    }

    private static bool Matches(string key, string pattern)
    {
        if (string.Equals(pattern, "*", StringComparison.Ordinal))
        {
            return true;
        }

        if (pattern.EndsWith(".*", StringComparison.Ordinal))
        {
            string prefix = pattern[..^1];
            return key.StartsWith(prefix, StringComparison.Ordinal);
        }

        return string.Equals(key, pattern, StringComparison.Ordinal);
    }
}
