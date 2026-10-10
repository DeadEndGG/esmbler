namespace Esmbler.Expressions;

/// <summary>Abstract syntax tree for the CEL-subset expression language (ADR 0009).</summary>
public abstract record Node(int Position);

public sealed record Literal(object? Value, int Position) : Node(Position);

public sealed record Ident(string Name, int Position) : Node(Position);

public sealed record Select(Node Operand, string Field, int Position) : Node(Position);

public sealed record Index(Node Operand, Node Key, int Position) : Node(Position);

public sealed record Call(string Function, Node? Target, IReadOnlyList<Node> Args, int Position) : Node(Position);

public sealed record Unary(string Op, Node Operand, int Position) : Node(Position);

public sealed record Binary(string Op, Node Left, Node Right, int Position) : Node(Position);

public sealed record Conditional(Node Test, Node IfTrue, Node IfFalse, int Position) : Node(Position);

public sealed record ListLiteral(IReadOnlyList<Node> Items, int Position) : Node(Position);
