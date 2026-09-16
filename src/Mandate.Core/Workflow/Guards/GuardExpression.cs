using System.Collections.Immutable;
using System.Globalization;

namespace Mandate.Core.Workflow.Guards;

/// <summary>The outcome of evaluating a guard, with the reasoning that produced it.</summary>
/// <param name="Value">Whether the guarded path is taken.</param>
/// <param name="Explanation">
/// Why, in reviewer-facing terms, including the values that were compared. Recorded on the
/// audit event for the edge so a skipped branch can be explained months later.
/// </param>
public sealed record GuardEvaluation(bool Value, string Explanation);

/// <summary>
/// A compiled guard: a boolean condition over the run's shared context.
/// </summary>
/// <remarks>
/// <para>
/// Guards decide whether a conditional path in the lifecycle is taken — the brownfield-only
/// impact-analysis branch, for instance. They are authored in YAML, which means they are
/// configuration supplied to the engine rather than code compiled into it.
/// </para>
/// <para>
/// The grammar is therefore deliberately closed: context key references, string, number and
/// boolean literals, the comparisons <c>== != &lt; &lt;= &gt; &gt;=</c>, membership via
/// <c>in [...]</c>, and the connectives <c>and</c>, <c>or</c>, <c>not</c>. There are no
/// function calls, no arithmetic, no member access and no way to reach a type or a method.
/// A general-purpose expression evaluator would have turned a change-controlled configuration
/// file into a code-execution surface; see docs/adr/0008.
/// </para>
/// <para>
/// Evaluation fails closed. A guard referring to a key that is absent from the context throws
/// rather than evaluating to false, because silently-false would skip a lifecycle stage — and
/// a stage skipped by a typo is a governance failure, not a routing decision.
/// </para>
/// </remarks>
public sealed class GuardExpression
{
    private readonly IGuardNode _root;

    private GuardExpression(string source, IGuardNode root, ImmutableHashSet<string> referencedKeys)
    {
        Source = source;
        _root = root;
        ReferencedKeys = referencedKeys;
    }

    /// <summary>The original source text.</summary>
    public string Source { get; }

    /// <summary>
    /// Every context key this guard reads.
    /// </summary>
    /// <remarks>
    /// Collected at parse time so workflow validation can check, before any run exists, that
    /// each key is one some stage actually contributes — catching a typo at load rather than
    /// as a mysteriously skipped node mid-run.
    /// </remarks>
    public ImmutableHashSet<string> ReferencedKeys { get; }

    /// <summary>Parses guard source, throwing on malformed input.</summary>
    /// <exception cref="GuardSyntaxException">The source is not a valid guard.</exception>
    public static GuardExpression Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new GuardSyntaxException(source, 0, "A guard may not be empty.");
        }

        ImmutableArray<GuardToken> tokens = GuardLexer.Tokenise(source);
        GuardParser parser = new(source, tokens);
        IGuardNode root = parser.ParseExpression();
        parser.ExpectEnd();

        HashSet<string> keys = [];
        root.CollectKeys(keys);

        return new GuardExpression(source, root, [.. keys]);
    }

    /// <summary>Parses guard source without throwing.</summary>
    public static bool TryParse(string? source, out GuardExpression? expression, out string? error)
    {
        try
        {
            expression = Parse(source ?? string.Empty);
            error = null;
            return true;
        }
        catch (GuardSyntaxException exception)
        {
            expression = null;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>Evaluates the guard against the supplied values.</summary>
    /// <exception cref="GuardEvaluationException">A referenced key is absent.</exception>
    public GuardEvaluation Evaluate(IGuardValueResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        bool value = _root.Evaluate(resolver, out string explanation);
        return new GuardEvaluation(value, explanation);
    }

    /// <inheritdoc />
    public override string ToString() => Source;
}

/// <summary>Raised when a guard cannot be evaluated against the current context.</summary>
public sealed class GuardEvaluationException : Exception
{
    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public GuardEvaluationException()
        : base("The guard could not be evaluated against the current context.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public GuardEvaluationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public GuardEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The context key that could not be resolved, when that was the cause.</summary>
    public string? Key { get; private init; }

    /// <summary>Creates the exception for a key no stage has contributed.</summary>
    public static GuardEvaluationException MissingKey(string key) =>
        new($"The guard refers to context key '{key}', which no stage has contributed. "
            + "Guards fail closed rather than evaluating to false, because a lifecycle stage "
            + "skipped by a typo is a governance failure rather than a routing decision.")
        {
            Key = key,
        };
}

internal interface IGuardNode
{
    bool Evaluate(IGuardValueResolver resolver, out string explanation);

    void CollectKeys(HashSet<string> keys);
}

internal sealed class GuardAndNode(IGuardNode left, IGuardNode right) : IGuardNode
{
    public bool Evaluate(IGuardValueResolver resolver, out string explanation)
    {
        bool leftValue = left.Evaluate(resolver, out string leftWhy);

        if (!leftValue)
        {
            // Short-circuits, and the explanation says which half decided it.
            explanation = $"({leftWhy}) and … → false";
            return false;
        }

        bool rightValue = right.Evaluate(resolver, out string rightWhy);
        explanation = $"({leftWhy}) and ({rightWhy}) → {Format(rightValue)}";
        return rightValue;
    }

    public void CollectKeys(HashSet<string> keys)
    {
        left.CollectKeys(keys);
        right.CollectKeys(keys);
    }

    internal static string Format(bool value) => value ? "true" : "false";
}

internal sealed class GuardOrNode(IGuardNode left, IGuardNode right) : IGuardNode
{
    public bool Evaluate(IGuardValueResolver resolver, out string explanation)
    {
        bool leftValue = left.Evaluate(resolver, out string leftWhy);

        if (leftValue)
        {
            explanation = $"({leftWhy}) or … → true";
            return true;
        }

        bool rightValue = right.Evaluate(resolver, out string rightWhy);
        explanation = $"({leftWhy}) or ({rightWhy}) → {GuardAndNode.Format(rightValue)}";
        return rightValue;
    }

    public void CollectKeys(HashSet<string> keys)
    {
        left.CollectKeys(keys);
        right.CollectKeys(keys);
    }
}

internal sealed class GuardNotNode(IGuardNode inner) : IGuardNode
{
    public bool Evaluate(IGuardValueResolver resolver, out string explanation)
    {
        bool value = inner.Evaluate(resolver, out string why);
        explanation = $"not ({why}) → {GuardAndNode.Format(!value)}";
        return !value;
    }

    public void CollectKeys(HashSet<string> keys) => inner.CollectKeys(keys);
}

internal sealed class GuardConstantNode(bool value) : IGuardNode
{
    public bool Evaluate(IGuardValueResolver resolver, out string explanation)
    {
        explanation = GuardAndNode.Format(value);
        return value;
    }

    public void CollectKeys(HashSet<string> keys)
    {
    }
}

internal enum GuardComparison
{
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
}

internal sealed class GuardComparisonNode(
    string key, GuardComparison comparison, string operand, bool operandIsKey) : IGuardNode
{
    public bool Evaluate(IGuardValueResolver resolver, out string explanation)
    {
        string left = GuardOperand.Resolve(resolver, key);
        string right = operandIsKey ? GuardOperand.Resolve(resolver, operand) : operand;

        bool value = Compare(left, right);

        explanation = $"{key} ('{left}') {Symbol()} "
                      + (operandIsKey ? $"{operand} ('{right}')" : $"'{right}'")
                      + $" → {GuardAndNode.Format(value)}";

        return value;
    }

    public void CollectKeys(HashSet<string> keys)
    {
        keys.Add(key);

        if (operandIsKey)
        {
            keys.Add(operand);
        }
    }

    private bool Compare(string left, string right)
    {
        bool leftIsNumber = decimal.TryParse(
            left, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal leftNumber);
        bool rightIsNumber = decimal.TryParse(
            right, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rightNumber);
        bool numeric = leftIsNumber && rightIsNumber;

        if (comparison is GuardComparison.Equal or GuardComparison.NotEqual)
        {
            bool equal = numeric
                ? leftNumber == rightNumber
                : string.Equals(left, right, StringComparison.Ordinal);

            return comparison == GuardComparison.Equal ? equal : !equal;
        }

        if (!numeric)
        {
            throw new GuardEvaluationException(
                $"Cannot order-compare '{left}' and '{right}': {Symbol()} requires numbers on "
                + "both sides. Use == or != to compare text.");
        }

        int order = leftNumber.CompareTo(rightNumber);

        return comparison switch
        {
            GuardComparison.Less => order < 0,
            GuardComparison.LessOrEqual => order <= 0,
            GuardComparison.Greater => order > 0,
            GuardComparison.GreaterOrEqual => order >= 0,
            _ => throw new InvalidOperationException($"Unhandled comparison {comparison}."),
        };
    }

    private string Symbol() => comparison switch
    {
        GuardComparison.Equal => "==",
        GuardComparison.NotEqual => "!=",
        GuardComparison.Less => "<",
        GuardComparison.LessOrEqual => "<=",
        GuardComparison.Greater => ">",
        GuardComparison.GreaterOrEqual => ">=",
        _ => "?",
    };
}

internal sealed class GuardInNode(string key, ImmutableArray<string> candidates, bool negated)
    : IGuardNode
{
    public bool Evaluate(IGuardValueResolver resolver, out string explanation)
    {
        string actual = GuardOperand.Resolve(resolver, key);
        bool contained = candidates.Contains(actual, StringComparer.Ordinal);
        bool value = negated ? !contained : contained;

        explanation = $"{key} ('{actual}') {(negated ? "not in" : "in")} "
                      + $"[{string.Join(", ", candidates.Select(candidate => $"'{candidate}'"))}] "
                      + $"→ {GuardAndNode.Format(value)}";

        return value;
    }

    public void CollectKeys(HashSet<string> keys) => keys.Add(key);
}

internal static class GuardOperand
{
    public static string Resolve(IGuardValueResolver resolver, string key) =>
        resolver.TryResolve(key, out string? value) && value is not null
            ? value
            : throw GuardEvaluationException.MissingKey(key);
}
