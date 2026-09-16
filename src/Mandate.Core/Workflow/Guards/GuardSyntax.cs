using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Mandate.Core.Workflow.Guards;

internal enum GuardTokenKind
{
    End,
    Key,
    String,
    Number,
    True,
    False,
    And,
    Or,
    Not,
    In,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    OpenParen,
    CloseParen,
    OpenBracket,
    CloseBracket,
    Comma,
}

internal readonly record struct GuardToken(GuardTokenKind Kind, string Text, int Position);

/// <summary>
/// Turns guard source text into tokens.
/// </summary>
/// <remarks>
/// The token set is closed and small. There is no token for a function call, an arithmetic
/// operator, an assignment, a member access or a statement separator — so those constructs
/// cannot be written, rather than being written and then rejected.
/// </remarks>
internal static class GuardLexer
{
    public static ImmutableArray<GuardToken> Tokenise(string source)
    {
        ImmutableArray<GuardToken>.Builder tokens = ImmutableArray.CreateBuilder<GuardToken>();
        int index = 0;

        while (index < source.Length)
        {
            char current = source[index];

            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            int start = index;

            switch (current)
            {
                case '(':
                    tokens.Add(new GuardToken(GuardTokenKind.OpenParen, "(", start));
                    index++;
                    continue;
                case ')':
                    tokens.Add(new GuardToken(GuardTokenKind.CloseParen, ")", start));
                    index++;
                    continue;
                case '[':
                    tokens.Add(new GuardToken(GuardTokenKind.OpenBracket, "[", start));
                    index++;
                    continue;
                case ']':
                    tokens.Add(new GuardToken(GuardTokenKind.CloseBracket, "]", start));
                    index++;
                    continue;
                case ',':
                    tokens.Add(new GuardToken(GuardTokenKind.Comma, ",", start));
                    index++;
                    continue;
                case '\'':
                case '"':
                    tokens.Add(ReadString(source, ref index));
                    continue;
            }

            if (TryReadOperator(source, ref index, out GuardToken op))
            {
                tokens.Add(op);
                continue;
            }

            if (char.IsAsciiDigit(current) || (current == '-' && index + 1 < source.Length && char.IsAsciiDigit(source[index + 1])))
            {
                tokens.Add(ReadNumber(source, ref index));
                continue;
            }

            if (char.IsAsciiLetter(current))
            {
                tokens.Add(ReadWord(source, ref index));
                continue;
            }

            throw new GuardSyntaxException(
                source,
                start,
                $"Unexpected character '{current}'. Guards may contain context keys, quoted "
                + "strings, numbers, true/false, the operators == != < <= > >= and in, and the "
                + "connectives and/or/not.");
        }

        tokens.Add(new GuardToken(GuardTokenKind.End, string.Empty, source.Length));
        return tokens.ToImmutable();
    }

    private static bool TryReadOperator(string source, ref int index, out GuardToken token)
    {
        (string Text, GuardTokenKind Kind)[] operators =
        [
            ("==", GuardTokenKind.Equal),
            ("!=", GuardTokenKind.NotEqual),
            ("<=", GuardTokenKind.LessOrEqual),
            (">=", GuardTokenKind.GreaterOrEqual),
            ("<", GuardTokenKind.Less),
            (">", GuardTokenKind.Greater),
        ];

        foreach ((string text, GuardTokenKind kind) in operators)
        {
            if (source.AsSpan(index).StartsWith(text, StringComparison.Ordinal))
            {
                token = new GuardToken(kind, text, index);
                index += text.Length;
                return true;
            }
        }

        // A single '=' is the most likely typo in hand-written YAML, so name the fix.
        if (source[index] == '=')
        {
            throw new GuardSyntaxException(
                source, index, "Use '==' for equality comparison, not '='.");
        }

        token = default;
        return false;
    }

    private static GuardToken ReadString(string source, ref int index)
    {
        char quote = source[index];
        int start = index;
        index++;

        StringBuilder value = new();

        while (index < source.Length && source[index] != quote)
        {
            if (source[index] == '\\' && index + 1 < source.Length)
            {
                index++;
            }

            value.Append(source[index]);
            index++;
        }

        if (index >= source.Length)
        {
            throw new GuardSyntaxException(source, start, "Unterminated string literal.");
        }

        index++;
        return new GuardToken(GuardTokenKind.String, value.ToString(), start);
    }

    private static GuardToken ReadNumber(string source, ref int index)
    {
        int start = index;

        if (source[index] == '-')
        {
            index++;
        }

        while (index < source.Length && (char.IsAsciiDigit(source[index]) || source[index] == '.'))
        {
            index++;
        }

        string text = source[start..index];

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            throw new GuardSyntaxException(source, start, $"'{text}' is not a valid number.");
        }

        return new GuardToken(GuardTokenKind.Number, text, start);
    }

    private static GuardToken ReadWord(string source, ref int index)
    {
        int start = index;

        while (index < source.Length
               && (char.IsAsciiLetterOrDigit(source[index]) || source[index] is '.' or '-' or '_'))
        {
            index++;
        }

        string text = source[start..index];

        GuardTokenKind kind = text switch
        {
            "and" => GuardTokenKind.And,
            "or" => GuardTokenKind.Or,
            "not" => GuardTokenKind.Not,
            "in" => GuardTokenKind.In,
            "true" => GuardTokenKind.True,
            "false" => GuardTokenKind.False,
            _ => GuardTokenKind.Key,
        };

        if (kind == GuardTokenKind.Key && !ContextFactIsKeyShaped(text))
        {
            throw new GuardSyntaxException(
                source,
                start,
                $"'{text}' is not a valid context key. Expected dot-separated lowercase "
                + "segments, such as 'run.scenario'.");
        }

        return new GuardToken(kind, text, start);
    }

    private static bool ContextFactIsKeyShaped(string text) => Context.ContextFact.IsValidKey(text);
}

/// <summary>Raised when guard source text cannot be parsed.</summary>
public sealed class GuardSyntaxException : Exception
{
    /// <summary>Creates the exception, pointing at the offending position in the source.</summary>
    public GuardSyntaxException(string source, int position, string problem)
        : base(Format(source, position, problem))
    {
        Source = source;
        Position = position;
        Problem = problem;
    }

    /// <summary>Creates the exception with no detail. Present to satisfy the exception pattern.</summary>
    public GuardSyntaxException()
        : this(string.Empty, 0, "Invalid guard expression.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public GuardSyntaxException(string message)
        : base(message)
    {
        Source = string.Empty;
        Problem = message;
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public GuardSyntaxException(string message, Exception innerException)
        : base(message, innerException)
    {
        Source = string.Empty;
        Problem = message;
    }

    /// <summary>The guard text that failed to parse.</summary>
    public new string Source { get; }

    /// <summary>Character offset at which the problem was detected.</summary>
    public int Position { get; }

    /// <summary>The problem, without the positional decoration.</summary>
    public string Problem { get; }

    private static string Format(string source, int position, string problem)
    {
        string caret = new string(' ', Math.Max(0, position)) + "^";

        return $"{problem}{Environment.NewLine}  {source}{Environment.NewLine}  {caret}";
    }
}
