import { Binary, Call, ExpressionError, ExpressionLimits, ExpressionSchema, hasOwn, Node, Select, ValueType } from './ast';

interface FunctionDef {
  readonly minArgs: number;
  readonly maxArgs: number;
  readonly method: boolean;
  readonly global: boolean;
}

/** Function table; must stay identical to Checker.Functions in C# (CI compares them). Order matters for suggestions. */
export const FUNCTIONS: ReadonlyMap<string, FunctionDef> = new Map<string, FunctionDef>([
  ['size', { minArgs: 0, maxArgs: 1, method: true, global: true }],
  ['has', { minArgs: 1, maxArgs: 1, method: false, global: true }],
  ['startsWith', { minArgs: 1, maxArgs: 1, method: true, global: false }],
  ['endsWith', { minArgs: 1, maxArgs: 1, method: true, global: false }],
  ['contains', { minArgs: 1, maxArgs: 1, method: true, global: false }],
  ['matches', { minArgs: 1, maxArgs: 1, method: true, global: false }],
  ['lowerAscii', { minArgs: 0, maxArgs: 0, method: true, global: false }],
  ['upperAscii', { minArgs: 0, maxArgs: 0, method: true, global: false }],
  ['trim', { minArgs: 0, maxArgs: 0, method: true, global: false }],
  ['int', { minArgs: 1, maxArgs: 1, method: false, global: true }],
  ['double', { minArgs: 1, maxArgs: 1, method: false, global: true }],
  ['string', { minArgs: 1, maxArgs: 1, method: false, global: true }],
]);

/** Static checks: node budget, known variables/fields/functions, definite type mismatches. Mirrors Checker.cs. */
export function check(root: Node, schema: ExpressionSchema | null | undefined, limits: ExpressionLimits): Set<string> {
  const checker = new Checker(schema ?? null, limits);
  checker.visit(root);
  return checker.references;
}

class Checker {
  readonly references = new Set<string>();
  private nodes = 0;

  constructor(
    private readonly schema: ExpressionSchema | null,
    private readonly limits: ExpressionLimits,
  ) {}

  visit(node: Node): ValueType {
    if (++this.nodes > this.limits.maxNodes) {
      throw new ExpressionError(
        'expr.too_complex',
        `Expression has more than ${this.limits.maxNodes} parts. Split it into several rules.`,
        node.position,
      );
    }

    switch (node.kind) {
      case 'literal': {
        const v = node.value;
        if (v === null) return 'null';
        switch (typeof v) {
          case 'boolean':
            return 'bool';
          case 'number':
            return 'number';
          case 'string':
            return 'string';
          default:
            return 'any';
        }
      }
      case 'ident': {
        if (this.schema === null) {
          return 'any';
        }

        if (!hasOwn(this.schema, node.name)) {
          const suggestion = suggest(node.name, Object.keys(this.schema));
          throw new ExpressionError(
            'expr.unknown_variable',
            `Unknown name '${node.name}'.` + (suggestion === undefined ? '' : ` Did you mean '${suggestion}'?`),
            node.position,
            suggestion,
          );
        }

        return this.schema[node.name].type;
      }
      case 'select':
        return this.visitSelect(node);
      case 'index':
        this.visit(node.operand);
        this.visit(node.key);
        return 'any';
      case 'list':
        for (const item of node.items) this.visit(item);
        return 'list';
      case 'unary': {
        const operand = this.visit(node.operand);
        if (node.op === '!') {
          requireType(operand, node.position, "'!' needs a true/false value", 'bool');
          return 'bool';
        }

        requireType(operand, node.position, "'-' needs a number", 'number');
        return operand;
      }
      case 'conditional': {
        requireType(this.visit(node.test), node.position, "The condition before '?' must be true/false", 'bool');
        const a = this.visit(node.ifTrue);
        const b = this.visit(node.ifFalse);
        return a === b ? a : 'any';
      }
      case 'binary':
        return this.visitBinary(node);
      case 'call':
        return this.visitCall(node);
    }
  }

  private visitSelect(sel: Select): ValueType {
    const operand = sel.operand;
    if (operand.kind === 'ident' && this.schema === null) {
      this.references.add(`${operand.name}.${sel.field}`);
    }

    if (operand.kind === 'ident' && this.schema !== null && hasOwn(this.schema, operand.name)) {
      const variable = this.schema[operand.name];
      this.visit(operand);
      this.references.add(`${operand.name}.${sel.field}`);
      if (variable.fields == null) {
        return 'any';
      }

      if (!hasOwn(variable.fields, sel.field)) {
        const suggestion = suggest(sel.field, Object.keys(variable.fields));
        throw new ExpressionError(
          'expr.unknown_field',
          `Unknown field '${operand.name}.${sel.field}'.` +
            (suggestion === undefined ? '' : ` Did you mean '${operand.name}.${suggestion}'?`),
          sel.position,
          suggestion === undefined ? undefined : `${operand.name}.${suggestion}`,
        );
      }

      return variable.fields[sel.field];
    }

    this.visit(operand);
    return 'any';
  }

  private visitBinary(bin: Binary): ValueType {
    const l = this.visit(bin.left);
    const r = this.visit(bin.right);
    switch (bin.op) {
      case '&&':
      case '||':
        requireType(l, bin.position, `'${bin.op}' needs true/false values`, 'bool');
        requireType(r, bin.position, `'${bin.op}' needs true/false values`, 'bool');
        return 'bool';
      case '==':
      case '!=':
      case '<':
      case '<=':
      case '>':
      case '>=':
        checkComparable(bin, l, r);
        return 'bool';
      case 'in':
        return 'bool';
      case '+':
        if (l === 'string' || r === 'string') {
          return 'string';
        }

        return numeric(l, r);
      default:
        requireType(l, bin.position, `'${bin.op}' needs numbers`, 'number');
        requireType(r, bin.position, `'${bin.op}' needs numbers`, 'number');
        return numeric(l, r);
    }
  }

  private visitCall(call: Call): ValueType {
    const def = FUNCTIONS.get(call.fn);
    if (def === undefined) {
      const suggestion = suggest(call.fn, FUNCTIONS.keys());
      throw new ExpressionError(
        'expr.unknown_function',
        `Unknown function '${call.fn}'.` + (suggestion === undefined ? '' : ` Did you mean '${suggestion}'?`),
        call.position,
        suggestion,
      );
    }

    const isMethod = call.target !== null;
    if ((isMethod && !def.method) || (!isMethod && !def.global)) {
      throw new ExpressionError(
        'expr.bad_call',
        isMethod
          ? `'${call.fn}' is not a method; call it as ${call.fn}(value).`
          : `'${call.fn}' is a method; call it as value.${call.fn}(...).`,
        call.position,
      );
    }

    const arity = call.args.length + (isMethod && call.fn === 'size' ? 1 : 0);
    if (arity < def.minArgs || arity > def.maxArgs || (call.fn === 'size' && arity !== 1)) {
      throw new ExpressionError('expr.bad_call', `'${call.fn}' was given the wrong number of arguments.`, call.position);
    }

    if (call.fn === 'has') {
      if (call.args[0].kind !== 'select') {
        throw new ExpressionError(
          'expr.bad_call',
          'has() takes a field selection, e.g. has(record.justification).',
          call.position,
        );
      }

      this.visit(call.args[0]);
      return 'bool';
    }

    if (call.target !== null) {
      this.visit(call.target);
    }

    for (const arg of call.args) this.visit(arg);

    switch (call.fn) {
      case 'size':
      case 'int':
      case 'double':
        return 'number';
      case 'string':
      case 'lowerAscii':
      case 'upperAscii':
      case 'trim':
        return 'string';
      default:
        return 'bool';
    }
  }
}

function numeric(l: ValueType, r: ValueType): ValueType {
  return l === 'number' && r === 'number' ? 'number' : 'any';
}

function checkComparable(bin: Binary, l: ValueType, r: ValueType): void {
  if (l === 'any' || l === 'null' || r === 'any' || r === 'null') {
    return;
  }

  if (l !== r) {
    const field = describe(bin.left);
    const hint = field !== null ? ` ${field} is ${article(l)}; compare it with ${article(l)} value.` : '';
    throw new ExpressionError(
      'expr.type_mismatch',
      `Cannot compare ${article(l)} with ${article(r)} using '${bin.op}'.${hint}`,
      bin.position,
    );
  }

  if ((bin.op === '<' || bin.op === '<=' || bin.op === '>' || bin.op === '>=') && l !== 'string' && l !== 'number') {
    throw new ExpressionError('expr.type_mismatch', `'${bin.op}' only compares numbers or text.`, bin.position);
  }
}

function describe(n: Node): string | null {
  return n.kind === 'select' && n.operand.kind === 'ident' ? `'${n.operand.name}.${n.field}'` : null;
}

function article(t: ValueType): string {
  switch (t) {
    case 'number':
      return 'a number';
    case 'string':
      return 'text';
    case 'bool':
      return 'true/false';
    case 'list':
      return 'a list';
    case 'map':
      return 'a map';
    default:
      return 'a value';
  }
}

function requireType(actual: ValueType, position: number, message: string, ...allowed: ValueType[]): void {
  if (actual === 'any' || allowed.includes(actual)) {
    return;
  }

  throw new ExpressionError('expr.type_mismatch', message + '.', position);
}

/** Closest candidate by Levenshtein distance (case-insensitive), if within max(2, len/3). Mirrors Checker.Suggest. */
export function suggest(name: string, candidates: Iterable<string>): string | undefined {
  let best: string | undefined;
  let bestDistance = Number.MAX_SAFE_INTEGER;
  const lowered = toLowerInvariant(name);
  for (const candidate of candidates) {
    const d = levenshtein(lowered, toLowerInvariant(candidate));
    if (d < bestDistance) {
      bestDistance = d;
      best = candidate;
    }
  }

  return best !== undefined && bestDistance <= Math.max(2, Math.floor(name.length / 3)) ? best : undefined;
}

/** Per-character lowercasing like .NET ToLowerInvariant (no context-sensitive or length-changing mappings). */
function toLowerInvariant(s: string): string {
  let out = '';
  for (const ch of s) {
    const lower = ch.toLowerCase();
    out += lower.length === ch.length ? lower : ch;
  }

  return out;
}

function levenshtein(a: string, b: string): number {
  let prev = new Array<number>(b.length + 1);
  let cur = new Array<number>(b.length + 1);
  for (let j = 0; j <= b.length; j++) prev[j] = j;
  for (let i = 1; i <= a.length; i++) {
    cur[0] = i;
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      cur[j] = Math.min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + cost);
    }

    [prev, cur] = [cur, prev];
  }

  return prev[b.length];
}
