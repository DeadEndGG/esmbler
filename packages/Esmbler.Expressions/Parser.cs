namespace Esmbler.Expressions;

/// <summary>Recursive-descent parser. Grammar documented in docs/reference/expressions.md.</summary>
internal sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly int _maxDepth;
    private int _pos;
    private int _depth;

    private Parser(List<Token> tokens, int maxDepth)
    {
        _tokens = tokens;
        _maxDepth = maxDepth;
    }

    public static Node Parse(string source, ExpressionLimits limits)
    {
        var parser = new Parser(Lexer.Tokenize(source, limits.MaxLength), limits.MaxDepth);
        var node = parser.ParseExpr();
        if (parser.Peek.Kind != TokenKind.End)
        {
            throw new ExpressionException("expr.syntax", $"Unexpected '{parser.Peek.Text}'.", parser.Peek.Position);
        }

        return node;
    }

    private Token Peek => _tokens[_pos];

    private Token Next() => _tokens[_pos++];

    private bool IsOp(string op) => Peek.Kind == TokenKind.Op && Peek.Text == op;

    private bool IsIdent(string name) => Peek.Kind == TokenKind.Ident && Peek.Text == name;

    private void Expect(string op)
    {
        if (!IsOp(op))
        {
            var found = Peek.Kind == TokenKind.End ? "end of expression" : $"'{Peek.Text}'";
            throw new ExpressionException("expr.syntax", $"Expected '{op}' but found {found}.", Peek.Position);
        }

        _pos++;
    }

    private Node Enter(Func<Node> parse)
    {
        if (++_depth > _maxDepth)
        {
            throw new ExpressionException("expr.too_complex", $"Expression nesting exceeds {_maxDepth} levels.", Peek.Position);
        }

        try
        {
            return parse();
        }
        finally
        {
            _depth--;
        }
    }

    private Node ParseExpr() => Enter(() =>
    {
        var test = ParseOr();
        if (IsOp("?"))
        {
            var pos = Next().Position;
            var ifTrue = ParseExpr();
            Expect(":");
            var ifFalse = ParseExpr();
            return new Conditional(test, ifTrue, ifFalse, pos);
        }

        return test;
    });

    private Node ParseOr()
    {
        var left = ParseAnd();
        while (IsOp("||"))
        {
            var pos = Next().Position;
            left = new Binary("||", left, ParseAnd(), pos);
        }

        return left;
    }

    private Node ParseAnd()
    {
        var left = ParseRel();
        while (IsOp("&&"))
        {
            var pos = Next().Position;
            left = new Binary("&&", left, ParseRel(), pos);
        }

        return left;
    }

    private Node ParseRel()
    {
        var left = ParseAdd();
        if (Peek.Kind == TokenKind.Op && Peek.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
        {
            var op = Next();
            return new Binary(op.Text, left, ParseAdd(), op.Position);
        }

        if (IsIdent("in"))
        {
            var pos = Next().Position;
            return new Binary("in", left, ParseAdd(), pos);
        }

        return left;
    }

    private Node ParseAdd()
    {
        var left = ParseMul();
        while (Peek.Kind == TokenKind.Op && Peek.Text is "+" or "-")
        {
            var op = Next();
            left = new Binary(op.Text, left, ParseMul(), op.Position);
        }

        return left;
    }

    private Node ParseMul()
    {
        var left = ParseUnary();
        while (Peek.Kind == TokenKind.Op && Peek.Text is "*" or "/" or "%")
        {
            var op = Next();
            left = new Binary(op.Text, left, ParseUnary(), op.Position);
        }

        return left;
    }

    private Node ParseUnary()
    {
        if (Peek.Kind == TokenKind.Op && Peek.Text is "!" or "-")
        {
            var op = Next();
            return Enter(() => new Unary(op.Text, ParseUnary(), op.Position));
        }

        return ParseMember();
    }

    private Node ParseMember()
    {
        var node = ParsePrimary();
        while (true)
        {
            if (IsOp("."))
            {
                _pos++;
                var name = Next();
                if (name.Kind != TokenKind.Ident)
                {
                    throw new ExpressionException("expr.syntax", "Expected a field or method name after '.'.", name.Position);
                }

                if (IsOp("("))
                {
                    node = new Call(name.Text, node, ParseArgs(), name.Position);
                }
                else
                {
                    node = new Select(node, name.Text, name.Position);
                }
            }
            else if (IsOp("["))
            {
                var pos = Next().Position;
                var key = ParseExpr();
                Expect("]");
                node = new Index(node, key, pos);
            }
            else
            {
                return node;
            }
        }
    }

    private List<Node> ParseArgs()
    {
        Expect("(");
        var args = new List<Node>();
        if (!IsOp(")"))
        {
            do
            {
                args.Add(ParseExpr());
            }
            while (TryConsume(","));
        }

        Expect(")");
        return args;
    }

    private bool TryConsume(string op)
    {
        if (IsOp(op))
        {
            _pos++;
            return true;
        }

        return false;
    }

    private Node ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
            case TokenKind.String:
                _pos++;
                return new Literal(token.Value, token.Position);
            case TokenKind.Ident:
                _pos++;
                switch (token.Text)
                {
                    case "true":
                        return new Literal(true, token.Position);
                    case "false":
                        return new Literal(false, token.Position);
                    case "null":
                        return new Literal(null, token.Position);
                }

                if (IsOp("("))
                {
                    return new Call(token.Text, null, ParseArgs(), token.Position);
                }

                return new Ident(token.Text, token.Position);
            case TokenKind.Op when token.Text == "(":
                _pos++;
                var inner = ParseExpr();
                Expect(")");
                return inner;
            case TokenKind.Op when token.Text == "[":
                _pos++;
                var items = new List<Node>();
                if (!IsOp("]"))
                {
                    do
                    {
                        items.Add(ParseExpr());
                    }
                    while (TryConsume(","));
                }

                Expect("]");
                return new ListLiteral(items, token.Position);
            case TokenKind.End:
                throw new ExpressionException("expr.syntax", "Unexpected end of expression.", token.Position);
            default:
                throw new ExpressionException("expr.syntax", $"Unexpected '{token.Text}'.", token.Position);
        }
    }
}
