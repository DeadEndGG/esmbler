# Esmbler.Expressions

Public, dependency-free CEL-subset expression language used by rules, guards, filters, defaults, and migrations (ADR 0009, REQ-RULE-003).

- Pipeline: `Lexer` → `Parser` (AST in `Ast.cs`) → `Checker` (limits, known names, type mismatches, references) → `Evaluator`.
- Invariants: non-Turing-complete; bounded by `ExpressionLimits`; one number type (double); absent = null; regex is NonBacktracking with a 50 ms timeout.
- Any semantic change must update `conformance/cases.json` and keep the TypeScript twin (`apps/web/src/app/core/expressions`) passing.
- Tests: `dotnet test tests/Esmbler.UnitTests --filter FullyQualifiedName~Expressions`.
