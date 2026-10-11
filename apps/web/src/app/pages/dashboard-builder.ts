import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { BuilderState } from '../builder/builder-state';
import { Api, ApiError } from '../core/api';
import { DraftState } from '../core/models';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface MetricOption {
  id: string;
  label: string;
  grouped: boolean;
  source: string;
  added: boolean;
}

interface TileOption {
  id: string;
  type: string;
  metric?: string | null;
  title?: string | null;
}

interface DashboardOptions {
  view: string;
  label: string;
  entity: string;
  added: boolean;
  rows: { id: string; tiles: TileOption[] }[];
  metrics: MetricOption[];
  groupable: { key: string; label: string; type: string }[];
  draftVersion?: number | null;
}

const KINDS: Record<string, string> = { 'display.kpi_tile': 'Number', 'display.bar_chart': 'Bar chart', 'display.metric_table': 'Table' };

/**
 * The dashboard builder outside the AI (REQ-WS-005, ADR 0019): add a dashboard, add tiles from existing metrics or a new
 * figure over any field you can see (fields added minutes ago included), lay them out in rows of up to four, and
 * remove them. Every change goes into the draft as one change; nothing is live until it is published.
 */
@Component({
  selector: 'esm-dashboard-builder',
  imports: [FormsModule, Icon],
  template: `
    <section class="mb-5 rounded-lg border border-subtle bg-raised p-5 shadow-xs" aria-labelledby="dash-builder-t">
      <header class="mb-3 flex flex-wrap items-center gap-2">
        <h2 id="dash-builder-t" class="flex-1 text-title-sm">Edit dashboard</h2>
        <button class="btn btn-sm" (click)="review()"><esm-icon name="check" [size]="14" /> Review and publish</button>
        <button class="btn btn-ghost btn-sm" (click)="done.emit()">Done</button>
      </header>
      <p class="mb-4 text-body-sm text-muted">Changes go into your draft. Figures for new tiles appear once you publish.</p>
      @if (options(); as o) {
        <ol class="mb-4 grid gap-2" aria-label="Rows">
          @for (r of o.rows; track r.id; let i = $index) {
            <li class="rounded-md border border-subtle p-3">
              <div class="mb-2 text-caption text-muted">Row {{ i + 1 }} · {{ r.tiles.length }} of 4 tiles</div>
              <ul class="flex flex-wrap gap-2" role="list">
                @for (t of r.tiles; track t.id) {
                  <li class="chip flex items-center gap-1.5">
                    <span>{{ tileLabel(t) }}</span>
                    <button class="btn btn-ghost btn-sm btn-icon" [attr.aria-label]="'Remove ' + tileLabel(t)" title="Remove" [disabled]="busy()" (click)="removeTile(t.id)"><esm-icon name="x" [size]="13" /></button>
                  </li>
                }
              </ul>
            </li>
          } @empty {
            <li class="text-body-sm text-muted">No tiles yet. Add the first one below.</li>
          }
        </ol>

        <form class="grid gap-3 rounded-md border border-subtle p-4" (ngSubmit)="addTile()" aria-label="Add a tile">
          <h3 class="text-title-xs">Add a tile</h3>
          <div class="flex flex-wrap gap-3">
            <label class="field grid gap-1 text-body-sm">Show as
              <select class="input" name="kind" [ngModel]="kind()" (ngModelChange)="kind.set($event); metric.set('')">
                <option value="kpi">Number</option>
                <option value="bar">Bar chart</option>
                <option value="table">Table</option>
              </select>
            </label>
            <label class="field grid gap-1 text-body-sm">Figure
              <select class="input" name="metric" [ngModel]="metric()" (ngModelChange)="metric.set($event)">
                <option value="">New figure…</option>
                @for (m of fitting(); track m.id) { <option [value]="m.id">{{ m.label }}{{ m.source === 'alerts' ? ' (alerts)' : '' }}</option> }
              </select>
            </label>
            <label class="field grid gap-1 text-body-sm">Where
              <select class="input" name="row" [ngModel]="row()" (ngModelChange)="row.set($event)">
                <option value="">New row</option>
                @for (r of o.rows; track r.id; let i = $index) {
                  @if (r.tiles.length < 4) { <option [value]="r.id">Beside row {{ i + 1 }}</option> }
                }
              </select>
            </label>
          </div>
          @if (!metric()) {
            <div class="flex flex-wrap gap-3">
              <label class="field grid gap-1 text-body-sm">Name
                <input class="input" name="newLabel" maxlength="60" required [ngModel]="newLabel()" (ngModelChange)="newLabel.set($event)" placeholder="Open by vendor" />
              </label>
              @if (kind() !== 'kpi') {
                <label class="field grid gap-1 text-body-sm">Group by
                  <select class="input" name="groupBy" [ngModel]="groupBy()" (ngModelChange)="groupBy.set($event)">
                    @for (g of o.groupable; track g.key) { <option [value]="g.key">{{ g.label }}</option> }
                  </select>
                </label>
              }
              <label class="flex items-center gap-2 self-end text-body-sm"><input type="checkbox" name="openOnly" [ngModel]="openOnly()" (ngModelChange)="openOnly.set($event)" /> Open {{ o.entity === 'core.tickets.ticket' ? 'tickets' : 'records' }} only</label>
            </div>
          }
          <div><button class="btn btn-primary btn-sm" type="submit" [disabled]="busy() || !canAdd()"><esm-icon name="plus" [size]="14" /> Add tile</button></div>
        </form>

        <div class="mt-4 flex flex-wrap gap-2">
          <form class="flex flex-wrap items-end gap-2" (ngSubmit)="create()" aria-label="New dashboard">
            <label class="field grid gap-1 text-body-sm">New dashboard
              <input class="input" name="dashLabel" maxlength="60" [ngModel]="dashLabel()" (ngModelChange)="dashLabel.set($event)" placeholder="Hardware vendors" />
            </label>
            <button class="btn btn-sm" type="submit" [disabled]="busy() || !dashLabel().trim()"><esm-icon name="plus" [size]="14" /> Create</button>
          </form>
          <span class="flex-1"></span>
          @if (o.added) { <button class="btn btn-ghost btn-sm" [disabled]="busy()" (click)="remove()"><esm-icon name="trash" [size]="14" /> Remove this dashboard</button> }
        </div>
      } @else if (error(); as err) {
        <div class="callout tone-red" role="alert">{{ err }}</div>
      } @else {
        <span class="skeleton block h-24"></span>
      }
    </section>
  `,
})
export class DashboardBuilder {
  readonly view = input.required<string>();
  readonly changed = output<void>();
  readonly done = output<void>();
  private readonly api = inject(Api);
  private readonly builder = inject(BuilderState);
  private readonly toasts = inject(Toasts);
  private readonly router = inject(Router);

  protected readonly options = signal<DashboardOptions | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly kind = signal<'kpi' | 'bar' | 'table'>('kpi');
  protected readonly metric = signal('');
  protected readonly row = signal('');
  protected readonly newLabel = signal('');
  protected readonly groupBy = signal('');
  protected readonly openOnly = signal(true);
  protected readonly dashLabel = signal('');

  /** Numbers take ungrouped metrics, charts and tables grouped ones (the server checks too). */
  protected readonly fitting = computed(() => (this.options()?.metrics ?? []).filter((m) => (this.kind() === 'kpi') !== m.grouped));
  protected readonly canAdd = computed(() => !!this.metric() || (!!this.newLabel().trim() && (this.kind() === 'kpi' || !!this.groupBy())));

  constructor() {
    queueMicrotask(() => void this.load());
  }

  protected tileLabel(t: TileOption): string {
    const metric = this.options()?.metrics.find((m) => m.id === t.metric);
    return `${t.title ?? metric?.label ?? t.metric ?? 'Tile'} · ${KINDS[t.type] ?? t.type}`;
  }

  async load(): Promise<void> {
    try {
      await this.builder.ensureDraft();
      const o = await this.api.get<DashboardOptions>(`/api/drafts/current/dashboards/${this.view()}/options`);
      this.options.set(o);
      if (!this.groupBy() && o.groupable.length) this.groupBy.set(o.groupable[0].key);
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not open the dashboard for editing.');
    }
  }

  protected async addTile(): Promise<void> {
    await this.run(async (version) => {
      await this.api.post(`/api/drafts/current/dashboards/${this.view()}/tiles`, {
        kind: this.kind(),
        metric: this.metric() || null,
        row: this.row() || null,
        newLabel: this.metric() ? null : this.newLabel().trim(),
        groupBy: this.metric() || this.kind() === 'kpi' ? null : this.groupBy() || null,
        openOnly: this.openOnly(),
        expectedVersion: version,
      });
      this.newLabel.set('');
      this.toasts.show('Tile added to your draft.', 'success');
    });
  }

  protected async removeTile(tile: string): Promise<void> {
    await this.run((version) => this.api.post(`/api/drafts/current/dashboards/${this.view()}/tiles/${tile}/remove`, { expectedVersion: version }));
  }

  protected async create(): Promise<void> {
    const options = this.options();
    if (!options) return;
    let created: string | null = null;
    await this.run(async (version) => {
      const r = await this.api.post<{ view: string }>('/api/drafts/current/dashboards', { label: this.dashLabel().trim(), entity: options.entity, expectedVersion: version });
      created = r.view;
    });
    if (created) {
      this.dashLabel.set('');
      void this.router.navigate(['/dashboards', created], { queryParams: { edit: 1 } });
    }
  }

  protected async remove(): Promise<void> {
    let removed = false;
    await this.run(async (version) => {
      await this.api.post(`/api/drafts/current/dashboards/${this.view()}/remove`, { expectedVersion: version });
      removed = true;
    });
    if (removed) {
      this.toasts.show('Dashboard removed from your draft. Publish to remove it for everyone.', 'success');
      void this.router.navigate(['/']);
    }
  }

  protected review(): void {
    if (!this.builder.open()) void this.builder.start();
  }

  /** Applies one change on the current draft version, then reloads the draft, the options and the page. */
  private async run(change: (version: number) => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    try {
      const draft: DraftState = await this.builder.ensureDraft();
      await change(draft.version);
      await this.builder.refresh();
      this.builder.revision.update((r) => r + 1);
      await this.load();
      this.changed.emit();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? (e.problem.suggestion ? `${e.message} ${e.problem.suggestion}` : e.message) : 'The change was not saved.', 'warning');
      await this.builder.refresh();
    } finally {
      this.busy.set(false);
    }
  }
}
