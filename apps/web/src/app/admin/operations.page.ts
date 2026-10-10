import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { Api, ApiError } from '../core/api';
import { Icon } from '../ui/icon';
import { Toasts } from '../core/toast';

interface QueueStats {
  queue: string;
  pending: number;
  running: number;
  dead: number;
  failed: number;
  oldestPendingSeconds?: number;
}

interface DeadLetter {
  queue: string;
  id: string;
  kind: string;
  attempts: number;
  lastError?: string;
  at: string;
}

/** Background work of this workspace: queue depth and age, dead letters, and replay (REQ-EVT-005). */
@Component({
  selector: 'esm-operations-page',
  imports: [Icon, DatePipe, DecimalPipe],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="activity" [size]="20" /></span>
        <div class="flex-1">
          <h1>Operations</h1>
          <p>Background work for this workspace: queues, timers and dead letters.</p>
        </div>
        <button class="btn btn-sm" (click)="load()"><esm-icon name="reset" [size]="14" /> Refresh</button>
      </header>
      <section class="tiles" aria-label="Queues">
        @for (q of stats(); track q.queue) {
          <div class="tile">
            <h2><esm-icon [name]="q.queue === 'jobs' ? 'clock' : 'zap'" [size]="15" /> {{ q.queue === 'jobs' ? 'Jobs and timers' : 'Events' }}</h2>
            <dl>
              <div><dt>Waiting</dt><dd>{{ q.pending }}</dd></div>
              <div><dt>Running</dt><dd>{{ q.running }}</dd></div>
              <div><dt>Dead letters</dt><dd [class.err]="q.dead > 0">{{ q.dead + q.failed }}</dd></div>
              <div><dt>Oldest waiting</dt><dd>{{ q.oldestPendingSeconds == null ? '—' : (q.oldestPendingSeconds | number: '1.0-0') + ' s' }}</dd></div>
            </dl>
          </div>
        }
      </section>
      <h2 class="section-title">Dead letters</h2>
      <p class="section-lead">Work that failed every retry, or stopped because it was no longer allowed. Replay requeues it; it runs again with its creator's current rights.</p>
      <div class="table-card">
        <table>
          <thead><tr><th scope="col">Kind</th><th scope="col">Queue</th><th scope="col">Attempts</th><th scope="col">Last error</th><th scope="col">When</th><th scope="col"></th></tr></thead>
          <tbody>
            @for (d of dead(); track d.id) {
              <tr>
                <td class="mono">{{ d.kind }}</td>
                <td>{{ d.queue }}</td>
                <td>{{ d.attempts }}</td>
                <td class="error">{{ d.lastError }}</td>
                <td class="small">{{ d.at | date: 'medium' }}</td>
                <td class="right"><button class="btn btn-sm" (click)="replay(d)">Replay</button></td>
              </tr>
            } @empty {
              <tr><td colspan="6" class="muted">No dead letters.</td></tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: `
    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: var(--esm-space-4); }
    .tile { padding: var(--esm-space-5); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); }
    .tile h2 { display: flex; align-items: center; gap: var(--esm-space-2); margin: 0 0 var(--esm-space-4); font: var(--esm-text-title-sm); }
    .tile h2 esm-icon { color: var(--esm-accent-brand); }
    dl { display: grid; grid-template-columns: 1fr 1fr; gap: var(--esm-space-4) var(--esm-space-3); margin: 0; }
    dt { font: var(--esm-text-caption); letter-spacing: 0.04em; text-transform: uppercase; color: var(--esm-text-muted); }
    dd { margin: 2px 0 0; font: var(--esm-text-title-lg); font-variant-numeric: tabular-nums; }
    .err { color: var(--esm-accent-red); }
    td { vertical-align: top; }
    td.error { max-width: 360px; color: var(--esm-text-muted); }
    .right { text-align: right; }
    .small { font: var(--esm-text-body-sm); }
  `,
})
export class OperationsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly stats = signal<QueueStats[]>([]);
  protected readonly dead = signal<DeadLetter[]>([]);

  constructor() {
    void this.load();
  }

  protected async load(): Promise<void> {
    try {
      const [stats, dead] = await Promise.all([this.api.get<QueueStats[]>('/api/admin/operations/queues'), this.api.get<DeadLetter[]>('/api/admin/operations/dead-letters')]);
      this.stats.set(stats);
      this.dead.set(dead);
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not load operations.', 'warning');
    }
  }

  protected async replay(d: DeadLetter): Promise<void> {
    try {
      await this.api.post(`/api/admin/operations/${d.queue}/${d.id}/replay`);
      this.toasts.show(`${d.kind} requeued.`, 'success');
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Replay failed.', 'warning');
    }
  }
}
