using System.Collections.Immutable;
using Mandate.Core.Execution;

namespace Mandate.Orchestrator.Gates;

/// <summary>A registry built from a fixed set of evaluators.</summary>
public sealed class GateEvaluatorRegistry : IGateEvaluatorRegistry
{
    private readonly ImmutableDictionary<string, IGateEvaluator> _byKind;

    /// <summary>Creates a registry, rejecting two evaluators claiming the same kind.</summary>
    public GateEvaluatorRegistry(IEnumerable<IGateEvaluator> evaluators)
    {
        ArgumentNullException.ThrowIfNull(evaluators);

        ImmutableDictionary<string, IGateEvaluator>.Builder builder =
            ImmutableDictionary.CreateBuilder<string, IGateEvaluator>(StringComparer.Ordinal);

        foreach (IGateEvaluator evaluator in evaluators)
        {
            if (builder.ContainsKey(evaluator.Kind))
            {
                throw new ArgumentException(
                    $"Two evaluators claim gate kind '{evaluator.Kind}'. Which one judged a gate "
                    + "would depend on registration order, which is not a basis for a "
                    + "governance decision.",
                    nameof(evaluators));
            }

            builder[evaluator.Kind] = evaluator;
        }

        _byKind = builder.ToImmutable();
        KnownKinds = _byKind.Keys.ToImmutableHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlySet<string> KnownKinds { get; }

    /// <inheritdoc />
    public IGateEvaluator? Resolve(string kind) =>
        _byKind.TryGetValue(kind, out IGateEvaluator? evaluator) ? evaluator : null;

    /// <summary>Every evaluator, for documentation and diagnostics.</summary>
    public IEnumerable<IGateEvaluator> All =>
        _byKind.Values.OrderBy(evaluator => evaluator.Kind, StringComparer.Ordinal);
}
