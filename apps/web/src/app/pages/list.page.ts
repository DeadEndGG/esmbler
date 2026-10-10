import { Component, DestroyRef, ElementRef, computed, effect, inject, input, signal, untracked, viewChildren } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { BuilderState } from '../builder/builder-state';
import { Api, ApiError } from '../core/api';
import { Directory } from '../core/directory';
import { DraftViewSpec, FieldSpec, NodeSpec, RecordPage, RecordView, ViewClientSpec } from '../core/models';
import { Session } from '../core/session';
import { Shortcuts } from '../core/shortcuts';
import { Toasts } from '../core/toast';

interface SavedView {
  id: string;
  label: string;
  filter?: string;
  sort?: { field: string; direction: 'asc' | 'desc' };
}

/**
 * collection.data_table with its saved views and filter bar (REQ-UX-*): keyboard navigation (J/K, Enter, C),
 * sortable columns, search, inline status/priority edits with optimistic update and rollback.
 */
@Component({
  selector: 'esm-list-page',
  imports: [RouterLink, FormsModule],
  template: `
    @let s = spec();
    <div class="page">
      <header class="top">
        <h1>{{ s?.view?.layout?.props?.['title'] ?? nav()?.pluralLabel ?? 'Records' }}</h1>
        <span class="flex"></span>
        @if (nav()?.canCreate) {
          <a class="btn btn-primary" [routerLink]="['/records', entity(), 'new']">New {{ s?.entity?.entity?.label?.toLowerCase() ?? 'record' }} <kbd>C</kbd></a>
        }
      </header>

      @if (savedViews().length) {
        <nav class="views" aria-label="Saved views">
          @for (v of savedViews(); track v.id) {
            <button class="view" [class.active]="activeView()?.id === v.id" [attr.aria-pressed]="activeView()?.id === v.id" (click)="selectView(v)">{{ v.label }}</button>
          }
        </nav>
      }

      <div class="filters" role="search">
        @if (filterBar()?.props?.['showSearch'] !== false) {
          <input class="input search" type="search" placeholder="Search" aria-label="Search records" [ngModel]="search()" (ngModelChange)="onSearch($event)" #searchBox />
        }
        @for (f of filterFields(); track f.key) {
          <select class="input" [attr.aria-label]="'Filter by ' + f.label" [ngModel]="filters()[f.key] ?? ''" (ngModelChange)="setFilter(f.key, $event)">
            <option value="">{{ f.label }}: any</option>
            @if (f.type === 'user') {
              <option value="@me">Me</option>
              @for (p of directory.people(); track p.id) { <option [value]="p.id">{{ p.displayName }}</option> }
            } @else if (f.type === 'group') {
              @for (g of directory.groups(); track g.id) { <option [value]="g.id">{{ g.name }}</option> }
            } @else {
              @for (o of options(f); track o.id) { <option [value]="o.id">{{ o.label }}</option> }
            }
          </select>
        }
        @if (hasFilters()) { <button class="btn btn-ghost btn-sm" (click)="clearFilters()">Clear</button> }
        <span class="flex"></span>
        <span class="muted count">{{ page()?.total ?? 0 }}{{ page()?.totalIsCapped ? '+' : '' }} {{ (page()?.total ?? 0) === 1 ? s?.entity?.entity?.label?.toLowerCase() : s?.entity?.entity?.pluralLabel?.toLowerCase() }}</span>
      </div>

      @if (error()) {
        <div class="callout tone-red" role="alert">{{ error() }}</div>
      }

      <div class="table-wrap">
        <table [class.compact]="table()?.props?.['density'] !== 'comfortable'" aria-rowcount="-1">
          <thead>
            <tr>
              @for (c of columns(); track c.id) {
                <th scope="col" [class]="'w-' + (c.props['width'] ?? 'md')" [attr.aria-sort]="sort().field === c.props['field'] ? (sort().direction === 'asc' ? 'ascending' : 'descending') : null">
                  <button class="sort" (click)="toggleSort(c.props['field'])">{{ c.props['label'] ?? fieldSpec(c.props['field'])?.label ?? c.props['field'] }}
                    @if (sort().field === c.props['field']) { <span aria-hidden="true">{{ sort().direction === 'asc' ? '↑' : '↓' }}</span> }
                  </button>
                </th>
              }
            </tr>
          </thead>
          <tbody>
            @if (loading() && !page()) {
              @for (i of [1, 2, 3, 4, 5, 6]; track i) { <tr>@for (c of columns(); track c.id) { <td><span class="skeleton"></span></td> }</tr> }
            }
            @for (r of page()?.items ?? []; track r.id; let i = $index) {
              <tr #row [class.cursor]="cursor() === i" [attr.data-record]="r.id" (click)="open(r, $event)" tabindex="-1">
                @for (c of columns(); track c.id; let first = $first) {
                  @let key = c.props['field'];
                  <td>
                    @if (first) {
                      <a [routerLink]="['/records', r.entity, r.id]" (click)="$event.stopPropagation()">{{ cell(r, key) }}</a>
                    } @else if (isChoice(key) && canEdit(r, key)) {
                      <select class="inline" [attr.aria-label]="(fieldSpec(key)?.label ?? key) + ' for ' + (r.number ?? r.id)" [ngModel]="r.data[key] ?? ''" (ngModelChange)="inlineSave(r, key, $event)" (click)="$event.stopPropagation()">
                        @for (o of options(fieldSpec(key)!); track o.id) { <option [value]="o.id">{{ o.label }}</option> }
                      </select>
                    } @else if (isChoice(key) && r.data[key]) {
                      <span class="badge" [class]="tone(key, r.data[key])">{{ cell(r, key) }}</span>
                    } @else {
                      <span [class.muted]="cell(r, key) === '—'">{{ cell(r, key) }}</span>
                    }
                  </td>
                }
              </tr>
            } @empty {
              @if (!loading()) {
                <tr><td [attr.colspan]="columns().length" class="empty">
                  <strong>Nothing here</strong>
                  <p class="muted">{{ hasFilters() ? 'No records match these filters.' : 'No records yet.' }}</p>
                  @if (hasFilters()) { <button class="btn btn-sm" (click)="clearFilters()">Clear filters</button> }
                </td></tr>
              }
            }
          </tbody>
        </table>
      </div>

      @if ((page()?.total ?? 0) > pageSize()) {
        <div class="pager">
          <button class="btn btn-sm" [disabled]="offset() === 0" (click)="go(-1)">Previous</button>
          <span class="muted">{{ offset() + 1 }}–{{ offset() + (page()?.items?.length ?? 0) }} of {{ page()?.total }}</span>
          <button class="btn btn-sm" [disabled]="offset() + pageSize() >= (page()?.total ?? 0)" (click)="go(1)">Next</button>
        </div>
      }
    </div>
  `,
  styles: `
    .page { padding: var(--esm-space-5) var(--esm-space-6); max-width: 1400px; }
    .top { display: flex; align-items: center; gap: var(--esm-space-3); margin-bottom: var(--esm-space-3); }
    h1 { margin: 0; font: var(--esm-text-title-lg); }
    .flex { flex: 1; }
    .views { display: flex; gap: var(--esm-space-1); border-bottom: 1px solid var(--esm-border-subtle); margin-bottom: var(--esm-space-3); overflow-x: auto; }
    .view { padding: var(--esm-space-2) var(--esm-space-3); border: 0; border-bottom: 2px solid transparent; background: transparent; cursor: pointer; font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); white-space: nowrap; }
    .view.active { color: var(--esm-text-default); border-bottom-color: var(--esm-accent-brand); }
    .filters { display: flex; flex-wrap: wrap; gap: var(--esm-space-2); align-items: center; margin-bottom: var(--esm-space-3); }
    .filters .input { height: 30px; width: auto; }
    .search { min-width: 220px; }
    .count { font: var(--esm-text-body-sm); }
    .table-wrap { overflow-x: auto; border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); }
    table { width: 100%; border-collapse: collapse; }
    th { text-align: left; font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); border-bottom: 1px solid var(--esm-border-subtle); background: var(--esm-surface-sunken); position: sticky; top: 0; }
    .sort { border: 0; background: transparent; padding: var(--esm-space-2) var(--esm-space-3); width: 100%; text-align: left; cursor: pointer; color: inherit; font: inherit; }
    td { padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); font: var(--esm-text-body-sm); max-width: 420px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    table.compact td { padding-top: 6px; padding-bottom: 6px; }
    tbody tr { cursor: pointer; }
    tbody tr:hover { background: var(--esm-state-hover); }
    tr.cursor { background: var(--esm-state-selected); box-shadow: inset 2px 0 0 var(--esm-accent-brand); }
    .w-sm { width: 100px; } .w-lg { width: 40%; }
    .inline { border: 1px solid transparent; background: transparent; border-radius: var(--esm-radius-sm); padding: 2px 4px; font: inherit; cursor: pointer; }
    .inline:hover, .inline:focus { border-color: var(--esm-border-input); background: var(--esm-surface-raised); }
    .empty { text-align: center; padding: var(--esm-space-10); white-space: normal; }
    .empty p { margin: var(--esm-space-1) 0 var(--esm-space-3); }
    .skeleton { display: block; height: 12px; width: 70%; }
    .pager { display: flex; align-items: center; justify-content: flex-end; gap: var(--esm-space-3); margin-top: var(--esm-space-3); font: var(--esm-text-body-sm); }
    .callout { padding: var(--esm-space-3); border-radius: var(--esm-radius-md); margin-bottom: var(--esm-space-3); }
    @media (max-width: 700px) { .page { padding: var(--esm-space-4); } }
  `,
})
export class ListPage {
  readonly entity = input.required<string>();
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  private readonly session = inject(Session);
  private readonly builder = inject(BuilderState);
  protected readonly directory = inject(Directory);
  private readonly rows = viewChildren<ElementRef<HTMLElement>>('row');

  protected readonly spec = signal<ViewClientSpec | null>(null);
  protected readonly page = signal<RecordPage | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly cursor = signal(-1);
  protected readonly search = signal('');
  protected readonly filters = signal<Record<string, string>>({});
  protected readonly activeView = signal<SavedView | null>(null);
  protected readonly sortOverride = signal<{ field: string; direction: 'asc' | 'desc' } | null>(null);
  protected readonly offset = signal(0);
  private searchTimer: ReturnType<typeof setTimeout> | null = null;
  private loadSeq = 0;

  protected readonly nav = computed(() => this.session.nav(this.entity()));
  private readonly layout = computed(() => this.spec()?.view.layout ?? null);
  protected readonly table = computed(() => find(this.layout(), 'collection.data_table'));
  protected readonly filterBar = computed(() => find(this.layout(), 'collection.filter_bar'));
  protected readonly savedViews = computed<SavedView[]>(() => find(this.layout(), 'collection.saved_views')?.props['views'] ?? []);
  protected readonly columns = computed(() => (this.table()?.children ?? []).filter((c) => c.type === 'collection.column' && !c.hidden && !this.isHiddenField(c.props['field'])));
  protected readonly filterFields = computed(() => ((this.filterBar()?.props['fields'] as string[]) ?? []).map((k) => this.fieldSpec(k)).filter((f): f is FieldSpec => !!f && !this.isHiddenField(f.key)));
  protected readonly pageSize = computed(() => this.table()?.props['pageSize'] ?? 50);
  protected readonly sort = computed(() => this.sortOverride() ?? this.activeView()?.sort ?? this.table()?.props['sort'] ?? { field: 'updated_at', direction: 'desc' });
  protected readonly hasFilters = computed(() => Object.values(this.filters()).some((v) => !!v) || !!this.search());

  constructor() {
    effect(() => {
      const entity = this.entity();
      this.builder.revision();
      const draft = this.builder.draftMode();
      untracked(() => void this.loadSpec(entity, draft));
    });
    effect(() => {
      this.spec();
      this.activeView();
      this.filters();
      this.sort();
      this.offset();
      this.search();
      untracked(() => void this.loadPage());
    });
    effect(() => {
      if (this.filterFields().some((f) => f.type === 'user' || f.type === 'group')) void this.directory.load();
    });

    const shortcuts = inject(Shortcuts);
    const off = [
      shortcuts.register({ keys: 'j', description: 'Next row', group: 'Lists', run: () => this.move(1) }),
      shortcuts.register({ keys: 'k', description: 'Previous row', group: 'Lists', run: () => this.move(-1) }),
      shortcuts.register({ keys: 'enter', description: 'Open the selected row', group: 'Lists', run: () => this.openCursor() }),
      shortcuts.register({ keys: 'c', description: 'Create a record', group: 'Lists', run: () => this.nav()?.canCreate && this.router.navigate(['/records', this.entity(), 'new']) }),
      shortcuts.register({ keys: '/', description: 'Focus search', group: 'Lists', run: () => document.querySelector<HTMLInputElement>('input[type=search]')?.focus() }),
    ];
    inject(DestroyRef).onDestroy(() => off.forEach((f) => f()));
  }

  private async loadSpec(entity: string, draft: boolean): Promise<void> {
    const viewId = this.session.nav(entity)?.listView;
    if (!viewId) {
      this.error.set('This list has no view configured.');
      this.loading.set(false);
      return;
    }
    try {
      const spec = draft ? (await this.api.get<DraftViewSpec>(`/api/drafts/current/views/${viewId}`)).spec : await this.session.view(viewId);
      this.spec.set(spec);
      if (!this.activeView() && this.savedViews().length) this.activeView.set(this.savedViews()[0]);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load this list.');
      this.loading.set(false);
    }
  }

  private async loadPage(): Promise<void> {
    const spec = this.spec();
    if (!spec) return;
    const seq = ++this.loadSeq;
    this.loading.set(true);
    const filter: string[] = [];
    if (this.activeView()?.filter) filter.push(this.activeView()!.filter!);
    for (const [key, value] of Object.entries(this.filters())) {
      if (!value) continue;
      filter.push(value === '@me' ? `record.${key} == user.id` : `record.${key} == ${quote(value)}`);
    }
    try {
      const page = await this.api.get<RecordPage>(`/api/records/${this.entity()}`, {
        filter,
        search: this.search() || undefined,
        sort: this.sort().field,
        dir: this.sort().direction,
        offset: this.offset(),
        size: this.pageSize(),
      });
      if (seq !== this.loadSeq) return;
      this.page.set(page);
      this.error.set(null);
      if (this.cursor() >= page.items.length) this.cursor.set(page.items.length - 1);
    } catch (e) {
      if (seq === this.loadSeq) this.error.set(e instanceof ApiError ? e.message : 'Could not load records.');
    } finally {
      if (seq === this.loadSeq) this.loading.set(false);
    }
  }

  protected selectView(v: SavedView): void {
    this.activeView.set(v);
    this.sortOverride.set(null);
    this.offset.set(0);
  }

  protected setFilter(key: string, value: string): void {
    this.filters.update((f) => ({ ...f, [key]: value }));
    this.offset.set(0);
  }

  protected clearFilters(): void {
    this.filters.set({});
    this.search.set('');
    this.offset.set(0);
  }

  protected onSearch(value: string): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.search.set(value.trim());
      this.offset.set(0);
    }, 250);
  }

  protected toggleSort(field: string): void {
    const s = this.sort();
    this.sortOverride.set({ field, direction: s.field === field && s.direction === 'asc' ? 'desc' : 'asc' });
  }

  protected go(delta: number): void {
    this.offset.update((o) => Math.max(0, o + delta * this.pageSize()));
    this.cursor.set(-1);
  }

  protected fieldSpec(key: string): FieldSpec | undefined {
    return this.spec()?.entity.entity.fields.find((f) => f.key === key);
  }

  private isHiddenField(key: string): boolean {
    const f = this.fieldSpec(key);
    return !f || !!f.hidden || this.spec()?.entity.fieldAccess[key] === 'hidden';
  }

  protected isChoice(key: string): boolean {
    return !!this.fieldSpec(key)?.choiceList;
  }

  protected options(f: FieldSpec) {
    return this.spec()?.entity.choiceLists.find((c) => c.id === f.choiceList)?.options.filter((o) => !o.hidden) ?? [];
  }

  protected tone(key: string, value: unknown): string {
    const t = this.spec()?.entity.choiceLists.find((c) => c.id === this.fieldSpec(key)?.choiceList)?.options.find((o) => o.id === value)?.tone;
    return t ? 'tone-' + t.replace('accent.', '') : 'tone-gray';
  }

  protected canEdit(r: RecordView, key: string): boolean {
    if (this.spec()?.entity.workflow?.stateField === key) return false;
    return !!r.access?.canUpdate && r.access.fields[key] === 'write';
  }

  protected cell(r: RecordView, key: string): string {
    if (key === 'number') return r.number ?? '—';
    if (key === 'updated_at') return relative(r.updatedAt);
    if (key === 'created_at') return relative(r.createdAt);
    const f = this.fieldSpec(key);
    const v = r.data[key];
    if (v === undefined || v === null || v === '') return '—';
    if (f?.choiceList) return this.options(f).find((o) => o.id === v)?.label ?? String(v);
    if (r.display[key]) return r.display[key];
    if (f?.type === 'toggle') return v ? 'Yes' : 'No';
    if (f?.type === 'date_time') return relative(v);
    return Array.isArray(v) ? v.join(', ') : String(v);
  }

  protected async inlineSave(r: RecordView, key: string, value: string): Promise<void> {
    const page = this.page();
    if (!page) return;
    const before = r;
    const optimistic: RecordView = { ...r, data: { ...r.data, [key]: value } };
    this.page.set({ ...page, items: page.items.map((x) => (x.id === r.id ? optimistic : x)) });
    try {
      const saved = await this.api.patch<RecordView>(`/api/records/${r.entity}/${r.id}`, { revision: r.revision, changes: { [key]: value }, configRevision: this.spec()?.configRevision });
      this.page.update((p) => (p ? { ...p, items: p.items.map((x) => (x.id === r.id ? saved : x)) } : p));
      this.toasts.show(`${this.fieldSpec(key)?.label ?? key} updated on ${r.number ?? 'the record'}.`, 'success', {
        label: 'Undo',
        run: () => void this.revert(saved, key, before.data[key]),
      });
    } catch (e) {
      this.page.update((p) => (p ? { ...p, items: p.items.map((x) => (x.id === r.id ? before : x)) } : p));
      this.toasts.show(e instanceof ApiError ? e.message : 'The change was not saved.', 'warning');
    }
  }

  private async revert(saved: RecordView, key: string, value: unknown): Promise<void> {
    try {
      const reverted = await this.api.patch<RecordView>(`/api/records/${saved.entity}/${saved.id}`, { revision: saved.revision, changes: { [key]: value ?? null } });
      this.page.update((p) => (p ? { ...p, items: p.items.map((x) => (x.id === saved.id ? reverted : x)) } : p));
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not undo.', 'warning');
    }
  }

  protected open(r: RecordView, e: MouseEvent): void {
    if ((e.target as HTMLElement).closest('a,select,button')) return;
    void this.router.navigate(['/records', r.entity, r.id]);
  }

  private move(delta: number): void {
    const n = this.page()?.items.length ?? 0;
    if (!n) return;
    const next = Math.min(n - 1, Math.max(0, this.cursor() + delta));
    this.cursor.set(next);
    this.rows()[next]?.nativeElement.scrollIntoView({ block: 'nearest' });
  }

  private openCursor(): void {
    const r = this.page()?.items[this.cursor()];
    if (r) void this.router.navigate(['/records', r.entity, r.id]);
  }
}

function find(node: NodeSpec | null, type: string): NodeSpec | null {
  if (!node) return null;
  if (node.type === type) return node;
  for (const c of node.children) {
    const f = find(c, type);
    if (f) return f;
  }
  return null;
}

function quote(value: string): string {
  return "'" + value.replace(/\\/g, '\\\\').replace(/'/g, "\\'") + "'";
}

export function relative(iso: string): string {
  const d = new Date(iso);
  const diff = (Date.now() - d.getTime()) / 1000;
  if (Number.isNaN(diff)) return iso;
  if (diff < 60) return 'just now';
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`;
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`;
  if (diff < 86400 * 7) return `${Math.floor(diff / 86400)}d ago`;
  return d.toLocaleDateString(undefined, { dateStyle: 'medium' });
}
