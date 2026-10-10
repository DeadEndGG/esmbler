import { Component, DestroyRef, computed, inject, input, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Directory } from '../core/directory';
import { CatalogItem, CatalogStep, CatalogView, ChoiceList, RecordView, VariableSpec } from '../core/models';
import { Shortcuts } from '../core/shortcuts';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

/**
 * Request form for one catalog item (REQ-BIZ-004, ADR 0015). Questions render by type; the server normalizes and
 * validates every answer again, and its field errors (variables.&lt;key&gt;) appear under the matching question.
 */
@Component({
  selector: 'esm-request-form-page',
  imports: [RouterLink, Icon],
  template: `
    <div class="mx-auto max-w-[1080px] px-8 pt-5 pb-12 max-[700px]:px-4">
      <nav class="crumbs" aria-label="Breadcrumb">
        <a class="crumb-link" routerLink="/catalog"><esm-icon name="arrow-left" [size]="14" /> Service catalog</a>
        <esm-icon name="chevron-right" [size]="14" />
        <span class="text-fg">{{ item()?.label ?? '…' }}</span>
      </nav>

      @if (error(); as err) {
        <div class="callout tone-red" role="alert">{{ err }}</div>
      } @else if (item(); as it) {
        <div class="grid gap-6 lg:grid-cols-[minmax(0,1fr)_320px]">
          <form class="surface p-6" (submit)="$event.preventDefault(); submit()" novalidate aria-labelledby="req-title">
            <header class="mb-6 flex items-start gap-4">
              <span class="item-icon" aria-hidden="true"><esm-icon [name]="it.icon === 'laptop' ? 'laptop' : it.icon === 'shield' ? 'shield' : it.icon === 'users' ? 'users' : 'package'" [size]="22" /></span>
              <div>
                <h1 id="req-title" class="text-title-lg">{{ it.label }}</h1>
                @if (it.description) { <p class="mt-1 text-body-sm text-muted">{{ it.description }}</p> }
              </div>
            </header>

            <div class="flex flex-col gap-5">
              @for (v of it.variables; track v.key) {
                <div class="question" [attr.data-variable]="v.key">
                  <label class="q-label" [attr.for]="'v-' + v.key">{{ v.label }} @if (v.required) { <span class="text-red" aria-label="required">*</span> }</label>
                  @switch (v.type) {
                    @case ('long_text') {
                      <textarea class="input" [id]="'v-' + v.key" rows="4" [attr.maxlength]="v.maxLength ?? null" [attr.aria-invalid]="!!errors()[v.key]" [attr.aria-required]="!!v.required"
                                [value]="answer(v.key) ?? ''" (input)="set(v.key, $any($event.target).value)"></textarea>
                    }
                    @case ('single_select') {
                      <select class="input" [id]="'v-' + v.key" [attr.aria-invalid]="!!errors()[v.key]" [attr.aria-required]="!!v.required" [value]="answer(v.key) ?? ''" (change)="set(v.key, $any($event.target).value || null)">
                        <option value="">Choose…</option>
                        @for (o of options(v); track o.id) { <option [value]="o.id">{{ o.label }}</option> }
                      </select>
                    }
                    @case ('multi_select') {
                      <div class="flex flex-wrap gap-2" role="group" [attr.aria-label]="v.label" [id]="'v-' + v.key">
                        @for (o of options(v); track o.id) {
                          <button type="button" class="opt" [class.on]="isChosen(v.key, o.id)" [attr.aria-pressed]="isChosen(v.key, o.id)" (click)="toggle(v.key, o.id)">
                            @if (isChosen(v.key, o.id)) { <esm-icon name="check" [size]="13" /> }{{ o.label }}
                          </button>
                        }
                      </div>
                    }
                    @case ('toggle') {
                      <label class="flex items-center gap-2 text-body"><input type="checkbox" class="size-4 accent-brand" [id]="'v-' + v.key" [checked]="!!answer(v.key)" (change)="set(v.key, $any($event.target).checked)" /> Yes</label>
                    }
                    @case ('user') {
                      <select class="input" [id]="'v-' + v.key" [attr.aria-invalid]="!!errors()[v.key]" [value]="answer(v.key) ?? ''" (change)="set(v.key, $any($event.target).value || null)">
                        <option value="">Choose a person…</option>
                        @for (p of directory.people(); track p.id) { <option [value]="p.id">{{ p.displayName }}</option> }
                      </select>
                    }
                    @case ('group') {
                      <select class="input" [id]="'v-' + v.key" [attr.aria-invalid]="!!errors()[v.key]" [value]="answer(v.key) ?? ''" (change)="set(v.key, $any($event.target).value || null)">
                        <option value="">Choose a group…</option>
                        @for (g of directory.groups(); track g.id) { <option [value]="g.id">{{ g.name }}</option> }
                      </select>
                    }
                    @default {
                      <input class="input" [id]="'v-' + v.key" [type]="inputType(v)" [attr.maxlength]="v.maxLength ?? null" [attr.aria-invalid]="!!errors()[v.key]" [attr.aria-required]="!!v.required"
                             [attr.aria-describedby]="errors()[v.key] ? 'e-' + v.key : null" [value]="answer(v.key) ?? ''" (input)="set(v.key, $any($event.target).value)" />
                    }
                  }
                  @if (v.help) { <p class="text-body-sm text-muted">{{ v.help }}</p> }
                  @if (errors()[v.key]; as message) { <p class="text-body-sm text-red" [id]="'e-' + v.key" role="alert">{{ message }}</p> }
                </div>
              }
              <div class="question">
                <label class="q-label" for="v-notes">Anything else we should know?</label>
                <textarea class="input" id="v-notes" rows="3" maxlength="4000" [value]="notes()" (input)="notes.set($any($event.target).value)"></textarea>
              </div>
            </div>

            <div class="mt-6 flex items-center justify-end gap-2 border-t border-subtle pt-5">
              <a class="btn btn-ghost" routerLink="/catalog">Cancel</a>
              <button class="btn btn-primary h-9" type="submit" [disabled]="saving()">{{ saving() ? 'Submitting…' : 'Submit request' }} <kbd>⌘↵</kbd></button>
            </div>
          </form>

          <aside class="flex flex-col gap-4" aria-label="What happens next">
            <section class="surface p-5">
              <h2 class="flex items-center gap-2 text-title-sm"><esm-icon name="list-checks" [size]="16" class="text-brand" /> What happens next</h2>
              <ol class="steps mt-5">
                <li class="step"><span class="dot done" aria-hidden="true"><esm-icon name="send" [size]="11" /></span><div><strong class="text-body-sm">You submit the request</strong><p class="text-body-sm text-muted">The service desk reviews it.</p></div></li>
                @for (s of ordered(); track s.id) {
                  <li class="step">
                    <span class="dot" aria-hidden="true"></span>
                    <div>
                      <strong class="text-body-sm">{{ s.title }}</strong>
                      <p class="text-body-sm text-muted">
                        @if (s.dependsOn.length) { Waits for {{ titles(s.dependsOn) }}. } @else { Starts right away. }
                        @if (s.conditional) { Only if it applies. }
                      </p>
                    </div>
                  </li>
                }
                <li class="step"><span class="dot" aria-hidden="true"><esm-icon name="check" [size]="11" /></span><div><strong class="text-body-sm">Fulfilled</strong><p class="text-body-sm text-muted">We let you know when everything is done.</p></div></li>
              </ol>
            </section>
          </aside>
        </div>
      } @else {
        <div class="surface flex flex-col gap-3 p-6" aria-busy="true"><span class="skeleton block h-8 w-1/2"></span><span class="skeleton block h-4"></span><span class="skeleton block h-4 w-2/3"></span></div>
      }
    </div>
  `,
  styles: `
    .crumbs { display: flex; align-items: center; gap: var(--esm-space-2); min-height: 28px; margin-bottom: var(--esm-space-4); font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    .crumb-link { display: inline-flex; align-items: center; gap: var(--esm-space-1); color: var(--esm-text-muted); font-weight: 500; }
    .crumb-link:hover { color: var(--esm-text-default); text-decoration: none; }
    .item-icon { display: grid; place-items: center; flex: none; width: 44px; height: 44px; border-radius: var(--esm-radius-md); background: var(--esm-state-selected); color: var(--esm-accent-brand); }
    .question { display: flex; flex-direction: column; gap: 6px; }
    .q-label { font: var(--esm-text-body-sm); font-weight: 600; color: var(--esm-text-default); }
    .opt { display: inline-flex; align-items: center; gap: 6px; height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong);
      background: var(--esm-surface-raised); font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-default); }
    .opt.on { border-color: var(--esm-accent-brand); background: var(--esm-state-selected); color: var(--esm-accent-brand); }
    .steps { position: relative; list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--esm-space-4); }
    .steps::before { content: ""; position: absolute; left: 9px; top: 6px; bottom: 6px; width: 1px; background: var(--esm-border-strong); }
    .step { position: relative; display: flex; gap: var(--esm-space-3); }
    .step p { margin: 2px 0 0; }
    .dot { position: relative; display: grid; place-items: center; flex: none; width: 19px; height: 19px; border-radius: 50%; border: 2px solid var(--esm-border-strong); background: var(--esm-surface-raised); color: var(--esm-text-muted); }
    .dot.done { border-color: var(--esm-accent-brand); background: var(--esm-accent-brand); color: var(--esm-text-on-accent); }
  `,
})
export class RequestFormPage {
  readonly item$ = input.required<string>({ alias: 'item' });
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly toasts = inject(Toasts);
  protected readonly directory = inject(Directory);

  protected readonly item = signal<CatalogItem | null>(null);
  private readonly lists = signal<ChoiceList[]>([]);
  protected readonly answers = signal<Record<string, unknown>>({});
  protected readonly notes = signal('');
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly error = signal<string | null>(null);
  protected readonly saving = signal(false);
  private readonly idempotencyKey = crypto.randomUUID();

  /** Steps in an order that respects dependencies. */
  protected readonly ordered = computed<CatalogStep[]>(() => {
    const steps = this.item()?.steps ?? [];
    const done = new Set<string>();
    const out: CatalogStep[] = [];
    while (out.length < steps.length) {
      const next = steps.filter((s) => !done.has(s.id) && s.dependsOn.every((d) => done.has(d) || !steps.some((x) => x.id === d)));
      if (!next.length) {
        out.push(...steps.filter((s) => !done.has(s.id)));
        break;
      }
      next.forEach((s) => {
        done.add(s.id);
        out.push(s);
      });
    }
    return out;
  });

  constructor() {
    void this.load();
    void this.directory.load();
    const off = inject(Shortcuts).register({ keys: 'mod+enter', description: 'Submit the request', group: 'Forms', allowInInput: true, run: () => void this.submit() });
    inject(DestroyRef).onDestroy(off);
  }

  protected answer(key: string): any {
    return this.answers()[key];
  }

  protected set(key: string, value: unknown): void {
    this.answers.update((a) => ({ ...a, [key]: value === '' ? null : value }));
    if (this.errors()[key]) this.errors.update((e) => ({ ...e, [key]: '' }));
  }

  protected isChosen(key: string, id: string): boolean {
    return ((this.answers()[key] as string[] | undefined) ?? []).includes(id);
  }

  protected toggle(key: string, id: string): void {
    const current = (this.answers()[key] as string[] | undefined) ?? [];
    this.set(key, current.includes(id) ? current.filter((x) => x !== id) : [...current, id]);
  }

  protected options(v: VariableSpec) {
    return this.lists().find((l) => l.id === v.choiceList)?.options.filter((o) => !o.hidden) ?? [];
  }

  protected inputType(v: VariableSpec): string {
    return { integer: 'number', decimal: 'number', date: 'date', date_time: 'datetime-local', email: 'email' }[v.type] ?? 'text';
  }

  protected titles(ids: string[]): string {
    const steps = this.item()?.steps ?? [];
    const names = ids.map((id) => `“${steps.find((s) => s.id === id)?.title ?? id}”`);
    return names.length > 1 ? `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}` : names[0];
  }

  private async load(): Promise<void> {
    try {
      const view = await this.api.get<CatalogView>('/api/catalog');
      const item = view.items.find((i) => i.id === this.item$());
      if (!item) {
        this.error.set('This item is not in the catalog any more, or you cannot request it.');
        return;
      }
      this.lists.set(view.choiceLists);
      this.answers.set(Object.fromEntries(item.variables.filter((v) => v.default !== undefined && v.default !== null).map((v) => [v.key, v.default])));
      this.item.set(item);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'The request form could not be loaded.');
    }
  }

  protected async submit(): Promise<void> {
    const item = this.item();
    if (!item || this.saving()) return;
    const missing = item.variables.filter((v) => v.required && v.type !== 'toggle' && isEmpty(this.answers()[v.key]));
    if (missing.length) {
      this.errors.set(Object.fromEntries(missing.map((v) => [v.key, `${v.label} is required.`])));
      document.getElementById('v-' + missing[0].key)?.focus();
      return;
    }
    this.saving.set(true);
    try {
      const variables = Object.fromEntries(
        Object.entries(this.answers())
          .filter(([, value]) => !isEmpty(value))
          .map(([key, value]) => [key, toWire(item.variables.find((v) => v.key === key)?.type, value)]),
      );
      const data: Record<string, unknown> = { catalog_item: item.id, variables };
      if (this.notes().trim()) data['description'] = this.notes().trim();
      const created = await this.api.post<RecordView>(`/api/records/${item.requestEntity}`, { data }, { 'Idempotency-Key': this.idempotencyKey });
      this.toasts.show(`${created.number ?? 'Request'} submitted. We'll keep you posted.`, 'success');
      await this.router.navigate(['/records', created.entity, created.id]);
    } catch (e) {
      if (e instanceof ApiError && e.problem.errors?.length) {
        const fieldErrors = Object.fromEntries(e.problem.errors.filter((x) => x.field.startsWith('variables.')).map((x) => [x.field.slice('variables.'.length), x.message]));
        this.errors.set(fieldErrors);
        const other = e.problem.errors.find((x) => !x.field.startsWith('variables.'));
        if (other) this.toasts.show(other.message, 'warning');
        const first = Object.keys(fieldErrors)[0];
        if (first) document.getElementById('v-' + first)?.focus();
      } else {
        this.toasts.show(e instanceof ApiError ? e.message : 'The request was not submitted.', 'warning');
      }
    } finally {
      this.saving.set(false);
    }
  }
}

/** Inputs give text; the API takes numbers as numbers and instants in UTC. */
function toWire(type: string | undefined, value: unknown): unknown {
  if ((type === 'integer' || type === 'decimal') && typeof value === 'string' && value.trim() !== '' && !Number.isNaN(Number(value))) return Number(value);
  if (type === 'date_time' && typeof value === 'string' && !value.endsWith('Z')) {
    const d = new Date(value);
    return Number.isNaN(d.getTime()) ? value : d.toISOString();
  }
  return value;
}

function isEmpty(v: unknown): boolean {
  return v === undefined || v === null || v === '' || (Array.isArray(v) && v.length === 0);
}
