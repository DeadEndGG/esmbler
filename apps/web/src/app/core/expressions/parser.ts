import { ExpressionError, ExpressionLimits, Node } from './ast';
import { Token, tokenize } from './lexer';

/** Recursive-descent parser. Mirrors Esmbler.Expressions/Parser.cs (grammar in ADR 0009). */
export function parse(source: string, limits: ExpressionLimits): Node {
  const parser = new Parser(tokenize(source, limits.maxLength), limits.maxDepth);
  const node = parser.parseExpr();
  if (parser.peek.kind !== 'end') {
    throw new ExpressionError('expr.syntax', `Unexpected '${parser.peek.text}'.`, parser.peek.position);
  }

  return node;
}

const REL_OPS = new Set(['==', '!=', '<', '<=', '>', '>=']);

class Parser {
  private pos = 0;
  private depth = 0;

  constructor(
    private readonly tokens: Token[],
    private readonly maxDepth: number,
  ) {}

  get peek(): Token {
    return this.tokens[this.pos];
  }

  private next(): Token {
    return this.tokens[this.pos++];
  }

  private isOp(op: string): boolean {
    return this.peek.kind === 'op' && this.peek.text === op;
  }

  private isIdent(name: string): boolean {
    return this.peek.kind === 'ident' && this.peek.text === name;
  }

  private expect(op: string): void {
    if (!this.isOp(op)) {
      const found = this.peek.kind === 'end' ? 'end of expression' : `'${this.peek.text}'`;
      throw new ExpressionError('expr.syntax', `Expected '${op}' but found ${found}.`, this.peek.position);
    }

    this.pos++;
  }

  private tryConsume(op: string): boolean {
    if (this.isOp(op)) {
      this.pos++;
      return true;
    }

    return false;
  }

  private enter(parse: () => Node): Node {
    if (++this.depth > this.maxDepth) {
      throw new ExpressionError('expr.too_complex', `Expression nesting exceeds ${this.maxDepth} levels.`, this.peek.position);
    }

    try {
      return parse();
    } finally {
      this.depth--;
    }
  }

  parseExpr(): Node {
    return this.enter(() => {
      const test = this.parseOr();
      if (this.isOp('?')) {
        const position = this.next().position;
        const ifTrue = this.parseExpr();
        this.expect(':');
        const ifFalse = this.parseExpr();
        return { kind: 'conditional', test, ifTrue, ifFalse, position };
      }

      return test;
    });
  }

  private parseOr(): Node {
    let left = this.parseAnd();
    while (this.isOp('||')) {
      const position = this.next().position;
      left = { kind: 'binary', op: '||', left, right: this.parseAnd(), position };
    }

    return left;
  }

  private parseAnd(): Node {
    let left = this.parseRel();
    while (this.isOp('&&')) {
      const position = this.next().position;
      left = { kind: 'binary', op: '&&', left, right: this.parseRel(), position };
    }

    return left;
  }

  private parseRel(): Node {
    const left = this.parseAdd();
    if (this.peek.kind === 'op' && REL_OPS.has(this.peek.text)) {
      const op = this.next();
      return { kind: 'binary', op: op.text, left, right: this.parseAdd(), position: op.position };
    }

    if (this.isIdent('in')) {
      const position = this.next().position;
      return { kind: 'binary', op: 'in', left, right: this.parseAdd(), position };
    }

    return left;
  }

  private parseAdd(): Node {
    let left = this.parseMul();
    while (this.peek.kind === 'op' && (this.peek.text === '+' || this.peek.text === '-')) {
      const op = this.next();
      left = { kind: 'binary', op: op.text, left, right: this.parseMul(), position: op.position };
    }

    return left;
  }

  private parseMul(): Node {
    let left = this.parseUnary();
    while (this.peek.kind === 'op' && (this.peek.text === '*' || this.peek.text === '/' || this.peek.text === '%')) {
      const op = this.next();
      left = { kind: 'binary', op: op.text, left, right: this.parseUnary(), position: op.position };
    }

    return left;
  }

  private parseUnary(): Node {
    if (this.peek.kind === 'op' && (this.peek.text === '!' || this.peek.text === '-')) {
      const op = this.next();
      return this.enter(() => ({ kind: 'unary', op: op.text as '!' | '-', operand: this.parseUnary(), position: op.position }));
    }

    return this.parseMember();
  }

  private parseMember(): Node {
    let node = this.parsePrimary();
    while (true) {
      if (this.isOp('.')) {
        this.pos++;
        const name = this.next();
        if (name.kind !== 'ident') {
          throw new ExpressionError('expr.syntax', "Expected a field or method name after '.'.", name.position);
        }

        node = this.isOp('(')
          ? { kind: 'call', fn: name.text, target: node, args: this.parseArgs(), position: name.position }
          : { kind: 'select', operand: node, field: name.text, position: name.position };
      } else if (this.isOp('[')) {
        const position = this.next().position;
        const key = this.parseExpr();
        this.expect(']');
        node = { kind: 'index', operand: node, key, position };
      } else {
        return node;
      }
    }
  }

  private parseArgs(): Node[] {
    this.expect('(');
    const args: Node[] = [];
    if (!this.isOp(')')) {
      do {
        args.push(this.parseExpr());
      } while (this.tryConsume(','));
    }

    this.expect(')');
    return args;
  }

  private parsePrimary(): Node {
    const token = this.peek;
    switch (token.kind) {
      case 'number':
      case 'string':
        this.pos++;
        return { kind: 'literal', value: token.value, position: token.position };
      case 'ident':
        this.pos++;
        switch (token.text) {
          case 'true':
            return { kind: 'literal', value: true, position: token.position };
          case 'false':
            return { kind: 'literal', value: false, position: token.position };
          case 'null':
            return { kind: 'literal', value: null, position: token.position };
        }

        if (this.isOp('(')) {
          return { kind: 'call', fn: token.text, target: null, args: this.parseArgs(), position: token.position };
        }

        return { kind: 'ident', name: token.text, position: token.position };
      case 'end':
        throw new ExpressionError('expr.syntax', 'Unexpected end of expression.', token.position);
      case 'op':
        if (token.text === '(') {
          this.pos++;
          const inner = this.parseExpr();
          this.expect(')');
          return inner;
        }

        if (token.text === '[') {
          this.pos++;
          const items: Node[] = [];
          if (!this.isOp(']')) {
            do {
              items.push(this.parseExpr());
            } while (this.tryConsume(','));
          }

          this.expect(']');
          return { kind: 'list', items, position: token.position };
        }

        throw new ExpressionError('expr.syntax', `Unexpected '${token.text}'.`, token.position);
    }
  }
}
