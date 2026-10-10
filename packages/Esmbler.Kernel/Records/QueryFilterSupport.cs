using Esmbler.Expressions;

namespace Esmbler.Kernel.Records;

/// <summary>
/// The part of the expression language that list filters and metrics compile to SQL (mirrors
/// <c>Esmbler.Kernel.Infrastructure.Records.QueryCompiler</c>). Configuration that is evaluated as a query (metric
/// filters) is checked at validation time with this, so it never fails at run time.
/// </summary>
public static class QueryFilterSupport
{
    private static readonly HashSet<string> Functions = ["has", "size", "contains", "startsWith", "endsWith", "lowerAscii", "upperAscii", "trim"];
    private static readonly HashSet<string> UserFields = ["id", "groups", "roles"];

    /// <summary>The first construct a query cannot express, or null when the whole expression is supported.</summary>
    public static string? Unsupported(Node node) => node switch
    {
        Literal => null,
        Select { Operand: Ident { Name: "record" } } => null,
        Select { Operand: Ident { Name: "user" } } sel => UserFields.Contains(sel.Field) ? null : $"user.{sel.Field}",
        Select { Operand: Ident { Name: var name } } => $"{name}.* (only record and user can be used in a query)",
        Ident ident => $"'{ident.Name}' on its own",
        ListLiteral list => list.Items.All(i => i is Literal { Value: string or double }) ? null : "lists of anything but text and numbers",
        Unary { Op: "!" or "-" } unary => Unsupported(unary.Operand),
        Binary bin => Unsupported(bin.Left) ?? Unsupported(bin.Right),
        Conditional c => Unsupported(c.Test) ?? Unsupported(c.IfTrue) ?? Unsupported(c.IfFalse),
        Call call when Functions.Contains(call.Function) => (call.Target is null ? null : Unsupported(call.Target)) ?? call.Args.Select(Unsupported).FirstOrDefault(u => u is not null),
        Call call => $"{call.Function}()",
        _ => "this expression",
    };
}
