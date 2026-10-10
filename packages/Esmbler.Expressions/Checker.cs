namespace Esmbler.Expressions;

/// <summary>Static checks: node budget, known variables/fields/functions, definite type mismatches.</summary>
internal sealed class Checker
{
    private static readonly Dictionary<string, (int MinArgs, int MaxArgs, bool Method, bool Global)> Functions = new()
    {
        ["size"] = (0, 1, true, true),
        ["has"] = (1, 1, false, true),
        ["startsWith"] = (1, 1, true, false),
        ["endsWith"] = (1, 1, true, false),
        ["contains"] = (1, 1, true, false),
        ["matches"] = (1, 1, true, false),
        ["lowerAscii"] = (0, 0, true, false),
        ["upperAscii"] = (0, 0, true, false),
        ["trim"] = (0, 0, true, false),
        ["int"] = (1, 1, false, true),
        ["double"] = (1, 1, false, true),
        ["string"] = (1, 1, false, true),
    };

    private readonly ExpressionSchema? _schema;
    private readonly ExpressionLimits _limits;
    private int _nodes;

    private Checker(ExpressionSchema? schema, ExpressionLimits limits)
    {
        _schema = schema;
        _limits = limits;
    }

    public HashSet<string> References { get; } = new(StringComparer.Ordinal);

    public static Checker Run(Node root, ExpressionSchema? schema, ExpressionLimits limits)
    {
        var checker = new Checker(schema, limits);
        checker.Visit(root);
        return checker;
    }

    private ValueType Visit(Node node)
    {
        if (++_nodes > _limits.MaxNodes)
        {
            throw new ExpressionException("expr.too_complex", $"Expression has more than {_limits.MaxNodes} parts. Split it into several rules.", node.Position);
        }

        switch (node)
        {
            case Literal lit:
                return lit.Value switch
                {
                    null => ValueType.Null,
                    bool => ValueType.Bool,
                    double => ValueType.Number,
                    string => ValueType.String,
                    _ => ValueType.Any,
                };
            case Ident id:
                if (_schema is null)
                {
                    return ValueType.Any;
                }

                if (!_schema.Variables.TryGetValue(id.Name, out var v))
                {
                    var suggestion = Suggest(id.Name, _schema.Variables.Keys);
                    throw new ExpressionException(
                        "expr.unknown_variable",
                        $"Unknown name '{id.Name}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"),
                        id.Position,
                        suggestion);
                }

                return v.Type;
            case Select sel:
                return VisitSelect(sel);
            case Index idx:
                Visit(idx.Operand);
                Visit(idx.Key);
                return ValueType.Any;
            case ListLiteral list:
                foreach (var item in list.Items)
                {
                    Visit(item);
                }

                return ValueType.List;
            case Unary un:
                var operand = Visit(un.Operand);
                if (un.Op == "!")
                {
                    RequireType(operand, un.Position, "'!' needs a true/false value", ValueType.Bool);
                    return ValueType.Bool;
                }

                RequireType(operand, un.Position, "'-' needs a number", ValueType.Number);
                return operand;
            case Conditional cond:
                RequireType(Visit(cond.Test), cond.Position, "The condition before '?' must be true/false", ValueType.Bool);
                var a = Visit(cond.IfTrue);
                var b = Visit(cond.IfFalse);
                return a == b ? a : ValueType.Any;
            case Binary bin:
                return VisitBinary(bin);
            case Call call:
                return VisitCall(call);
            default:
                throw new ExpressionException("expr.internal", "Unknown node.", node.Position);
        }
    }

    private ValueType VisitSelect(Select sel)
    {
        if (sel.Operand is Ident plain && _schema is null)
        {
            References.Add($"{plain.Name}.{sel.Field}");
        }

        if (sel.Operand is Ident root && _schema is not null && _schema.Variables.TryGetValue(root.Name, out var variable))
        {
            Visit(sel.Operand);
            References.Add($"{root.Name}.{sel.Field}");
            if (variable.Fields is null)
            {
                return ValueType.Any;
            }

            if (!variable.Fields.TryGetValue(sel.Field, out var fieldType))
            {
                var suggestion = Suggest(sel.Field, variable.Fields.Keys);
                throw new ExpressionException(
                    "expr.unknown_field",
                    $"Unknown field '{root.Name}.{sel.Field}'." + (suggestion is null ? string.Empty : $" Did you mean '{root.Name}.{suggestion}'?"),
                    sel.Position,
                    suggestion is null ? null : $"{root.Name}.{suggestion}");
            }

            return fieldType;
        }

        Visit(sel.Operand);
        return ValueType.Any;
    }

    private ValueType VisitBinary(Binary bin)
    {
        var l = Visit(bin.Left);
        var r = Visit(bin.Right);
        switch (bin.Op)
        {
            case "&&":
            case "||":
                RequireType(l, bin.Position, $"'{bin.Op}' needs true/false values", ValueType.Bool);
                RequireType(r, bin.Position, $"'{bin.Op}' needs true/false values", ValueType.Bool);
                return ValueType.Bool;
            case "==":
            case "!=":
            case "<":
            case "<=":
            case ">":
            case ">=":
                CheckComparable(bin, l, r);
                return ValueType.Bool;
            case "in":
                return ValueType.Bool;
            case "+":
                if (l == ValueType.String || r == ValueType.String)
                {
                    return ValueType.String;
                }

                return Numeric(l, r);
            default:
                RequireType(l, bin.Position, $"'{bin.Op}' needs numbers", ValueType.Number);
                RequireType(r, bin.Position, $"'{bin.Op}' needs numbers", ValueType.Number);
                return Numeric(l, r);
        }
    }

    private static ValueType Numeric(ValueType l, ValueType r) =>
        l == ValueType.Number && r == ValueType.Number ? ValueType.Number : ValueType.Any;

    private static void CheckComparable(Binary bin, ValueType l, ValueType r)
    {
        if (l is ValueType.Any or ValueType.Null || r is ValueType.Any or ValueType.Null)
        {
            return;
        }

        if (l != r)
        {
            var hint = describe(bin.Left) is { } field
                ? $" {field} is {Article(l)}; compare it with {Article(l)} value."
                : string.Empty;
            throw new ExpressionException(
                "expr.type_mismatch",
                $"Cannot compare {Article(l)} with {Article(r)} using '{bin.Op}'.{hint}",
                bin.Position);
        }

        if (bin.Op is "<" or "<=" or ">" or ">=" && l is not (ValueType.String or ValueType.Number))
        {
            throw new ExpressionException("expr.type_mismatch", $"'{bin.Op}' only compares numbers or text.", bin.Position);
        }

        static string? describe(Node n) => n is Select { Operand: Ident id } s ? $"'{id.Name}.{s.Field}'" : null;
    }

    private static string Article(ValueType t) => t switch
    {
        ValueType.Number => "a number",
        ValueType.String => "text",
        ValueType.Bool => "true/false",
        ValueType.List => "a list",
        ValueType.Map => "a map",
        _ => "a value",
    };

    private ValueType VisitCall(Call call)
    {
        if (!Functions.TryGetValue(call.Function, out var def))
        {
            var suggestion = Suggest(call.Function, Functions.Keys);
            throw new ExpressionException(
                "expr.unknown_function",
                $"Unknown function '{call.Function}'." + (suggestion is null ? string.Empty : $" Did you mean '{suggestion}'?"),
                call.Position,
                suggestion);
        }

        var isMethod = call.Target is not null;
        if ((isMethod && !def.Method) || (!isMethod && !def.Global))
        {
            throw new ExpressionException(
                "expr.bad_call",
                isMethod ? $"'{call.Function}' is not a method; call it as {call.Function}(value)." : $"'{call.Function}' is a method; call it as value.{call.Function}(...).",
                call.Position);
        }

        var arity = call.Args.Count + (isMethod && call.Function == "size" ? 1 : 0);
        if (arity < def.MinArgs || arity > def.MaxArgs || (call.Function == "size" && arity != 1))
        {
            throw new ExpressionException("expr.bad_call", $"'{call.Function}' was given the wrong number of arguments.", call.Position);
        }

        if (call.Function == "has")
        {
            if (call.Args[0] is not Select)
            {
                throw new ExpressionException("expr.bad_call", "has() takes a field selection, e.g. has(record.justification).", call.Position);
            }

            Visit(call.Args[0]);
            return ValueType.Bool;
        }

        if (call.Target is not null)
        {
            Visit(call.Target);
        }

        foreach (var arg in call.Args)
        {
            Visit(arg);
        }

        return call.Function switch
        {
            "size" or "int" or "double" => ValueType.Number,
            "string" or "lowerAscii" or "upperAscii" or "trim" => ValueType.String,
            _ => ValueType.Bool,
        };
    }

    private static void RequireType(ValueType actual, int position, string message, params ValueType[] allowed)
    {
        if (actual is ValueType.Any || allowed.Contains(actual))
        {
            return;
        }

        throw new ExpressionException("expr.type_mismatch", message + ".", position);
    }

    internal static string? Suggest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            var d = Levenshtein(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        return best is not null && bestDistance <= Math.Max(2, name.Length / 3) ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
