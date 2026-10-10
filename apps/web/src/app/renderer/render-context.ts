import { Injectable, computed, signal } from '@angular/core';
import { ChoiceList, ChoiceOption, EntityClientSpec, FieldSpec, FieldStates, RecordView, ViewClientSpec } from '../core/models';
import { evaluateRules } from './rules';

export type RenderMode = 'view' | 'form' | 'draft';

/**
 * Per-page rendering state shared by every element component. Values flow from the record; edits go through
 * the page's handlers (inline save, form value, or draft try-out). Field visibility and editability combine the
 * viewer's field permissions, server field states, and client-evaluated rules (REQ-RULE-003, REQ-AUTHZ-004).
 */
@Injectable()
export class RenderContext {
  readonly spec = signal<ViewClientSpec | null>(null);
  readonly record = signal<RecordView | null>(null);
  readonly mode = signal<RenderMode>('view');
  /** Form/draft values not yet saved. */
  readonly draftValues = signal<Record<string, any>>({});
  readonly errors = signal<Record<string, string>>({});
  readonly changedTargets = signal<Set<string>>(new Set());
  readonly saving = signal<Set<string>>(new Set());

  /** Handlers installed by the page. */
  commitField: (key: string, value: unknown) => Promise<void> = async () => undefined;
  runTransition: (id: string) => Promise<void> = async () => undefined;

  readonly entity = computed<EntityClientSpec | null>(() => this.spec()?.entity ?? null);

  readonly values = computed<Record<string, any>>(() => ({ ...(this.record()?.data ?? {}), ...this.draftValues() }));

  /** Server states merged with client rule evaluation on the current (possibly unsaved) values. */
  readonly states = computed<FieldStates>(() => {
    const server = this.record()?.fieldStates ?? { required: [], readOnly: [], hidden: [], options: {}, messages: [] };
    const entity = this.entity();
    if (!entity) return server;
    const client = evaluateRules(entity, this.values(), this.mode() === 'view' ? null : this.record()?.data ?? null);
    return {
      required: unique([...client.required, ...(this.mode() === 'view' ? server.required : [])]),
      readOnly: unique([...server.readOnly.filter(() => this.mode() === 'view'), ...client.readOnly]),
      hidden: unique([...server.hidden, ...client.hidden]),
      options: { ...server.options, ...client.options },
      messages: client.messages.length ? client.messages : server.messages,
    };
  });

  field(key: string): FieldSpec | undefined {
    return this.entity()?.entity.fields.find((f) => f.key === key);
  }

  choiceList(field: FieldSpec | undefined): ChoiceList | undefined {
    return field?.choiceList ? this.entity()?.choiceLists.find((c) => c.id === field.choiceList) : undefined;
  }

  option(field: FieldSpec | undefined, id: unknown): ChoiceOption | undefined {
    return this.choiceList(field)?.options.find((o) => o.id === id);
  }

  allowedOptions(field: FieldSpec): ChoiceOption[] {
    const list = this.choiceList(field)?.options.filter((o) => !o.hidden) ?? [];
    const restricted = this.states().options[field.key];
    return restricted ? list.filter((o) => restricted.includes(o.id)) : list;
  }

  isHidden(key: string): boolean {
    const f = this.field(key);
    return !f || f.hidden === true || this.entity()?.fieldAccess[key] === 'hidden' || this.states().hidden.includes(key);
  }

  isRequired(key: string): boolean {
    return (this.field(key)?.required ?? false) || this.states().required.includes(key);
  }

  canEdit(key: string): boolean {
    const f = this.field(key);
    if (!f || f.system || f.readOnly || f.deprecated || f.type === 'auto_number') return false;
    if (this.states().readOnly.includes(key)) return false;
    if (this.mode() === 'draft' || this.mode() === 'form') return this.entity()?.fieldAccess[key] === 'write';
    const access = this.record()?.access;
    return !!access?.canUpdate && access.fields[key] === 'write';
  }

  display(key: string): string {
    const f = this.field(key);
    const v = this.values()[key];
    if (v === undefined || v === null || v === '') return '';
    if (!f) return String(v);
    switch (f.type) {
      case 'single_select':
      case 'status':
      case 'priority':
        return this.option(f, v)?.label ?? String(v);
      case 'multi_select':
        return (v as string[]).map((id) => this.option(f, id)?.label ?? id).join(', ');
      case 'user':
      case 'group':
      case 'lookup':
        return this.record()?.display[key] ?? (this.mode() === 'draft' ? 'Sample' : '…');
      case 'toggle':
        return v ? 'Yes' : 'No';
      case 'date_time':
        return new Date(v).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
      case 'date':
        return new Date(v + 'T00:00:00').toLocaleDateString(undefined, { dateStyle: 'medium' });
      case 'rich_text':
        return String(v).replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim();
      default:
        return String(v);
    }
  }

  tone(field: FieldSpec | undefined, value: unknown): string {
    const t = this.option(field, value)?.tone;
    return t ? 'tone-' + t.replace('accent.', '') : 'tone-gray';
  }

  isChanged(nodeId: string): boolean {
    return this.changedTargets().has(nodeId);
  }

  setValue(key: string, value: unknown): void {
    this.draftValues.update((d) => ({ ...d, [key]: value }));
    this.errors.update((e) => {
      const { [key]: _, ...rest } = e;
      return rest;
    });
  }
}

function unique(list: string[]): string[] {
  return [...new Set(list)];
}
