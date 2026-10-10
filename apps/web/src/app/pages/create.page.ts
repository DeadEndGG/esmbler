import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { BuilderState } from '../builder/builder-state';
import { Api, ApiError } from '../core/api';
import { DraftViewSpec, RecordView } from '../core/models';
import { Session } from '../core/session';
import { Shortcuts } from '../core/shortcuts';
import { Toasts } from '../core/toast';
import { NodeComponent } from '../renderer/node';
import { RenderContext } from '../renderer/render-context';
import { Icon } from '../ui/icon';

/** spec_object.form for creating a record. Rules run as you type; the server re-validates on submit. */
@Component({
  selector: 'esm-create-page',
  imports: [NodeComponent, RouterLink, Icon],
  providers: [RenderContext],
  template: `
    @let s = ctx.spec();
    <div class="page">
      <nav class="crumbs" aria-label="Breadcrumb">
        <a class="crumb-link" [routerLink]="['/records', entity()]"><esm-icon name="arrow-left" [size]="14" /> {{ s?.entity?.entity?.pluralLabel ?? 'Records' }}</a>
        <esm-icon name="chevron-right" [size]="14" />
        <span class="text-fg">New</span>
      </nav>
      <header class="mb-5">
        <h1 class="text-title-lg">New {{ s?.entity?.entity?.label?.toLowerCase() ?? 'record' }}</h1>
        <p class="mt-0.5 text-body-sm text-muted">Fields marked * are required. Rules run as you type.</p>
      </header>
      @if (error()) { <div class="callout tone-red" role="alert">{{ error() }}</div> }
      @if (s && ctx.record()) {
        <form (submit)="$event.preventDefault(); submit()" novalidate>
          @for (m of ctx.states().messages; track $index) { <div class="callout tone-blue" role="note">{{ m.text }}</div> }
          <esm-node [node]="s.view.layout" [viewId]="s.view.id" />
          <div class="actions">
            <a class="btn btn-ghost" [routerLink]="['/records', entity()]">Cancel</a>
            <button class="btn btn-primary" type="submit" [disabled]="saving()">{{ saving() ? 'Creating…' : 'Create' }} <kbd>⌘↵</kbd></button>
          </div>
        </form>
      } @else if (!error()) {
        <div class="skeleton-page" aria-busy="true"><span class="skeleton"></span><span class="skeleton"></span><span class="skeleton"></span></div>
      }
    </div>
  `,
  styles: `
    .page { padding: var(--esm-space-5) var(--esm-space-8) var(--esm-space-12); max-width: 920px; margin: 0 auto; }
    .crumbs { display: flex; align-items: center; gap: var(--esm-space-2); min-height: 28px; font: var(--esm-text-body-sm); color: var(--esm-text-muted); margin-bottom: var(--esm-space-4); }
    .crumb-link { display: inline-flex; align-items: center; gap: var(--esm-space-1); color: var(--esm-text-muted); font-weight: 500; }
    .crumb-link:hover { color: var(--esm-text-default); text-decoration: none; }
    .callout { margin-bottom: var(--esm-space-3); }
    .actions { position: sticky; bottom: 0; display: flex; justify-content: flex-end; gap: var(--esm-space-2); margin-top: var(--esm-space-4); padding: var(--esm-space-3) 0;
      background: linear-gradient(transparent, var(--esm-surface-base) 35%); }
    @media (max-width: 700px) { .page { padding: var(--esm-space-3) var(--esm-space-4); } }
    .skeleton-page { display: flex; flex-direction: column; gap: var(--esm-space-3); }
    .skeleton-page .skeleton { display: block; height: 32px; }
  `,
})
export class CreatePage {
  readonly entity = input.required<string>();
  protected readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly builder = inject(BuilderState);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private readonly idempotencyKey = crypto.randomUUID();

  constructor() {
    this.ctx.mode.set('form');
    this.ctx.commitField = async (key, value) => this.ctx.setValue(key, value);
    effect(() => {
      const entity = this.entity();
      const draft = this.builder.draftMode();
      this.builder.revision();
      untracked(() => void this.load(entity, draft));
    });
    const shortcuts = inject(Shortcuts);
    const off = shortcuts.register({ keys: 'mod+enter', description: 'Submit the form', group: 'Forms', allowInInput: true, run: () => void this.submit() });
    inject(DestroyRef).onDestroy(off);
  }

  private async load(entity: string, draft: boolean): Promise<void> {
    const nav = this.session.nav(entity);
    const viewId = nav?.quickCreateView;
    if (!nav?.canCreate || !viewId) {
      this.error.set("You can't create this kind of record here.");
      return;
    }
    try {
      const spec = draft ? (await this.api.get<DraftViewSpec>(`/api/drafts/current/views/${viewId}`)).spec : await this.session.view(viewId);
      this.ctx.spec.set(spec);
      const defaults: Record<string, unknown> = {};
      for (const f of spec.entity.entity.fields) if (f.default !== undefined && f.default !== null) defaults[f.key] = f.default;
      const now = new Date().toISOString();
      const blank: RecordView = { id: '', entity, revision: 0, configRevision: spec.configRevision, data: defaults, display: {}, createdAt: now, updatedAt: now };
      this.ctx.record.set(blank);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load the form.');
    }
  }

  protected async submit(): Promise<void> {
    if (this.saving() || !this.ctx.spec()) return;
    const missing = this.ctx
      .entity()!
      .entity.fields.filter((f) => this.ctx.isRequired(f.key) && !this.ctx.isHidden(f.key) && isEmpty(this.ctx.values()[f.key]))
      .map((f) => f.key);
    if (missing.length) {
      this.ctx.errors.set(Object.fromEntries(missing.map((k) => [k, `${this.ctx.field(k)?.label ?? k} is required.`])));
      document.querySelector<HTMLElement>(`[data-field="${missing[0]}"] input, [data-field="${missing[0]}"] select, [data-field="${missing[0]}"] textarea`)?.focus();
      return;
    }
    this.saving.set(true);
    try {
      const created = await this.api.post<RecordView>(`/api/records/${this.entity()}`, { data: this.ctx.values(), configRevision: this.ctx.spec()!.configRevision }, { 'Idempotency-Key': this.idempotencyKey });
      this.toasts.show(`${created.number ?? 'Record'} created.`, 'success');
      await this.router.navigate(['/records', created.entity, created.id]);
    } catch (e) {
      if (e instanceof ApiError && e.problem.errors?.length) {
        this.ctx.errors.set(Object.fromEntries(e.problem.errors.map((x) => [x.field, x.message])));
      } else {
        this.toasts.show(e instanceof ApiError ? e.message : 'The record was not created.', 'warning');
      }
    } finally {
      this.saving.set(false);
    }
  }
}

function isEmpty(v: unknown): boolean {
  return v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0);
}
