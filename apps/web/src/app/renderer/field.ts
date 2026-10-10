import { NgTemplateOutlet } from '@angular/common';
import { Component, ElementRef, computed, effect, inject, input, signal, viewChild } from '@angular/core';
import { Api } from '../core/api';
import { Directory } from '../core/directory';
import { NodeSpec, RecordPage } from '../core/models';
import { RenderContext } from './render-context';

/**
 * Field placement: label + value. In view mode, writable values edit inline (click or press E; Enter commits,
 * Esc cancels). In form and draft mode the control is always editable so rules can be tried immediately.
 */
@Component({
  selector: 'esm-field',
  imports: [NgTemplateOutlet],
  templateUrl: './field.html',
  styleUrl: './field.scss',
})
export class FieldPlacement {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(RenderContext);
  protected readonly directory = inject(Directory);
  private readonly api = inject(Api);
  private readonly editorRef = viewChild<ElementRef<HTMLElement>>('editor');

  protected readonly key = computed(() => this.node().props['field'] as string);
  protected readonly spec = computed(() => this.ctx.field(this.key()));
  protected readonly label = computed(() => this.node().props['label'] ?? this.spec()?.label ?? this.key());
  protected readonly editing = signal(false);
  protected readonly alwaysEditable = computed(() => this.ctx.mode() !== 'view');
  /** Request answers and the catalog item are set when the request is submitted, not edited inline. */
  protected readonly editable = computed(() => this.ctx.canEdit(this.key()) && !this.node().props['readOnly'] && !['variables', 'catalog_item', 'principals'].includes(this.spec()?.type ?? ''));
  protected readonly value = computed(() => this.ctx.values()[this.key()]);
  protected readonly error = computed(() => this.ctx.errors()[this.key()]);
  protected readonly required = computed(() => this.ctx.isRequired(this.key()));
  protected readonly lookupResults = signal<{ id: string; label: string }[]>([]);
  protected readonly answers = computed(() =>
    this.ctx.answers((type, id) => (type === 'user' ? this.directory.people().find((p) => p.id === id)?.displayName : this.directory.groups().find((g) => g.id === id)?.name)),
  );
  protected local: any = null;
  protected readonly regexTags = /<[^>]+>/g;

  constructor() {
    effect(() => {
      const t = this.spec()?.type;
      if (t === 'user' || t === 'group' || t === 'variables') void this.directory.load();
    });
  }

  protected start(): void {
    if (!this.editable() || this.alwaysEditable()) return;
    this.local = this.toEditor(this.value());
    this.editing.set(true);
    setTimeout(() => (this.editorRef()?.nativeElement as HTMLInputElement | undefined)?.focus?.());
  }

  protected async commit(): Promise<void> {
    if (!this.editing()) return;
    this.editing.set(false);
    const next = this.fromEditor(this.local);
    if (JSON.stringify(next ?? null) !== JSON.stringify(this.value() ?? null)) {
      await this.ctx.commitField(this.key(), next);
    }
  }

  protected cancel(): void {
    this.editing.set(false);
  }

  protected onEditorKey(e: KeyboardEvent): void {
    if (e.key === 'Escape') {
      e.stopPropagation();
      this.cancel();
    } else if (e.key === 'Enter' && (this.spec()?.type !== 'long_text' && this.spec()?.type !== 'rich_text' || e.metaKey || e.ctrlKey)) {
      e.preventDefault();
      void this.commit();
    }
  }

  protected onValueKey(e: KeyboardEvent): void {
    if (e.key === 'e' || e.key === 'Enter') {
      e.preventDefault();
      this.start();
    }
  }

  protected formChange(value: any): void {
    this.ctx.setValue(this.key(), this.fromEditor(value));
  }

  protected toggleMulti(id: string): void {
    const current: string[] = Array.isArray(this.value()) ? this.value() : [];
    const next = current.includes(id) ? current.filter((x) => x !== id) : [...current, id];
    if (this.alwaysEditable()) this.ctx.setValue(this.key(), next);
    else void this.ctx.commitField(this.key(), next);
  }

  protected async searchLookup(q: string): Promise<void> {
    const target = this.spec()?.target;
    if (!target || q.trim().length < 1) {
      this.lookupResults.set([]);
      return;
    }
    const page = await this.api.get<RecordPage>(`/api/records/${target}`, { search: q, size: 8 });
    this.lookupResults.set(page.items.map((r) => ({ id: r.id, label: `${r.number ?? ''} ${r.data['title'] ?? ''}`.trim() })));
  }

  protected pickLookup(id: string): void {
    this.lookupResults.set([]);
    if (this.alwaysEditable()) this.ctx.setValue(this.key(), id);
    else {
      this.editing.set(false);
      void this.ctx.commitField(this.key(), id);
    }
  }

  protected isRich(): boolean {
    return this.spec()?.type === 'rich_text';
  }

  private toEditor(v: any): any {
    const t = this.spec()?.type;
    if (v === null || v === undefined) return t === 'toggle' ? false : '';
    if (t === 'rich_text') return String(v).replace(/<\/p>\s*<p>/g, '\n\n').replace(/<br\s*\/?>/g, '\n').replace(/<[^>]+>/g, '');
    if (t === 'date_time') return toLocalInput(v);
    return v;
  }

  private fromEditor(v: any): any {
    const t = this.spec()?.type;
    if (v === '' || v === undefined) return null;
    switch (t) {
      case 'integer':
      case 'decimal':
        return v === null ? null : Number(v);
      case 'date_time':
        return v ? new Date(v).toISOString() : null;
      case 'rich_text':
        return String(v).split(/\n{2,}/).map((p) => `<p>${escapeHtml(p).replace(/\n/g, '<br>')}</p>`).join('');
      default:
        return v;
    }
  }
}

function toLocalInput(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function escapeHtml(s: string): string {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
