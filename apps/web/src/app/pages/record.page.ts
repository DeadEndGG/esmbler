import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { BuilderState } from '../builder/builder-state';
import { DraftCommands, findNode } from '../builder/draft-commands';
import { Api, ApiError } from '../core/api';
import { ExplorerState } from '../core/explorer-state';
import { DraftViewSpec, NodeSpec, RecordView } from '../core/models';
import { Session } from '../core/session';
import { Shortcuts } from '../core/shortcuts';
import { Toasts } from '../core/toast';
import { NodeComponent } from '../renderer/node';
import { RenderContext } from '../renderer/render-context';
import { Icon } from '../ui/icon';

interface SlashItem {
  label: string;
  hint: string;
  node: () => Omit<NodeSpec, 'children'>;
}

/**
 * spec_object.record_view rendered by the element renderer. View mode edits inline with optimistic saves bound to
 * the record revision. With the AI builder open the page switches to the draft spec live: changed blocks are
 * marked, blocks can be selected, dragged and inserted ("/"), and field values can be tried without saving.
 */
@Component({
  selector: 'esm-record-page',
  imports: [NodeComponent, RouterLink, FormsModule, Icon],
  providers: [RenderContext],
  template: `
    @let s = ctx.spec();
    <div class="page" [class.draft]="ctx.mode() === 'draft'">
      @if (ctx.mode() === 'draft') {
        <div class="band" role="status">
          <span class="band-mark" aria-hidden="true"><esm-icon name="sparkles" [size]="13" /></span>
          <strong class="whitespace-nowrap">Draft preview</strong>
          <span class="muted min-w-0 truncate">Only you see this. {{ changedCount() }} element{{ changedCount() === 1 ? '' : 's' }} changed. Values you type here are not saved.</span>
          <span class="flex"></span>
          @if (tried()) { <button class="btn btn-ghost btn-sm" (click)="resetTry()"><esm-icon name="reset" [size]="14" /> Reset values</button> }
          <button class="btn btn-sm" (click)="openSlash()"><esm-icon name="plus" [size]="14" /> Insert <kbd>/</kbd></button>
        </div>
      }
      <nav class="crumbs" aria-label="Breadcrumb">
        <a class="crumb-link" [routerLink]="returnTree()"><esm-icon name="arrow-left" [size]="14" /> {{ s?.entity?.entity?.pluralLabel ?? 'Records' }}</a>
        <esm-icon name="chevron-right" [size]="14" />
        <span class="mono text-fg">{{ preview() ? 'Sample record' : (ctx.record()?.number ?? '…') }}</span>
        <span class="flex"></span>
        @if (ctx.mode() === 'view' && ctx.record()?.access?.canDelete) {
          <button class="btn btn-sm btn-danger" (click)="remove()"><esm-icon name="trash" [size]="14" /> Delete</button>
        }
      </nav>

      @if (error(); as err) {
        <div class="callout tone-red" role="alert">
          <strong>{{ err }}</strong>
          @if (notFound()) { <p><a [routerLink]="returnTree()">Back to the list</a></p> }
        </div>
      } @else if (s && ctx.record()) {
        @for (m of ctx.states().messages; track $index) {
          <div class="callout" [class]="'tone-' + toneOf(m.tone)" role="note">{{ m.text }}</div>
        }
        <esm-node [node]="s.view.layout" [viewId]="s.view.id" />
      } @else {
        <div class="skeleton-page" aria-busy="true">
          <span class="skeleton h"></span>
          <span class="skeleton"></span>
          <span class="skeleton"></span>
          <span class="skeleton short"></span>
        </div>
      }
    </div>

    @if (slashOpen()) {
      <div class="scrim" (click)="slashOpen.set(false)"></div>
      <div class="slash" role="dialog" aria-label="Insert an element" (keydown)="slashKey($event)">
        <input class="input" placeholder="Insert into {{ slashTargetLabel() }}…" [ngModel]="slashQuery()" (ngModelChange)="slashQuery.set($event)" aria-label="Search elements" #q autofocus />
        <ul role="listbox">
          @for (item of slashItems(); track item.label; let i = $index) {
            <li role="option" [attr.aria-selected]="i === slashIndex()" [class.active]="i === slashIndex()" (click)="insert(item)" (mouseenter)="slashIndex.set(i)">
              <span>{{ item.label }}</span><span class="muted">{{ item.hint }}</span>
            </li>
          } @empty {
            <li class="muted">No matching elements</li>
          }
        </ul>
      </div>
    }
  `,
  styles: `
    .page { padding: var(--esm-space-5) var(--esm-space-8) var(--esm-space-12); max-width: 1320px; margin: 0 auto; }
    .page.draft { background-image: radial-gradient(var(--esm-border-strong) 1px, transparent 1px); background-size: 18px 18px; min-height: 100%; max-width: none; }
    .band { position: sticky; top: 0; z-index: 20; display: flex; align-items: center; gap: var(--esm-space-2); margin: calc(-1 * var(--esm-space-5)) calc(-1 * var(--esm-space-8)) var(--esm-space-4);
      padding: var(--esm-space-2) var(--esm-space-8); font: var(--esm-text-body-sm);
      background: color-mix(in srgb, var(--esm-accent-brand) 7%, var(--esm-surface-raised)); border-bottom: 1px solid color-mix(in srgb, var(--esm-accent-brand) 30%, transparent);
      backdrop-filter: blur(8px); }
    .band-mark { display: grid; place-items: center; width: 22px; height: 22px; border-radius: var(--esm-radius-sm); background: var(--esm-gradient-brand); color: var(--esm-text-on-gradient); }
    .flex { flex: 1; }
    .crumbs { display: flex; align-items: center; gap: var(--esm-space-2); min-height: 28px; font: var(--esm-text-body-sm); color: var(--esm-text-muted); margin-bottom: var(--esm-space-4); }
    .crumb-link { display: inline-flex; align-items: center; gap: var(--esm-space-1); color: var(--esm-text-muted); font-weight: 500; }
    .crumb-link:hover { color: var(--esm-text-default); text-decoration: none; }
    .callout { margin-bottom: var(--esm-space-3); }
    .callout p { margin: var(--esm-space-2) 0 0; }
    .skeleton-page { display: flex; flex-direction: column; gap: var(--esm-space-3); max-width: 720px; }
    .skeleton-page .skeleton { display: block; height: 14px; }
    .skeleton-page .h { height: 32px; width: 50%; }
    .skeleton-page .short { width: 30%; }
    .scrim { position: fixed; inset: 0; z-index: 900; background: var(--esm-scrim); backdrop-filter: blur(2px); }
    .slash { position: fixed; z-index: 901; top: 18vh; left: 50%; transform: translateX(-50%); width: min(440px, calc(100vw - 32px)); padding: var(--esm-space-2); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-overlay); border: 1px solid var(--esm-border-subtle); }
    .slash .input { width: 100%; }
    .slash ul { list-style: none; margin: var(--esm-space-2) 0 0; padding: 0; max-height: 320px; overflow: auto; }
    .slash li { display: flex; justify-content: space-between; gap: var(--esm-space-3); padding: var(--esm-space-2) var(--esm-space-3); border-radius: var(--esm-radius-sm); cursor: pointer; font: var(--esm-text-body-sm); }
    .slash li.active { background: var(--esm-state-selected); }
    @media (max-width: 700px) { .page { padding: var(--esm-space-3) var(--esm-space-4); } .band { margin: calc(-1 * var(--esm-space-3)) calc(-1 * var(--esm-space-4)) var(--esm-space-3); padding: var(--esm-space-2) var(--esm-space-4); } }
  `,
})
export class RecordPage {
  readonly entity = input.required<string>();
  readonly id = input<string>();
  readonly preview = input<boolean>(false);

  protected readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly builder = inject(BuilderState);
  private readonly commands = inject(DraftCommands);
  private readonly toasts = inject(Toasts);
  private readonly router = inject(Router);
  private readonly explorer = inject(ExplorerState);
  /** The explorer this record was opened from, with its filters (REQ-WS-003). */
  protected readonly returnTree = computed(() => this.router.parseUrl(this.explorer.returnUrl(this.entity())));

  protected readonly error = signal<string | null>(null);
  protected readonly notFound = signal(false);
  protected readonly slashOpen = signal(false);
  protected readonly slashIndex = signal(0);
  protected readonly slashQuery = signal('');
  private loadSeq = 0;

  protected readonly changedCount = computed(() => this.ctx.changedTargets().size);
  protected readonly tried = computed(() => Object.keys(this.ctx.draftValues()).length > 0);

  constructor() {
    this.ctx.commitField = (key, value) => this.save({ [key]: value });
    this.ctx.runTransition = (id) => this.transition(id);
    this.ctx.refreshRecord = async () => {
      const id = this.id();
      if (!id || this.preview()) return;
      this.ctx.record.set(await this.api.get<RecordView>(`/api/records/${this.entity()}/${id}`));
    };

    effect(() => {
      const entity = this.entity();
      const id = this.id();
      const draft = this.builder.draftMode() || this.preview();
      this.builder.revision();
      untracked(() => void this.load(entity, id, draft));
    });

    const shortcuts = inject(Shortcuts);
    const off = [
      shortcuts.register({ keys: '/', description: 'Insert an element (draft mode)', group: 'Builder', run: () => this.openSlash() }),
      shortcuts.register({ keys: 'escape', description: 'Clear the selection', group: 'Builder', run: () => this.builder.selection.set([]) }),
      shortcuts.register({ keys: 'backspace', description: 'Remove the selected block (draft mode)', group: 'Builder', run: () => void this.removeSelected() }),
    ];
    inject(DestroyRef).onDestroy(() => off.forEach((f) => f()));
  }

  private async load(entity: string, id: string | undefined, draft: boolean): Promise<void> {
    const seq = ++this.loadSeq;
    const viewId = this.session.nav(entity)?.recordView;
    if (!viewId) {
      this.error.set('This record type has no record page configured.');
      return;
    }
    try {
      const recordPromise: Promise<RecordView> = this.preview() || !id ? this.api.get<RecordView>(`/api/drafts/current/sample/${entity}`) : this.api.get<RecordView>(`/api/records/${entity}/${id}`);
      if (draft) {
        const [dv, record] = await Promise.all([this.api.get<DraftViewSpec>(`/api/drafts/current/views/${viewId}`), recordPromise]);
        if (seq !== this.loadSeq) return;
        this.ctx.mode.set('draft');
        this.ctx.spec.set(dv.spec);
        this.ctx.changedTargets.set(new Set(dv.changedTargets));
        this.ctx.record.set(record);
        this.commands.track(viewId, dv.spec.view.layout);
      } else {
        const [spec, record] = await Promise.all([this.session.view(viewId), recordPromise]);
        if (seq !== this.loadSeq) return;
        this.ctx.mode.set('view');
        this.ctx.draftValues.set({});
        this.ctx.changedTargets.set(new Set());
        this.ctx.spec.set(spec);
        this.ctx.record.set(record);
      }
      this.error.set(null);
      this.notFound.set(false);
    } catch (e) {
      if (seq !== this.loadSeq) return;
      this.notFound.set(e instanceof ApiError && e.status === 404);
      this.error.set(e instanceof ApiError ? e.message : 'Could not load this record.');
    }
  }

  /** Optimistic inline save bound to the revision the user saw; rolls back and explains on failure. */
  private async save(changes: Record<string, unknown>): Promise<void> {
    const record = this.ctx.record();
    if (!record) return;
    if (this.ctx.mode() === 'draft') {
      for (const [k, v] of Object.entries(changes)) this.ctx.setValue(k, v);
      return;
    }
    const keys = Object.keys(changes);
    this.ctx.saving.update((s) => new Set([...s, ...keys]));
    this.ctx.record.set({ ...record, data: { ...record.data, ...changes } });
    try {
      const saved = await this.api.patch<RecordView>(`/api/records/${record.entity}/${record.id}`, { revision: record.revision, changes, configRevision: this.ctx.spec()?.configRevision ?? record.configRevision });
      this.ctx.record.set(saved);
      this.ctx.errors.set({});
    } catch (e) {
      this.ctx.record.set(record);
      this.handleWriteError(e);
    } finally {
      this.ctx.saving.update((s) => new Set([...s].filter((k) => !keys.includes(k))));
    }
  }

  private async transition(id: string): Promise<void> {
    const record = this.ctx.record();
    if (!record || this.ctx.mode() === 'draft') return;
    try {
      const saved = await this.api.post<RecordView>(`/api/records/${record.entity}/${record.id}/transitions/${id}`, { revision: record.revision, changes: this.ctx.draftValues(), configRevision: this.ctx.spec()?.configRevision ?? record.configRevision });
      this.ctx.record.set(saved);
      this.ctx.draftValues.set({});
      this.ctx.errors.set({});
      const label = record.transitions?.find((t) => t.id === id)?.label ?? id;
      this.toasts.show(`${label}: done.`, 'success');
    } catch (e) {
      this.handleWriteError(e);
    }
  }

  private handleWriteError(e: unknown): void {
    if (!(e instanceof ApiError)) {
      this.toasts.show('The change was not saved.', 'warning');
      return;
    }
    if (e.problem.errors?.length) {
      this.ctx.errors.set(Object.fromEntries(e.problem.errors.map((x) => [x.field, x.message])));
      this.toasts.show(e.problem.errors.length === 1 ? e.problem.errors[0].message : `${e.problem.errors.length} fields need attention.`, 'warning');
    } else if (e.code === 'record_conflict' || e.code === 'config_conflict') {
      this.toasts.show('This record changed since you opened it. Showing the latest version.', 'warning');
      this.builder.revision.update((r) => r + 1);
    } else {
      this.toasts.show(e.problem.suggestion ? `${e.message} ${e.problem.suggestion}` : e.message, 'warning');
    }
  }

  protected toneOf(tone: string): string {
    return ({ info: 'blue', success: 'green', warning: 'amber', critical: 'red' } as Record<string, string>)[tone] ?? 'gray';
  }

  protected resetTry(): void {
    this.ctx.draftValues.set({});
    this.ctx.errors.set({});
  }

  // Slash insert: adds an element to the selected card (or the first card) through a typed node.insert command.
  private readonly targetCard = computed<NodeSpec | null>(() => {
    const layout = this.ctx.spec()?.view.layout;
    if (!layout) return null;
    for (const sel of this.builder.selection()) {
      const n = findNode(layout, sel.nodeId);
      if (n && ['layout.card', 'layout.column', 'layout.tab'].includes(n.type)) return n;
    }
    return firstOfType(layout, 'layout.card');
  });

  protected readonly slashTargetLabel = computed(() => this.targetCard()?.props['title'] ?? 'the page');

  protected readonly slashItems = computed<SlashItem[]>(() => {
    const entity = this.ctx.entity();
    const layout = this.ctx.spec()?.view.layout;
    if (!entity || !layout) return [];
    const placed = new Set<string>();
    collectFields(layout, placed);
    const fields: SlashItem[] = entity.entity.fields
      .filter((f) => !placed.has(f.key) && !f.hidden && !f.system && entity.fieldAccess[f.key] !== 'hidden')
      .map((f) => ({ label: f.label, hint: 'Field', node: () => ({ id: newNodeId(), type: 'field', props: { field: f.key } }) }));
    const blocks: SlashItem[] = [
      { label: 'Callout', hint: 'Display', node: () => ({ id: newNodeId(), type: 'display.callout', props: { tone: 'info', text: 'New callout' } }) },
      { label: 'Heading', hint: 'Display', node: () => ({ id: newNodeId(), type: 'display.heading', props: { level: 3, text: 'Heading' } }) },
      { label: 'Workflow buttons', hint: 'Action', node: () => ({ id: newNodeId(), type: 'action.transition_buttons', props: {} }) },
    ];
    const q = this.slashQuery().toLowerCase();
    return [...fields, ...blocks].filter((i) => !q || i.label.toLowerCase().includes(q));
  });

  protected openSlash(): void {
    if (this.ctx.mode() !== 'draft') return;
    this.slashQuery.set('');
    this.slashIndex.set(0);
    this.slashOpen.set(true);
  }

  protected slashKey(e: KeyboardEvent): void {
    const n = this.slashItems().length;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      this.slashIndex.set((this.slashIndex() + 1) % Math.max(n, 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      this.slashIndex.set((this.slashIndex() - 1 + n) % Math.max(n, 1));
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const item = this.slashItems()[this.slashIndex()];
      if (item) void this.insert(item);
    } else if (e.key === 'Escape') {
      this.slashOpen.set(false);
    } else {
      this.slashIndex.set(0);
    }
  }

  protected async insert(item: SlashItem): Promise<void> {
    const target = this.targetCard();
    const viewId = this.ctx.spec()?.view.id;
    this.slashOpen.set(false);
    if (!target || !viewId) {
      this.toasts.show('Select a card to insert into.', 'info');
      return;
    }
    const node = item.node();
    const result = await this.commands.insert(viewId, target.id, node);
    if (result) this.builder.highlight([node.id]);
  }

  private async removeSelected(): Promise<void> {
    if (this.ctx.mode() !== 'draft') return;
    const sel = this.builder.selection();
    if (sel.length !== 1) return;
    const removed = await this.commands.remove(sel[0].view, sel[0].nodeId);
    if (removed) this.builder.selection.set([]);
  }

  protected async remove(): Promise<void> {
    const record = this.ctx.record();
    if (!record || !window.confirm(`Delete ${record.number ?? 'this record'}? You can undo this for 30 days.`)) return;
    try {
      await this.api.delete(`/api/records/${record.entity}/${record.id}`, { revision: record.revision });
      await this.router.navigateByUrl(this.explorer.returnUrl(record.entity));
      this.toasts.show(`${record.number ?? 'Record'} deleted.`, 'info', {
        label: 'Undo',
        run: () =>
          void this.api
            .post(`/api/records/${record.entity}/${record.id}/restore`)
            .then(() => this.router.navigate(['/records', record.entity, record.id]))
            .catch((e) => this.toasts.show(e instanceof ApiError ? e.message : 'Could not restore.', 'warning')),
      }, 8000);
    } catch (e) {
      this.handleWriteError(e);
    }
  }

  protected back(): void {
    void this.router.navigateByUrl(this.explorer.returnUrl(this.entity()));
  }
}

function newNodeId(): string {
  const hex = Array.from(crypto.getRandomValues(new Uint8Array(5)), (b) => b.toString(16).padStart(2, '0')).join('');
  return `tenant__n_${hex}`;
}

function firstOfType(node: NodeSpec, type: string): NodeSpec | null {
  if (node.type === type) return node;
  for (const c of node.children) {
    const f = firstOfType(c, type);
    if (f) return f;
  }
  return null;
}

function collectFields(node: NodeSpec, into: Set<string>): void {
  if (node.type === 'field' && node.props['field']) into.add(node.props['field']);
  node.children.forEach((c) => collectFields(c, into));
}
