import { ExpressionError, isWhiteSpace } from './ast';

export type TokenKind = 'number' | 'string' | 'ident' | 'op' | 'end';

export interface Token {
  readonly kind: TokenKind;
  readonly text: string;
  readonly value: number | string | null;
  readonly position: number;
}

const OPS = ['==', '!=', '<=', '>=', '&&', '||', '<', '>', '!', '+', '-', '*', '/', '%', '(', ')', '[', ']', '.', ',', '?', ':'];

const isDigit = (c: string): boolean => c >= '0' && c <= '9';
const isLetter = (c: string): boolean => /^\p{L}$/u.test(c);
const isLetterOrDigit = (c: string): boolean => /^[\p{L}\p{Nd}]$/u.test(c);

/** Mirrors Esmbler.Expressions/Lexer.cs. Positions are UTF-16 offsets, like .NET. */
export function tokenize(source: string, maxLength: number): Token[] {
  if (source.length > maxLength) {
    throw new ExpressionError('expr.too_long', `Expression is longer than ${maxLength} characters.`);
  }

  const tokens: Token[] = [];
  let i = 0;
  while (i < source.length) {
    const c = source[i];
    if (isWhiteSpace(c)) {
      i++;
      continue;
    }

    if (isDigit(c)) {
      const start = i;
      while (i < source.length && isDigit(source[i])) i++;
      if (i + 1 < source.length && source[i] === '.' && isDigit(source[i + 1])) {
        i++;
        while (i < source.length && isDigit(source[i])) i++;
      }

      const text = source.slice(start, i);
      tokens.push({ kind: 'number', text, value: Number(text), position: start });
      continue;
    }

    if (isLetter(c) || c === '_') {
      const start = i;
      while (i < source.length && (isLetterOrDigit(source[i]) || source[i] === '_')) i++;
      tokens.push({ kind: 'ident', text: source.slice(start, i), value: null, position: start });
      continue;
    }

    if (c === "'" || c === '"') {
      const start = i;
      const quote = c;
      i++;
      let sb = '';
      let closed = false;
      while (i < source.length) {
        const ch = source[i];
        if (ch === '\\' && i + 1 < source.length) {
          const next = source[i + 1];
          sb += next === 'n' ? '\n' : next === 't' ? '\t' : next === 'r' ? '\r' : next;
          i += 2;
          continue;
        }

        if (ch === quote) {
          closed = true;
          i++;
          break;
        }

        sb += ch;
        i++;
      }

      if (!closed) {
        throw new ExpressionError('expr.syntax', 'Unterminated string literal.', start);
      }

      tokens.push({ kind: 'string', text: source.slice(start, i), value: sb, position: start });
      continue;
    }

    const op = OPS.find((o) => source.startsWith(o, i));
    if (op === undefined) {
      throw new ExpressionError('expr.syntax', `Unexpected character '${c}'.`, i);
    }

    tokens.push({ kind: 'op', text: op, value: null, position: i });
    i += op.length;
  }

  tokens.push({ kind: 'end', text: '', value: null, position: source.length });
  return tokens;
}
