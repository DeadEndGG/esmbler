import { DEFAULT_LIMITS, ExpressionError, ExpressionLimits, ExpressionSchema, Node } from './ast';
import { check } from './checker';
import { evaluate, typeName, Vars } from './evaluator';
import { parse } from './parser';

export { ExpressionError, DEFAULT_LIMITS } from './ast';
export type { ExpressionLimits, ExpressionSchema, Node, ValueType, VariableSchema } from './ast';
export { suggest, FUNCTIONS } from './checker';
export { valueEquals, formatNumber } from './evaluator';
export type { Vars } from './evaluator';

/** A parsed and checked expression, safe to cache and evaluate repeatedly. Mirrors CompiledExpression in C#. */
export interface CompiledExpression {
  readonly source: string;
  readonly root: Node;
  /** Variable members referenced, e.g. `record.category`. Used for dependency graphs. */
  readonly references: Set<string>;
  evaluate(vars: Vars): unknown;
  /** Evaluates to a boolean; any non-boolean result is an `expr.not_boolean` error. */
  evaluateBool(vars: Vars): boolean;
}

/** Parses and statically checks `source`. Throws {@link ExpressionError} with actionable messages. */
export function compile(
  source: string,
  schema?: ExpressionSchema | null,
  limits?: Partial<ExpressionLimits> | null,
): CompiledExpression {
  const effective: ExpressionLimits = {
    maxLength: limits?.maxLength ?? DEFAULT_LIMITS.maxLength,
    maxDepth: limits?.maxDepth ?? DEFAULT_LIMITS.maxDepth,
    maxNodes: limits?.maxNodes ?? DEFAULT_LIMITS.maxNodes,
  };
  const root = parse(source, effective);
  const references = check(root, schema, effective);
  return {
    source,
    root,
    references,
    evaluate: (vars) => evaluate(root, vars),
    evaluateBool: (vars) => {
      const result = evaluate(root, vars);
      if (typeof result !== 'boolean') {
        throw new ExpressionError(
          'expr.not_boolean',
          `Expression must produce true/false but produced ${typeName(result)}.`,
        );
      }

      return result;
    },
  };
}
