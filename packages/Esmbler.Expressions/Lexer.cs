using System.Globalization;
using System.Text;

namespace Esmbler.Expressions;

internal enum TokenKind
{
    Number,
    String,
    Ident,
    Op,
    End,
}

internal readonly record struct Token(TokenKind Kind, string Text, object? Value, int Position);

internal static class Lexer
{
    private static readonly string[] Ops =
    [
        "==", "!=", "<=", ">=", "&&", "||", "<", ">", "!", "+", "-", "*", "/", "%", "(", ")", "[", "]", ".", ",", "?", ":",
    ];

    public static List<Token> Tokenize(string source, int maxLength)
    {
        if (source.Length > maxLength)
        {
            throw new ExpressionException("expr.too_long", $"Expression is longer than {maxLength} characters.");
        }

        var tokens = new List<Token>();
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < source.Length && char.IsAsciiDigit(source[i]))
                {
                    i++;
                }

                if (i + 1 < source.Length && source[i] == '.' && char.IsAsciiDigit(source[i + 1]))
                {
                    i++;
                    while (i < source.Length && char.IsAsciiDigit(source[i]))
                    {
                        i++;
                    }
                }

                var text = source[start..i];
                tokens.Add(new Token(TokenKind.Number, text, double.Parse(text, CultureInfo.InvariantCulture), start));

                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Ident, source[start..i], null, start));
                continue;
            }

            if (c is '\'' or '"')
            {
                var start = i;
                var quote = c;
                i++;
                var sb = new StringBuilder();
                var closed = false;
                while (i < source.Length)
                {
                    var ch = source[i];
                    if (ch == '\\' && i + 1 < source.Length)
                    {
                        var next = source[i + 1];
                        sb.Append(next switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => next,
                        });
                        i += 2;
                        continue;
                    }

                    if (ch == quote)
                    {
                        closed = true;
                        i++;
                        break;
                    }

                    sb.Append(ch);
                    i++;
                }

                if (!closed)
                {
                    throw new ExpressionException("expr.syntax", "Unterminated string literal.", start);
                }

                tokens.Add(new Token(TokenKind.String, source[start..i], sb.ToString(), start));
                continue;
            }

            var matched = false;
            foreach (var op in Ops)
            {
                if (string.CompareOrdinal(source, i, op, 0, op.Length) == 0)
                {
                    tokens.Add(new Token(TokenKind.Op, op, null, i));
                    i += op.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                throw new ExpressionException("expr.syntax", $"Unexpected character '{c}'.", i);
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, null, source.Length));
        return tokens;
    }
}
