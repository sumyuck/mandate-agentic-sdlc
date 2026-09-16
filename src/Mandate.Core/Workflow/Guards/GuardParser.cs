using System.Collections.Immutable;

namespace Mandate.Core.Workflow.Guards;

/// <summary>
/// Recursive-descent parser for the guard grammar.
/// </summary>
/// <remarks>
/// <code>
/// expression  := disjunction
/// disjunction := conjunction ( 'or' conjunction )*
/// conjunction := negation ( 'and' negation )*
/// negation    := 'not' negation | primary
/// primary     := '(' expression ')' | 'true' | 'false' | predicate
/// predicate   := key ( comparison operand | 'in' list | 'not' 'in' list )
/// comparison  := '==' | '!=' | '&lt;' | '&lt;=' | '&gt;' | '&gt;='
/// operand     := key | string | number | 'true' | 'false'
/// list        := '[' operand ( ',' operand )* ']'
/// </code>
/// <para>
/// A predicate must begin with a context key. Comparing two literals
/// (<c>'a' == 'a'</c>) is rejected: it is always either trivially true or a mistake, and
/// refusing it keeps the grammar's purpose — routing on run state — unambiguous.
/// </para>
/// </remarks>
internal sealed class GuardParser(string source, ImmutableArray<GuardToken> tokens)
{
    private int _position;

    public IGuardNode ParseExpression() => ParseDisjunction();

    public void ExpectEnd()
    {
        if (Current.Kind != GuardTokenKind.End)
        {
            throw Error($"Unexpected '{Current.Text}' after the end of the expression.");
        }
    }

    private GuardToken Current => tokens[_position];

    private IGuardNode ParseDisjunction()
    {
        IGuardNode left = ParseConjunction();

        while (Current.Kind == GuardTokenKind.Or)
        {
            _position++;
            left = new GuardOrNode(left, ParseConjunction());
        }

        return left;
    }

    private IGuardNode ParseConjunction()
    {
        IGuardNode left = ParseNegation();

        while (Current.Kind == GuardTokenKind.And)
        {
            _position++;
            left = new GuardAndNode(left, ParseNegation());
        }

        return left;
    }

    private IGuardNode ParseNegation()
    {
        if (Current.Kind != GuardTokenKind.Not)
        {
            return ParsePrimary();
        }

        _position++;
        return new GuardNotNode(ParseNegation());
    }

    private IGuardNode ParsePrimary()
    {
        switch (Current.Kind)
        {
            case GuardTokenKind.OpenParen:
                {
                    _position++;
                    IGuardNode inner = ParseDisjunction();

                    if (Current.Kind != GuardTokenKind.CloseParen)
                    {
                        throw Error("Expected ')'.");
                    }

                    _position++;
                    return inner;
                }

            case GuardTokenKind.True:
                _position++;
                return new GuardConstantNode(true);

            case GuardTokenKind.False:
                _position++;
                return new GuardConstantNode(false);

            case GuardTokenKind.Key:
                return ParsePredicate();

            case GuardTokenKind.String:
            case GuardTokenKind.Number:
                throw Error(
                    $"A condition must start with a context key, not the literal '{Current.Text}'. "
                    + "Comparing two literals is always either trivially true or a mistake.");

            case GuardTokenKind.End:
                throw Error("The expression ends where a condition was expected.");

            default:
                throw Error($"Unexpected '{Current.Text}' where a condition was expected.");
        }
    }

    private IGuardNode ParsePredicate()
    {
        string key = Current.Text;
        _position++;

        bool negatedMembership = false;

        if (Current.Kind == GuardTokenKind.Not)
        {
            // 'not in' is the only place 'not' may follow a key.
            if (tokens[_position + 1].Kind != GuardTokenKind.In)
            {
                throw Error($"Expected 'in' after 'not' in a membership test on '{key}'.");
            }

            negatedMembership = true;
            _position++;
        }

        if (Current.Kind == GuardTokenKind.In)
        {
            _position++;
            return new GuardInNode(key, ParseList(), negatedMembership);
        }

        GuardComparison comparison = Current.Kind switch
        {
            GuardTokenKind.Equal => GuardComparison.Equal,
            GuardTokenKind.NotEqual => GuardComparison.NotEqual,
            GuardTokenKind.Less => GuardComparison.Less,
            GuardTokenKind.LessOrEqual => GuardComparison.LessOrEqual,
            GuardTokenKind.Greater => GuardComparison.Greater,
            GuardTokenKind.GreaterOrEqual => GuardComparison.GreaterOrEqual,
            _ => throw Error(
                $"Expected a comparison or 'in' after the key '{key}'. A bare key is not a "
                + "condition; write '{0} == true' if you mean a boolean fact."
                    .Replace("{0}", key, StringComparison.Ordinal)),
        };

        _position++;
        (string operand, bool operandIsKey) = ParseOperand();

        return new GuardComparisonNode(key, comparison, operand, operandIsKey);
    }

    private ImmutableArray<string> ParseList()
    {
        if (Current.Kind != GuardTokenKind.OpenBracket)
        {
            throw Error("Expected '[' to begin a membership list.");
        }

        _position++;
        ImmutableArray<string>.Builder candidates = ImmutableArray.CreateBuilder<string>();

        while (true)
        {
            (string operand, bool operandIsKey) = ParseOperand();

            if (operandIsKey)
            {
                throw Error(
                    "A membership list may contain only literals, so that the set of accepted "
                    + "values is readable in the workflow file.");
            }

            candidates.Add(operand);

            if (Current.Kind == GuardTokenKind.Comma)
            {
                _position++;
                continue;
            }

            break;
        }

        if (Current.Kind != GuardTokenKind.CloseBracket)
        {
            throw Error("Expected ',' or ']' in a membership list.");
        }

        _position++;

        if (candidates.Count == 0)
        {
            throw Error("A membership list may not be empty.");
        }

        return candidates.ToImmutable();
    }

    private (string Value, bool IsKey) ParseOperand()
    {
        GuardToken token = Current;

        switch (token.Kind)
        {
            case GuardTokenKind.String:
            case GuardTokenKind.Number:
                _position++;
                return (token.Text, false);

            case GuardTokenKind.True:
            case GuardTokenKind.False:
                _position++;
                return (token.Kind == GuardTokenKind.True ? "true" : "false", false);

            case GuardTokenKind.Key:
                _position++;
                return (token.Text, true);

            default:
                throw Error(
                    $"Expected a value after the operator, found '{token.Text}'. Values are "
                    + "context keys, quoted strings, numbers, or true/false.");
        }
    }

    private GuardSyntaxException Error(string problem) =>
        new(source, Current.Position, problem);
}
