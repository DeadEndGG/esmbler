import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { NodeSpec, RecordPage } from '../core/models';
import { RenderContext } from './render-context';

/** collection.related_list: the one-to-many side; shows only children the viewer may read. */
@Component({
  selector: 'esm-related-list',
  imports: [RouterLink],
  template: `
    @let rel = relationship();
    <div class="related">
      <table>
        <thead><tr>@for (c of columns(); track c) { <th scope="col">{{ label(c) }}</th> }</tr></thead>
        <tbody>
          @for (r of page()?.items ?? []; track r.id) {
            <tr>
              @for (c of columns(); track c; let first = $first) {
                <td>
                  @if (first) { <a [routerLink]="['/records', r.entity, r.id]">{{ cell(r, c) }}</a> } @else { {{ cell(r, c) }} }
                </td>
              }
            </tr>
          } @empty {
            <tr><td [attr.colspan]="columns().length" class="muted">{{ ctx.mode() === 'draft' ? (rel?.inverseLabel ?? 'Related records') + ' appear here on real records.' : 'None yet.' }}</td></tr>
          }
        </tbody>
      </table>
      @if (page()?.total ?? 0 > (page()?.items?.length ?? 0)) { <p class="muted more">{{ page()?.total }} in total</p> }
    </div>
  `,
  styles: `
    .related { border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); overflow: hidden; }
    table { width: 100%; border-collapse: collapse; font: var(--esm-text-body-sm); }
    th { text-align: left; color: var(--esm-text-muted); font: var(--esm-text-caption); letter-spacing: 0.04em; text-transform: uppercase; padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); background: var(--esm-surface-sunken); }
    td { padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); }
    tbody tr:last-child td { border-bottom: 0; }
    td a { font: var(--esm-text-mono); font-weight: 500; }
    .more { font: var(--esm-text-body-sm); margin: 0; padding: var(--esm-space-2) var(--esm-space-3); border-top: 1px solid var(--esm-border-subtle); }
  `,
})
export class RelatedList {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  protected readonly page = signal<RecordPage | null>(null);
  protected readonly relationship = computed(() => this.ctx.entity()?.relationships.find((r) => r.id === this.node().props['relationship']));
  protected readonly columns = computed<string[]>(() => this.node().props['columns'] ?? ['number', 'title']);
  private readonly childSpec = computed(() => this.ctx.spec()?.relatedEntities[this.relationship()?.entity ?? '']);

  constructor() {
    effect(() => {
      const r = this.ctx.record();
      const rel = this.relationship();
      if (r && rel && this.ctx.mode() !== 'draft' && r.revision > 0) {
        void this.api.get<RecordPage>(`/api/records/${r.entity}/${r.id}/related/${rel.id}`, { size: this.node().props['pageSize'] ?? 10 }).then((p) => this.page.set(p)).catch(() => this.page.set(null));
      }
    });
  }

  protected label(key: string): string {
    return this.childSpec()?.entity.fields.find((f) => f.key === key)?.label ?? key;
  }

  protected cell(r: { data: Record<string, any>; display: Record<string, string> }, key: string): string {
    const field = this.childSpec()?.entity.fields.find((f) => f.key === key);
    const v = r.data[key];
    if (v === undefined || v === null) return '—';
    if (field?.choiceList) return this.childSpec()?.choiceLists.find((c) => c.id === field.choiceList)?.options.find((o) => o.id === v)?.label ?? String(v);
    return r.display[key] ?? String(v);
  }
}
