import { compile } from '../core/expressions';
import { EntityClientSpec } from '../core/models';

export interface ClientRuleResult {
  required: string[];
  hidden: string[];
  readOnly: string[];
  options: Record<string, string[]>;
  messages: { rule: string; tone: string; text: string; field?: string }[];
  matched: string[];
}

const cache = new Map<string, ReturnType<typeof compile> | null>();

function compiled(expr: string) {
  if (!cache.has(expr)) {
    try {
      cache.set(expr, compile(expr));
    } catch {
      cache.set(expr, null);
    }
  }
  return cache.get(expr) ?? null;
}

/**
 * Client-side evaluation of the entity's data rules for instant feedback. The server re-evaluates everything on
 * save (authoritative); rules that read fields hidden from this viewer are never sent here (REQ-RULE-006).
 * Read-only is evaluated against the saved record (prior state), like the server.
 */
export function evaluateRules(entity: EntityClientSpec, values: Record<string, any>, prior: Record<string, any> | null): ClientRuleResult {
  const result: ClientRuleResult = { required: [], hidden: [], readOnly: [], options: {}, messages: [], matched: [] };
  const vars = { record: values, prior: prior ?? {}, user: {}, now: new Date().toISOString() };
  const priorVars = { ...vars, record: prior ?? values };
  const rules = [...entity.rules].filter((r) => r.kind === 'conditional' && r.enabled !== false).sort((a, b) => a.order - b.order || a.id.localeCompare(b.id));
  for (const rule of rules) {
    const test = rule.when ? compiled(rule.when) : null;
    let matches = true;
    let matchesPrior = true;
    if (rule.when) {
      if (!test) continue;
      try {
        matches = test.evaluateBool(vars);
        matchesPrior = test.evaluateBool(priorVars);
      } catch {
        continue;
      }
    }
    if (matches) result.matched.push(rule.id);
    for (const a of rule.actions) {
      if (a.type === 'read_only' && matchesPrior && a.field) result.readOnly.push(a.field);
      if (!matches) continue;
      if (a.type === 'require' && a.field) result.required.push(a.field);
      if (a.type === 'hide' && a.field) result.hidden.push(a.field);
      if (a.type === 'show' && a.field) result.hidden = result.hidden.filter((h) => h !== a.field);
      if (a.type === 'restrict_options' && a.field) result.options[a.field] = a.options ?? [];
      if (a.type === 'message' && a.message) result.messages.push({ rule: rule.id, tone: a.tone ?? 'info', text: a.message, field: a.field });
    }
  }
  return result;
}

/** Evaluates a visibility condition such as display.callout visibleWhen. Errors hide the element. */
export function visibleWhen(expr: string | undefined, values: Record<string, any>): boolean {
  if (!expr) return true;
  const c = compiled(expr);
  if (!c) return false;
  try {
    return c.evaluateBool({ record: values, prior: {}, user: {}, now: new Date().toISOString() });
  } catch {
    return false;
  }
}
