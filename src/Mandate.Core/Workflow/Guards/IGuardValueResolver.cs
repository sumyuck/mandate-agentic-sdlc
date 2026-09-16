namespace Mandate.Core.Workflow.Guards;

/// <summary>
/// Supplies the values a guard expression refers to.
/// </summary>
/// <remarks>
/// Guards read from the run's shared context, but the grammar is deliberately unaware of
/// where values come from: it can resolve a key or it cannot. That keeps the expression
/// language free of any notion of objects, methods or navigation, which is what makes it
/// safe to evaluate configuration authored outside the engine.
/// </remarks>
public interface IGuardValueResolver
{
    /// <summary>Resolves a context key to its current value.</summary>
    /// <returns><see langword="true"/> when the key is present.</returns>
    bool TryResolve(string key, out string? value);
}

/// <summary>A resolver backed by a plain dictionary, for tests and simple callers.</summary>
public sealed class DictionaryGuardValueResolver(IReadOnlyDictionary<string, string> values)
    : IGuardValueResolver
{
    private readonly IReadOnlyDictionary<string, string> _values =
        values ?? throw new ArgumentNullException(nameof(values));

    /// <inheritdoc />
    public bool TryResolve(string key, out string? value) => _values.TryGetValue(key, out value);
}
