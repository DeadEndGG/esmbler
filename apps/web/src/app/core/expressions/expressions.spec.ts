import cases from '../../../../../../packages/Esmbler.Expressions/conformance/cases.json' with { type: 'json' };
import { compile, ExpressionError, ExpressionSchema, formatNumber, suggest, valueEquals } from './index';

interface ConformanceCase {
  expr: string;
  expect?: unknown;
  error?: string;
  phase?: string;
  vars?: Record<string, unknown>;
}

/** REQ-RULE-003: server and client evaluators stay aligned via shared conformance cases (ADR 0009). */
describe('expressions conformance (cases.json)', () => {
  const schema: ExpressionSchema = { record: { type: 'map' }, user: { type: 'map' } };
  const sharedVars = cases.vars as Record<string, unknown>;

  for (const c of cases.cases as ConformanceCase[]) {
    it(c.expr, () => {
      const vars = c.vars ?? sharedVars;
      if (c.error !== undefined) {
        const err = captureError(() => compile(c.expr, schema).evaluate(vars));
        expect(err.code).toBe(c.error);
        if (c.phase === 'check') {
          expect(captureError(() => compile(c.expr, schema)).code).toBe(c.error);
        }

        return;
      }

      const actual = compile(c.expr, schema).evaluate(vars);
      expect(valueEquals(c.expect, actual) && sameKind(c.expect, actual), `expected ${show(c.expect)} got ${show(actual)}`).toBe(true);
      expect(actual).toEqual(c.expect ?? null);
    });
  }
});

describe('expressions checker', () => {
  it('unknown field suggests closest match', () => {
    const schema: ExpressionSchema = { record: { type: 'map', fields: { justification: 'string', category: 'number' } } };
    const err = captureError(() => compile('has(record.justifcation)', schema));
    expect(err.code).toBe('expr.unknown_field');
    expect(err.suggestion).toBe('record.justification');
    expect(err.message).toBe("Unknown field 'record.justifcation'. Did you mean 'record.justification'?");
  });

  it('type mismatch on typed field explains fix', () => {
    const schema: ExpressionSchema = { record: { type: 'map', fields: { category: 'number' } } };
    const err = captureError(() => compile("record.category == '8'", schema));
    expect(err.code).toBe('expr.type_mismatch');
    expect(err.message).toContain("'record.category' is a number");
    expect(err.message).toBe(
      "Cannot compare a number with text using '=='. 'record.category' is a number; compare it with a number value.",
    );
  });

  it('node budget is enforced', () => {
    const big = Array.from({ length: 300 }, () => '1').join(' + ');
    expect(captureError(() => compile(big)).code).toBe('expr.too_complex');
  });

  it('depth and length limits are enforced', () => {
    expect(captureError(() => compile('('.repeat(40) + '1' + ')'.repeat(40))).code).toBe('expr.too_complex');
    expect(captureError(() => compile('1'.repeat(2001))).code).toBe('expr.too_long');
    expect(captureError(() => compile('1 + 1', null, { maxLength: 3 })).code).toBe('expr.too_long');
  });

  it('references are collected', () => {
    const compiled = compile('record.category == 8 && has(record.justification)', { record: { type: 'map' } });
    expect([...compiled.references].sort()).toEqual(['record.category', 'record.justification']);
  });

  it('references are collected without a schema', () => {
    expect([...compile('a.b + c.d').references].sort()).toEqual(['a.b', 'c.d']);
  });

  it('unknown variable and function suggest names', () => {
    const err = captureError(() => compile('recrd.category == 1', { record: { type: 'map' } }));
    expect(err.code).toBe('expr.unknown_variable');
    expect(err.suggestion).toBe('record');
    const fn = captureError(() => compile("'a'.startWith('a')"));
    expect(fn.code).toBe('expr.unknown_function');
    expect(fn.suggestion).toBe('startsWith');
  });

  it('rejects misused calls', () => {
    expect(captureError(() => compile("'a'.int()")).code).toBe('expr.bad_call');
    expect(captureError(() => compile("trim('a')")).code).toBe('expr.bad_call');
    expect(captureError(() => compile('size(1, 2)')).code).toBe('expr.bad_call');
    expect(captureError(() => compile("'a'.size(1)")).code).toBe('expr.bad_call');
    expect(captureError(() => compile('has(1)')).code).toBe('expr.bad_call');
  });
});

describe('expressions evaluator', () => {
  const vars = cases.vars as Record<string, unknown>;
  const run = (expr: string) => compile(expr).evaluate(vars);

  it('formats numbers like the C# twin', () => {
    expect(run('string(3.5)')).toBe('3.5');
    expect(run('string(-0)')).toBe('-0');
    expect(formatNumber(1e15)).toBe('1000000000000000');
    expect(formatNumber(1e17)).toBe('1E+17');
    expect(formatNumber(1.5e-7)).toBe('1.5E-07');
    expect(formatNumber(0.000125)).toBe('0.000125');
    expect(formatNumber(1.2345678901234568e18)).toBe('1.2345678901234568E+18');
  });

  it('converts strings like .NET parsing', () => {
    expect(run("int(' -42 ')")).toBe(-42);
    expect(captureError(() => run("int('4.2')")).code).toBe('expr.conversion');
    expect(captureError(() => run("int('9223372036854775808')")).code).toBe('expr.conversion');
    expect(run("double('.5')")).toBe(0.5);
    expect(run("double('1e3')")).toBe(1000);
    expect(captureError(() => run("double('0x10')")).code).toBe('expr.conversion');
    expect(captureError(() => run('int(true)')).code).toBe('expr.conversion');
  });

  it('reports runtime errors with C# codes', () => {
    expect(captureError(() => run('5 % 0')).code).toBe('expr.divide_by_zero');
    expect(captureError(() => run("'x'.matches('(')")).code).toBe('expr.bad_regex');
    expect(captureError(() => run('record.tags[0.5]')).code).toBe('expr.index_out_of_range');
    expect(captureError(() => run("double('1e308') * 10")).code).toBe('expr.overflow');
    expect(captureError(() => compile('record.title').evaluateBool(vars)).code).toBe('expr.not_boolean');
  });

  it('supports maps, in, and the conditional operator', () => {
    expect(run("record['title']")).toBe('VPN down');
    expect(run("'a' in record.nested")).toBe(true);
    expect(run("'category' in record")).toBe(true);
    expect(run("record.priority > 1 ? 'high' : 'low'")).toBe('high');
    expect(run("'é'.upperAscii()")).toBe('é');
    expect(compile('record.priority == 2').evaluateBool(vars)).toBe(true);
  });

  it('exposes suggest', () => {
    expect(suggest('categroy', ['category', 'priority'])).toBe('category');
    expect(suggest('zzzzzz', ['category'])).toBeUndefined();
  });
});

function captureError(fn: () => unknown): ExpressionError {
  try {
    fn();
  } catch (e) {
    if (e instanceof ExpressionError) return e;
    throw e;
  }

  throw new Error('Expected an ExpressionError to be thrown.');
}

function sameKind(a: unknown, b: unknown): boolean {
  const kind = (v: unknown) => (v === null || v === undefined ? 'null' : Array.isArray(v) ? 'list' : typeof v);
  return kind(a) === kind(b);
}

function show(v: unknown): string {
  return JSON.stringify(v);
}
