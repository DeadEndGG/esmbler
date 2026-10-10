using System.Globalization;
using System.Text.RegularExpressions;

namespace Esmbler.Expressions;

/// <summary>
/// Tree-walking evaluator. Semantics (shared with the TypeScript evaluator, see conformance/cases.json):
/// absent map keys read as null; selecting through null yields null; ordering comparisons with null are false;
/// arithmetic with null yields null; there is one number type (IEEE double, like JSON/JavaScript); &amp;&amp;/|| absorb errors when the other side decides the result.
/// </summary>
internal static class Evaluator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    public static object? Eval(Node node, IReadOnlyDictionary<string, object?> vars)
    {
        switch (node)
        {
            case Literal lit:
                return lit.Value;
            case Ident id:
                if (!vars.TryGetValue(id.Name, out var v))
                {
                    throw new ExpressionException("expr.unknown_variable", $"Unknown name '{id.Name}'.", id.Position);
                }

                return v;
            case Select sel:
                return SelectField(Eval(sel.Operand, vars), sel.Field, sel.Position);
            case Index idx:
                return IndexValue(Eval(idx.Operand, vars), Eval(idx.Key, vars), idx.Position);
            case ListLiteral list:
                return list.Items.Select(i => Eval(i, vars)).ToList();
            case Unary un:
                return EvalUnary(un, Eval(un.Operand, vars));
            case Conditional cond:
                var test = Eval(cond.Test, vars);
                if (test is not bool b)
                {
                    throw new ExpressionException("expr.type_error", "Condition before '?' is not true/false.", cond.Position);
                }

                return b ? Eval(cond.IfTrue, vars) : Eval(cond.IfFalse, vars);
            case Binary bin:
                return EvalBinary(bin, vars);
            case Call call:
                return EvalCall(call, vars);
            default:
                throw new ExpressionException("expr.internal", "Unknown node.", node.Position);
        }
    }

    private static object? SelectField(object? target, string field, int position) => target switch
    {
        null => null,
        IReadOnlyDictionary<string, object?> map => map.TryGetValue(field, out var value) ? value : null,
        _ => throw new ExpressionException("expr.type_error", $"Cannot read '.{field}' from {TypeName(target)}.", position),
    };

    private static object? IndexValue(object? target, object? key, int position)
    {
        switch (target)
        {
            case null:
                return null;
            case IReadOnlyList<object?> list when key is double i:
                if (i < 0 || i >= list.Count || i != Math.Floor(i))
                {
                    throw new ExpressionException("expr.index_out_of_range", $"Index {FormatNumber(i)} is out of range.", position);
                }

                return list[(int)i];
            case IReadOnlyDictionary<string, object?> map when key is string k:
                return map.TryGetValue(k, out var value) ? value : null;
            default:
                throw new ExpressionException("expr.type_error", $"Cannot index {TypeName(target)} with {TypeName(key)}.", position);
        }
    }

    private static object? EvalUnary(Unary un, object? value)
    {
        if (un.Op == "!")
        {
            return value is bool b
                ? !b
                : throw new ExpressionException("expr.type_error", $"'!' needs true/false, got {TypeName(value)}.", un.Position);
        }

        return value switch
        {
            null => null,
            double d => -d,
            _ => throw new ExpressionException("expr.type_error", $"'-' needs a number, got {TypeName(value)}.", un.Position),
        };
    }

    private static object? EvalBinary(Binary bin, IReadOnlyDictionary<string, object?> vars)
    {
        if (bin.Op is "&&" or "||")
        {
            return EvalLogical(bin, vars);
        }

        var l = Eval(bin.Left, vars);
        var r = Eval(bin.Right, vars);
        switch (bin.Op)
        {
            case "==":
                return ValueEquals(l, r);
            case "!=":
                return !ValueEquals(l, r);
            case "<":
            case "<=":
            case ">":
            case ">=":
                if (l is null || r is null)
                {
                    return false;
                }

                var c = CompareOrdered(l, r, bin);
                return bin.Op switch
                {
                    "<" => c < 0,
                    "<=" => c <= 0,
                    ">" => c > 0,
                    _ => c >= 0,
                };
            case "in":
                return r switch
                {
                    null => false,
                    IReadOnlyList<object?> list => list.Any(item => ValueEquals(l, item)),
                    IReadOnlyDictionary<string, object?> map => l is string key && map.ContainsKey(key),
                    _ => throw new ExpressionException("expr.type_error", $"'in' needs a list on the right, got {TypeName(r)}.", bin.Position),
                };
            default:
                return Arithmetic(bin, l, r);
        }
    }

    private static object? EvalLogical(Binary bin, IReadOnlyDictionary<string, object?> vars)
    {
        var decisive = bin.Op == "||";
        object? l;
        ExpressionException? lError = null;
        try
        {
            l = Eval(bin.Left, vars);
        }
        catch (ExpressionException ex)
        {
            l = null;
            lError = ex;
        }

        if (lError is null && l is bool lb && lb == decisive)
        {
            return decisive;
        }

        var r = Eval(bin.Right, vars);
        if (r is bool rb && rb == decisive)
        {
            return decisive;
        }

        if (lError is not null)
        {
            throw lError;
        }

        if (l is not bool || r is not bool)
        {
            throw new ExpressionException("expr.type_error", $"'{bin.Op}' needs true/false values.", bin.Position);
        }

        return !decisive;
    }

    private static object? Arithmetic(Binary bin, object? l, object? r)
    {
        if (l is null || r is null)
        {
            return null;
        }

        if (bin.Op == "+")
        {
            switch (l, r)
            {
                case (string ls, string rs):
                    return ls + rs;
                case (IReadOnlyList<object?> ll, IReadOnlyList<object?> rl):
                    return ll.Concat(rl).ToList();
            }
        }

        if (l is double ld && r is double rd)
        {
            var result = bin.Op switch
            {
                "+" => ld + rd,
                "-" => ld - rd,
                "*" => ld * rd,
                "/" => rd == 0 ? throw DivideByZero(bin.Position) : ld / rd,
                "%" => rd == 0 ? throw DivideByZero(bin.Position) : ld % rd,
                _ => throw new ExpressionException("expr.internal", "Unknown operator.", bin.Position),
            };
            return double.IsFinite(result) ? result : throw Overflow(bin.Position);
        }

        throw new ExpressionException("expr.type_error", $"Cannot apply '{bin.Op}' to {TypeName(l)} and {TypeName(r)}.", bin.Position);
    }

    private static object? EvalCall(Call call, IReadOnlyDictionary<string, object?> vars)
    {
        if (call.Function == "has")
        {
            var sel = (Select)call.Args[0];
            var target = Eval(sel.Operand, vars);
            return target is IReadOnlyDictionary<string, object?> map && map.TryGetValue(sel.Field, out var value) && value is not null;
        }

        var receiver = call.Target is null ? null : Eval(call.Target, vars);
        var args = call.Args.Select(a => Eval(a, vars)).ToArray();
        switch (call.Function)
        {
            case "size":
                var subject = call.Target is null ? args[0] : receiver;
                return subject switch
                {
                    null => 0d,
                    string s => (double)s.Length,
                    IReadOnlyList<object?> list => (double)list.Count,
                    IReadOnlyDictionary<string, object?> map => (double)map.Count,
                    _ => throw new ExpressionException("expr.type_error", $"size() does not apply to {TypeName(subject)}.", call.Position),
                };
            case "startsWith":
            case "endsWith":
            case "contains":
            case "matches":
                if (receiver is null)
                {
                    return false;
                }

                if (receiver is not string str || args[0] is not string arg)
                {
                    throw new ExpressionException("expr.type_error", $"{call.Function}() works on text.", call.Position);
                }

                return call.Function switch
                {
                    "startsWith" => str.StartsWith(arg, StringComparison.Ordinal),
                    "endsWith" => str.EndsWith(arg, StringComparison.Ordinal),
                    "contains" => str.Contains(arg, StringComparison.Ordinal),
                    _ => Matches(str, arg, call.Position),
                };
            case "lowerAscii":
            case "upperAscii":
            case "trim":
                return receiver switch
                {
                    null => null,
                    string s2 => call.Function switch
                    {
                        "lowerAscii" => AsciiCase(s2, lower: true),
                        "upperAscii" => AsciiCase(s2, lower: false),
                        _ => s2.Trim(),
                    },
                    _ => throw new ExpressionException("expr.type_error", $"{call.Function}() works on text.", call.Position),
                };
            case "int":
                return args[0] switch
                {
                    null => null,
                    double d => Math.Truncate(d),
                    string s when long.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => (double)parsed,
                    var other => throw new ExpressionException("expr.conversion", $"Cannot convert {Describe(other)} to a whole number.", call.Position),
                };
            case "double":
                return args[0] switch
                {
                    null => null,
                    double d => d,
                    string s when double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
                    var other => throw new ExpressionException("expr.conversion", $"Cannot convert {Describe(other)} to a number.", call.Position),
                };
            case "string":
                return args[0] switch
                {
                    null => null,
                    string s => s,
                    bool b => b ? "true" : "false",
                    double d => FormatNumber(d),
                    var other => throw new ExpressionException("expr.conversion", $"Cannot convert {TypeName(other)} to text.", call.Position),
                };
            default:
                throw new ExpressionException("expr.unknown_function", $"Unknown function '{call.Function}'.", call.Position);
        }
    }

    private static bool Matches(string input, string pattern, int position)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            throw new ExpressionException("expr.bad_regex", $"'{pattern}' is not a valid pattern.", position);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ExpressionException("expr.timeout", "Pattern took too long to evaluate.", position);
        }
    }

    private static string AsciiCase(string s, bool lower) =>
        string.Create(s.Length, (s, lower), static (span, state) =>
        {
            for (var i = 0; i < state.s.Length; i++)
            {
                var c = state.s[i];
                span[i] = state.lower
                    ? (c is >= 'A' and <= 'Z' ? (char)(c + 32) : c)
                    : (c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
            }
        });

    /// <summary>Formats like JavaScript's Number#toString for the common range: integral values without a fraction.</summary>
    internal static string FormatNumber(double d) =>
        d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d.ToString("0", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);

    public static bool ValueEquals(object? l, object? r)
    {
        if (l is null || r is null)
        {
            return l is null && r is null;
        }

        switch (l, r)
        {
            case (double a, double b):
                return a == b;
            case (string a, string b):
                return string.Equals(a, b, StringComparison.Ordinal);
            case (bool a, bool b):
                return a == b;
            case (IReadOnlyList<object?> a, IReadOnlyList<object?> b):
                return a.Count == b.Count && a.Zip(b).All(p => ValueEquals(p.First, p.Second));
            case (IReadOnlyDictionary<string, object?> a, IReadOnlyDictionary<string, object?> b):
                return a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && ValueEquals(kv.Value, other));
            default:
                return false;
        }
    }

    private static int CompareOrdered(object l, object r, Binary bin)
    {
        if (l is double a && r is double b)
        {
            return a.CompareTo(b);
        }

        if (l is string ls && r is string rs)
        {
            return Math.Sign(string.CompareOrdinal(ls, rs));
        }

        throw new ExpressionException("expr.type_error", $"Cannot compare {TypeName(l)} with {TypeName(r)} using '{bin.Op}'.", bin.Position);
    }

    private static ExpressionException DivideByZero(int position) => new("expr.divide_by_zero", "Division by zero.", position);

    private static ExpressionException Overflow(int position) => new("expr.overflow", "Number is too large.", position);

    private static string Describe(object? v) => v is string s ? $"'{s}'" : TypeName(v);

    internal static string TypeName(object? v) => v switch
    {
        null => "null",
        bool => "true/false",
        double => "a number",
        string => "text",
        IReadOnlyList<object?> => "a list",
        IReadOnlyDictionary<string, object?> => "a map",
        _ => v.GetType().Name,
    };
}
