---
title: "ADR 0009: Rule expression language"
status: accepted
owner: platform-architecture
last_reviewed: 2026-10-10
requirements: [REQ-RULE-001, REQ-RULE-002, REQ-RULE-003, REQ-RULE-004, REQ-RULE-005, REQ-RULE-006, REQ-REC-002, REQ-AI-004]
---

# ADR 0009: Rule expression language

## Context and problem statement

Rules ("if category is Access, Justification is required"), query filters ([ADR 0002](0002-storage-and-query-model.md)) and migration value expressions need a safe, non-Turing-complete language. The server is authoritative; the Angular renderer evaluates the same rules for instant feedback, so both must agree exactly. LLMs write most expressions, and humans review them in diffs. Which language, and how do we keep two implementations identical?

## Decision drivers

- Terminates with bounded cost; no loops, no I/O.
- Readable by people and LLMs; close to something models already know.
- Identical results in C# and TypeScript, proved by tests.
- Actionable static errors (unknown field with suggestion, type mismatch).

## Considered options

1. **CEL subset, implemented twice (C# and TypeScript) with a shared conformance suite.**
2. Full CEL via existing libraries (`cel-dotnet`, `cel-js`).
3. JSONLogic.
4. JavaScript via Jint (server) and the browser.

## Decision outcome

**Chosen option 1.** C# in [`packages/Esmbler.Expressions`](../../packages/Esmbler.Expressions/) (`Lexer`, `Parser`, `Checker`, `Evaluator`); TypeScript twin in [`apps/web/src/app/core/expressions`](../../apps/web/src/app/core/expressions/) (used by the renderer for instant form feedback; the server stays authoritative). Shared cases in [`conformance/cases.json`](../../packages/Esmbler.Expressions/conformance/cases.json).

### Grammar (EBNF)

```ebnf
expr      = or , [ "?" , expr , ":" , expr ] ;
or        = and , { "||" , and } ;
and       = rel , { "&&" , rel } ;
rel       = add , [ ( "==" | "!=" | "<" | "<=" | ">" | ">=" | "in" ) , add ] ;   (* non-associative *)
add       = mul , { ( "+" | "-" ) , mul } ;
mul       = unary , { ( "*" | "/" | "%" ) , unary } ;
unary     = ( "!" | "-" ) , unary | member ;
member    = primary , { "." , ident , [ args ] | "[" , expr , "]" } ;
primary   = number | string | "true" | "false" | "null"
          | ident , [ args ] | "(" , expr , ")" | "[" , [ expr , { "," , expr } ] , "]" ;
args      = "(" , [ expr , { "," , expr } ] , ")" ;
ident     = ( letter | "_" ) , { letter | digit | "_" } ;
number    = digit , { digit } , [ "." , digit , { digit } ] ;           (* no exponent; sign via unary - *)
string    = "'" , { char | escape } , "'" | '"' , { char | escape } , '"' ;
escape    = "\" , ( "n" | "t" | "r" | any ) ;
```

Functions: global `has(sel)`, `size(x)`, `int(x)`, `double(x)`, `string(x)`; methods `.size()`, `.startsWith(s)`, `.endsWith(s)`, `.contains(s)`, `.matches(re)`, `.lowerAscii()`, `.upperAscii()`, `.trim()`.

Variables: `record` (current values, readable fields only), `prior` (values before this write), `user` (`id`, `roles`, `groups`), `now` (UTC instant string).

### Semantics

| Construct | Result |
|---|---|
| Numbers | One type: IEEE double. `7 / 2 == 3.5`; `1 / 0` → `expr.divide_by_zero`; non-finite → `expr.overflow` |
| `record.missing` (absent key) | `null` |
| `null.x` (select through null) | `null` |
| `null == null` / `null != 'a'` | `true` / `true` |
| `null < 3`, `null >= 3` | `false` |
| `null + 1` | `null` |
| `a && b`, `a \|\| b` | Commutative error absorption: a decisive operand (`false` for `&&`, `true` for `\|\|`) wins even if the other side errors |
| `x in list` | Equality membership; `x in null` → `false` |
| `s.matches(re)` | .NET `RegexOptions.NonBacktracking`, 50 ms timeout → `expr.timeout`; on `null` → `false` |
| Dates | ISO strings: `"2026-10-10"`, `"2026-10-10T14:03:00Z"`; normalized format makes text comparison chronological |
| Choice values | Option ID strings (`'access'`), never labels |

Static checks (`Checker`): known variables; field existence against the entity schema with a closest-match suggestion; definite type mismatches (`'a' == 1`, `1 && true`); limits from `ExpressionLimits`: 2,000 characters, depth 32, 256 nodes. Fields hidden from the evaluating principal are absent from the schema, so a rule cannot reference them client-side; data rules referencing restricted fields are evaluated only on the server (REQ-RULE-006).

### Deviations from CEL

| CEL | Esmbler | Why |
|---|---|---|
| `int`, `uint`, `double` | single number (double) | JSON and JavaScript have one number type; avoids `1 == 1.0` surprises across twins |
| Missing map key is an error | `null` | Records have optional fields; absent and null behave the same in rules |
| Select on null is an error | `null` | Same reason; keeps rules short |
| Ordering with null is an error | `false` | A rule on an empty field does not fire |
| Arithmetic with null is an error | `null` | Propagates "unknown" |
| RE2 | .NET NonBacktracking (linear time) + timeout | No RE2 in .NET; equivalent safety; JS twin uses the same restricted syntax subset checked by the conformance suite |
| Macros (`all`, `exists`, `map`), maps, timestamps, durations | Not supported in Gate 1 | Add with conformance cases when a gate needs them |

### Conformance process

1. Every behavior change starts with a case in `cases.json`: `expr`, optional `vars`, and `expect` or `error` (code), with `phase: "check"` for static errors.
2. C#: `ConformanceTests.Case_matches_expected` runs every case. TypeScript: a Vitest runner loads the same file.
3. CI fails if either runner fails or if a function exists in one implementation's function table and not the other.
4. Error codes, not messages, are compared; messages are localized per runtime.

### Examples

```json
[
  { "id": "tenant__rules.employee_id_digits", "kind": "validation", "scope": "data",
    "entity": "core.tickets.ticket", "field": "tenant__employee_id",
    "assert": "record.tenant__employee_id == null || record.tenant__employee_id.matches('^[0-9]{6}$')",
    "message": "Employee ID is six digits, for example 004213." },
  { "id": "tenant__rules.access_justification", "kind": "conditional", "scope": "data",
    "entity": "core.tickets.ticket", "when": "record.category == 'access'",
    "actions": [{ "type": "require", "field": "tenant__justification",
                  "message": "Access requests need a business justification." }] },
  { "id": "tenant__rules.show_vendor", "kind": "conditional", "scope": "form",
    "entity": "core.tickets.ticket", "view": "core.tickets.record",
    "when": "record.category in ['hardware', 'software']",
    "actions": [{ "type": "show", "field": "tenant__vendor" }] }
]
```

The AI restates each rule: "Justification is required when Category is Access, on every form, the API, imports and automations (data rule)."

### Consequences

- Good: familiar CEL syntax for LLMs; tiny surface; both runtimes are under our control.
- Bad: two implementations to maintain; deviations mean "CEL" documentation must be read with this table.

## Pros and cons of the options

| Option | Pros | Cons |
|---|---|---|
| CEL subset ×2 + conformance | Readable, safe, provably aligned | Two implementations |
| Full CEL libraries | Standard | `cel-dotnet` immature; JS and C# libraries diverge on numbers and errors |
| JSONLogic | Trivial to evaluate everywhere | Unreadable for humans and LLMs in diffs |
| Jint / JavaScript | Expressive | Turing-complete; needs sandboxing and fuel limits |

## More information

- Verification: `Unit.Expressions.ConformanceTests` (C#) and `apps/web/src/app/core/expressions/expressions.spec.ts` (TypeScript, `npx ng test`) both run every case in `conformance/cases.json`.
- Revisit triggers: a gate needs timestamps/durations (SLA, Gate 2) or list macros; a mature, conformant CEL library appears for both .NET and TypeScript.
