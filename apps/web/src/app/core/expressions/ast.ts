/** Abstract syntax tree for the CEL-subset expression language (ADR 0009). Mirrors Esmbler.Expressions/Ast.cs. */

export type LiteralValue = null | boolean | number | string;

export interface Literal {
  readonly kind: 'literal';
  readonly value: LiteralValue;
  readonly position: number;
}

export interface Ident {
  readonly kind: 'ident';
  readonly name: string;
  readonly position: number;
}

export interface Select {
  readonly kind: 'select';
  readonly operand: Node;
  readonly field: string;
  readonly position: number;
}

export interface Index {
  readonly kind: 'index';
  readonly operand: Node;
  readonly key: Node;
  readonly position: number;
}

export interface Call {
  readonly kind: 'call';
  readonly fn: string;
  readonly target: Node | null;
  readonly args: readonly Node[];
  readonly position: number;
}

export interface Unary {
  readonly kind: 'unary';
  readonly op: '!' | '-';
  readonly operand: Node;
  readonly position: number;
}

export interface Binary {
  readonly kind: 'binary';
  readonly op: string;
  readonly left: Node;
  readonly right: Node;
  readonly position: number;
}

export interface Conditional {
  readonly kind: 'conditional';
  readonly test: Node;
  readonly ifTrue: Node;
  readonly ifFalse: Node;
  readonly position: number;
}

export interface ListLiteral {
  readonly kind: 'list';
  readonly items: readonly Node[];
  readonly position: number;
}

export type Node = Literal | Ident | Select | Index | Call | Unary | Binary | Conditional | ListLiteral;

/** Static types known to the checker. `any` disables type checks for a value. */
export type ValueType = 'any' | 'null' | 'bool' | 'number' | 'string' | 'list' | 'map';

/** A variable with `fields` is a map whose keys are checked (e.g. `record.category`). */
export interface VariableSchema {
  type: ValueType;
  fields?: Record<string, ValueType>;
}

/** Describes the variables an expression may reference. */
export type ExpressionSchema = Record<string, VariableSchema>;

/** Static cost bounds. Expressions exceeding them are rejected at compile time. */
export interface ExpressionLimits {
  maxLength: number;
  maxDepth: number;
  maxNodes: number;
}

export const DEFAULT_LIMITS: Readonly<ExpressionLimits> = Object.freeze({ maxLength: 2000, maxDepth: 32, maxNodes: 256 });

/** Raised for syntax, check, or evaluation errors. `code` is stable and machine readable. */
export class ExpressionError extends Error {
  constructor(
    readonly code: string,
    message: string,
    readonly position: number = -1,
    readonly suggestion?: string,
  ) {
    super(message);
    this.name = 'ExpressionError';
  }
}

/** Whitespace as defined by .NET's char.IsWhiteSpace (differs from JS \s on U+0085 and U+FEFF). */
export function isWhiteSpace(c: string): boolean {
  return c === '\u0085' || (c !== '﻿' && /^\s$/.test(c));
}

export function hasOwn(obj: object, key: string): boolean {
  return Object.prototype.hasOwnProperty.call(obj, key);
}
