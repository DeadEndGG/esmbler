import { Binary, Call, ExpressionError, hasOwn, isWhiteSpace, Node, Unary } from './ast';

/**
 * Tree-walking evaluator. Mirrors Esmbler.Expressions/Evaluator.cs (see conformance/cases.json):
 * absent map keys read as null; selecting through null yields null; ordering comparisons with null are false;
 * arithmetic with null yields null; there is one number type (IEEE double); && / || absorb errors when the other
 * side decides the result. Values are null, boolean, number, string, arrays (lists) and plain objects (maps);
 * `undefined` is treated as null.
 */
export type Vars = Readonly<Record<string, unknown>>;

type MapValue = Record<string, unknown>;

const isMap = (v: unknown): v is MapValue => typeof v === 'object' && v !== null && !Array.isArray(v);
const isNull = (v: unknown): v is null | undefined => v === null || v === undefined;
const norm = (v: unknown): unknown => (v === undefined ? null : v);

export function evaluate(node: Node, vars: Vars): unknown {
  switch (node.kind) {
    case 'literal':
      return node.value;
    case 'ident':
      if (!hasOwn(vars, node.name)) {
        throw new ExpressionError('expr.unknown_variable', `Unknown name '${node.name}'.`, node.position);
      }

      return norm(vars[node.name]);
    case 'select':
      return selectField(evaluate(node.operand, vars), node.field, node.position);
    case 'index':
      return indexValue(evaluate(node.operand, vars), evaluate(node.key, vars), node.position);
    case 'list':
      return node.items.map((i) => evaluate(i, vars));
    case 'unary':
      return evalUnary(node, evaluate(node.operand, vars));
    case 'conditional': {
      const test = evaluate(node.test, vars);
      if (typeof test !== 'boolean') {
        throw new ExpressionError('expr.type_error', "Condition before '?' is not true/false.", node.position);
      }

      return test ? evaluate(node.ifTrue, vars) : evaluate(node.ifFalse, vars);
    }
    case 'binary':
      return evalBinary(node, vars);
    case 'call':
      return evalCall(node, vars);
  }
}

function selectField(target: unknown, field: string, position: number): unknown {
  if (isNull(target)) return null;
  if (isMap(target)) return hasOwn(target, field) ? norm(target[field]) : null;
  throw new ExpressionError('expr.type_error', `Cannot read '.${field}' from ${typeName(target)}.`, position);
}

function indexValue(target: unknown, key: unknown, position: number): unknown {
  if (isNull(target)) return null;
  if (Array.isArray(target) && typeof key === 'number') {
    if (key < 0 || key >= target.length || key !== Math.floor(key)) {
      throw new ExpressionError('expr.index_out_of_range', `Index ${formatNumber(key)} is out of range.`, position);
    }

    return norm(target[key]);
  }

  if (isMap(target) && typeof key === 'string') {
    return hasOwn(target, key) ? norm(target[key]) : null;
  }

  throw new ExpressionError('expr.type_error', `Cannot index ${typeName(target)} with ${typeName(key)}.`, position);
}

function evalUnary(un: Unary, value: unknown): unknown {
  if (un.op === '!') {
    if (typeof value === 'boolean') return !value;
    throw new ExpressionError('expr.type_error', `'!' needs true/false, got ${typeName(value)}.`, un.position);
  }

  if (isNull(value)) return null;
  if (typeof value === 'number') return -value;
  throw new ExpressionError('expr.type_error', `'-' needs a number, got ${typeName(value)}.`, un.position);
}

function evalBinary(bin: Binary, vars: Vars): unknown {
  if (bin.op === '&&' || bin.op === '||') {
    return evalLogical(bin, vars);
  }

  const l = evaluate(bin.left, vars);
  const r = evaluate(bin.right, vars);
  switch (bin.op) {
    case '==':
      return valueEquals(l, r);
    case '!=':
      return !valueEquals(l, r);
    case '<':
    case '<=':
    case '>':
    case '>=': {
      if (isNull(l) || isNull(r)) {
        return false;
      }

      const c = compareOrdered(l, r, bin);
      switch (bin.op) {
        case '<':
          return c < 0;
        case '<=':
          return c <= 0;
        case '>':
          return c > 0;
        default:
          return c >= 0;
      }
    }
    case 'in':
      if (isNull(r)) return false;
      if (Array.isArray(r)) return r.some((item) => valueEquals(l, item));
      if (isMap(r)) return typeof l === 'string' && hasOwn(r, l);
      throw new ExpressionError('expr.type_error', `'in' needs a list on the right, got ${typeName(r)}.`, bin.position);
    default:
      return arithmetic(bin, l, r);
  }
}

function evalLogical(bin: Binary, vars: Vars): unknown {
  const decisive = bin.op === '||';
  let l: unknown;
  let lError: ExpressionError | null = null;
  try {
    l = evaluate(bin.left, vars);
  } catch (ex) {
    if (!(ex instanceof ExpressionError)) throw ex;
    l = null;
    lError = ex;
  }

  if (lError === null && l === decisive) {
    return decisive;
  }

  const r = evaluate(bin.right, vars);
  if (r === decisive) {
    return decisive;
  }

  if (lError !== null) {
    throw lError;
  }

  if (typeof l !== 'boolean' || typeof r !== 'boolean') {
    throw new ExpressionError('expr.type_error', `'${bin.op}' needs true/false values.`, bin.position);
  }

  return !decisive;
}

function arithmetic(bin: Binary, l: unknown, r: unknown): unknown {
  if (isNull(l) || isNull(r)) {
    return null;
  }

  if (bin.op === '+') {
    if (typeof l === 'string' && typeof r === 'string') return l + r;
    if (Array.isArray(l) && Array.isArray(r)) return [...l, ...r];
  }

  if (typeof l === 'number' && typeof r === 'number') {
    let result: number;
    switch (bin.op) {
      case '+':
        result = l + r;
        break;
      case '-':
        result = l - r;
        break;
      case '*':
        result = l * r;
        break;
      case '/':
        if (r === 0) throw divideByZero(bin.position);
        result = l / r;
        break;
      case '%':
        if (r === 0) throw divideByZero(bin.position);
        result = l % r;
        break;
      default:
        throw new ExpressionError('expr.internal', 'Unknown operator.', bin.position);
    }

    if (!Number.isFinite(result)) {
      throw new ExpressionError('expr.overflow', 'Number is too large.', bin.position);
    }

    return result;
  }

  throw new ExpressionError(
    'expr.type_error',
    `Cannot apply '${bin.op}' to ${typeName(l)} and ${typeName(r)}.`,
    bin.position,
  );
}

function evalCall(call: Call, vars: Vars): unknown {
  if (call.fn === 'has') {
    const sel = call.args[0];
    if (sel.kind !== 'select') {
      throw new ExpressionError('expr.bad_call', 'has() takes a field selection, e.g. has(record.justification).', call.position);
    }

    const target = evaluate(sel.operand, vars);
    return isMap(target) && hasOwn(target, sel.field) && !isNull(target[sel.field]);
  }

  const receiver = call.target === null ? null : evaluate(call.target, vars);
  const args = call.args.map((a) => evaluate(a, vars));
  switch (call.fn) {
    case 'size': {
      const subject = call.target === null ? args[0] : receiver;
      if (isNull(subject)) return 0;
      if (typeof subject === 'string' || Array.isArray(subject)) return subject.length;
      if (isMap(subject)) return Object.keys(subject).length;
      throw new ExpressionError('expr.type_error', `size() does not apply to ${typeName(subject)}.`, call.position);
    }
    case 'startsWith':
    case 'endsWith':
    case 'contains':
    case 'matches': {
      if (isNull(receiver)) {
        return false;
      }

      const arg = args[0];
      if (typeof receiver !== 'string' || typeof arg !== 'string') {
        throw new ExpressionError('expr.type_error', `${call.fn}() works on text.`, call.position);
      }

      switch (call.fn) {
        case 'startsWith':
          return receiver.startsWith(arg);
        case 'endsWith':
          return receiver.endsWith(arg);
        case 'contains':
          return receiver.includes(arg);
        default:
          return matches(receiver, arg, call.position);
      }
    }
    case 'lowerAscii':
    case 'upperAscii':
    case 'trim':
      if (isNull(receiver)) return null;
      if (typeof receiver === 'string') {
        return call.fn === 'lowerAscii'
          ? receiver.replace(/[A-Z]/g, (c) => c.toLowerCase())
          : call.fn === 'upperAscii'
            ? receiver.replace(/[a-z]/g, (c) => c.toUpperCase())
            : trimDotNet(receiver);
      }

      throw new ExpressionError('expr.type_error', `${call.fn}() works on text.`, call.position);
    case 'int': {
      const v = args[0];
      if (isNull(v)) return null;
      if (typeof v === 'number') return Math.trunc(v);
      if (typeof v === 'string') {
        const parsed = parseLong(trimDotNet(v));
        if (parsed !== null) return parsed;
      }

      throw new ExpressionError('expr.conversion', `Cannot convert ${describe(v)} to a whole number.`, call.position);
    }
    case 'double': {
      const v = args[0];
      if (isNull(v)) return null;
      if (typeof v === 'number') return v;
      if (typeof v === 'string') {
        const parsed = parseDouble(trimDotNet(v));
        if (parsed !== null) return parsed;
      }

      throw new ExpressionError('expr.conversion', `Cannot convert ${describe(v)} to a number.`, call.position);
    }
    case 'string': {
      const v = args[0];
      if (isNull(v)) return null;
      if (typeof v === 'string') return v;
      if (typeof v === 'boolean') return v ? 'true' : 'false';
      if (typeof v === 'number') return formatNumber(v);
      throw new ExpressionError('expr.conversion', `Cannot convert ${typeName(v)} to text.`, call.position);
    }
    default:
      throw new ExpressionError('expr.unknown_function', `Unknown function '${call.fn}'.`, call.position);
  }
}

// The C# twin uses .NET RegexOptions.NonBacktracking with a 50 ms timeout; JavaScript RegExp has neither, so
// patterns are cached and invalid ones map to expr.bad_regex.
const regexCache = new Map<string, RegExp | null>();

function matches(input: string, pattern: string, position: number): boolean {
  let re = regexCache.get(pattern);
  if (re === undefined) {
    try {
      re = new RegExp(pattern);
    } catch {
      re = null;
    }

    if (regexCache.size >= 256) regexCache.clear();
    regexCache.set(pattern, re);
  }

  if (re === null) {
    throw new ExpressionError('expr.bad_regex', `'${pattern}' is not a valid pattern.`, position);
  }

  return re.test(input);
}

/** String.Trim() in .NET: strips char.IsWhiteSpace characters. */
function trimDotNet(s: string): string {
  let start = 0;
  let end = s.length;
  while (start < end && isWhiteSpace(s[start])) start++;
  while (end > start && isWhiteSpace(s[end - 1])) end--;
  return s.slice(start, end);
}

const LONG_MIN = -(2n ** 63n);
const LONG_MAX = 2n ** 63n - 1n;

/** long.TryParse(s, NumberStyles.AllowLeadingSign, InvariantCulture) converted to double, or null. */
function parseLong(s: string): number | null {
  const m = /^([+-]?)([0-9]+)\0*$/.exec(s);
  if (m === null) return null;
  const value = BigInt(m[1] === '-' ? `-${m[2]}` : m[2]);
  if (value < LONG_MIN || value > LONG_MAX) return null;
  return Number(value);
}

/** double.TryParse(s, NumberStyles.Float, InvariantCulture), or null. */
function parseDouble(s: string): number | null {
  const t = s.replace(/\0+$/, '');
  if (/^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?$/.test(t)) {
    return Number(t);
  }

  const special = /^([+-]?)(infinity|nan)$/i.exec(t);
  if (special !== null) {
    if (special[2].toLowerCase() === 'nan') return NaN;
    return special[1] === '-' ? -Infinity : Infinity;
  }

  return null;
}

/**
 * Formats like Evaluator.FormatNumber in C#: integral values below 1e15 without a fraction, otherwise .NET's
 * shortest round-trip "R" format (scientific notation such as "1E+20" / "1.5E-07" outside [1e-4, 1e17)).
 */
export function formatNumber(d: number): string {
  if (Number.isNaN(d)) return 'NaN';
  if (!Number.isFinite(d)) return d > 0 ? 'Infinity' : '-Infinity';
  if (d === Math.floor(d) && Math.abs(d) < 1e15) {
    return Object.is(d, -0) ? '-0' : String(d);
  }

  const sign = d < 0 ? '-' : '';
  const [mantissa, exp] = Math.abs(d).toExponential().split('e');
  const digits = mantissa.replace('.', '');
  const scale = Number(exp) + 1;
  if (scale > 17 || scale < -3) {
    const e = scale - 1;
    const m = digits.length > 1 ? `${digits[0]}.${digits.slice(1)}` : digits;
    return `${sign}${m}E${e < 0 ? '-' : '+'}${String(Math.abs(e)).padStart(2, '0')}`;
  }

  if (scale <= 0) {
    return `${sign}0.${'0'.repeat(-scale)}${digits}`;
  }

  if (digits.length <= scale) {
    return sign + digits + '0'.repeat(scale - digits.length);
  }

  return `${sign}${digits.slice(0, scale)}.${digits.slice(scale)}`;
}

/** Deep equality over expression values; numbers compare numerically, maps by key set and values. */
export function valueEquals(l: unknown, r: unknown): boolean {
  if (isNull(l) || isNull(r)) {
    return isNull(l) && isNull(r);
  }

  if (typeof l === 'number' && typeof r === 'number') return l === r;
  if (typeof l === 'string' && typeof r === 'string') return l === r;
  if (typeof l === 'boolean' && typeof r === 'boolean') return l === r;
  if (Array.isArray(l) && Array.isArray(r)) {
    return l.length === r.length && l.every((item, i) => valueEquals(item, r[i]));
  }

  if (isMap(l) && isMap(r)) {
    const keys = Object.keys(l);
    return keys.length === Object.keys(r).length && keys.every((k) => hasOwn(r, k) && valueEquals(l[k], r[k]));
  }

  return false;
}

function compareOrdered(l: unknown, r: unknown, bin: Binary): number {
  if (typeof l === 'number' && typeof r === 'number') {
    // double.CompareTo: NaN sorts below every number and equals itself.
    if (l < r) return -1;
    if (l > r) return 1;
    if (l === r) return 0;
    return Number.isNaN(l) ? (Number.isNaN(r) ? 0 : -1) : 1;
  }

  if (typeof l === 'string' && typeof r === 'string') {
    return l < r ? -1 : l > r ? 1 : 0;
  }

  throw new ExpressionError(
    'expr.type_error',
    `Cannot compare ${typeName(l)} with ${typeName(r)} using '${bin.op}'.`,
    bin.position,
  );
}

function divideByZero(position: number): ExpressionError {
  return new ExpressionError('expr.divide_by_zero', 'Division by zero.', position);
}

function describe(v: unknown): string {
  return typeof v === 'string' ? `'${v}'` : typeName(v);
}

export function typeName(v: unknown): string {
  if (isNull(v)) return 'null';
  switch (typeof v) {
    case 'boolean':
      return 'true/false';
    case 'number':
      return 'a number';
    case 'string':
      return 'text';
  }

  if (Array.isArray(v)) return 'a list';
  if (isMap(v)) return 'a map';
  return typeof v;
}
