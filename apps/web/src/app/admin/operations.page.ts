import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { Api, ApiError } from '../core/api';
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
  imports: [DatePipe, DecimalPipe],
  template: `
    <div class="page">
      <header class="top"><h1>Operations</h1><button class="btn btn-sm" (click)="load()">Refresh</button></header>
      <section class="tiles" aria-label="Queues">
        @for (q of stats(); track q.queue) {
          <div class="tile">
            <h2>{{ q.queue === 'jobs' ? 'Jobs and timers' : 'Events' }}</h2>
            <dl>
              <div><dt>Waiting</dt><dd>{{ q.pending }}</dd></div>
              <div><dt>Running</dt><dd>{{ q.running }}</dd></div>
              <div><dt>Dead letters</dt><dd [class.err]="q.dead > 0">{{ q.dead + q.failed }}</dd></div>
              <div><dt>Oldest waiting</dt><dd>{{ q.oldestPendingSeconds == null ? '—' : (q.oldestPendingSeconds | number: '1.0-0') + ' s' }}</dd></div>
            </dl>
          </div>
        }
      </section>
      <h2>Dead letters</h2>
      <p class="muted small">Work that failed every retry, or stopped because it was no longer allowed. Replay requeues it; it runs again with its creator's current rights.</p>
      <div class="table-wrap">
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
    .page { padding: var(--esm-space-5) var(--esm-space-6); max-width: 1100px; }
    .top { display: flex; align-items: center; justify-content: space-between; margin-bottom: var(--esm-space-4); }
    h1 { margin: 0; font: var(--esm-text-title-lg); }
    h2 { font: var(--esm-text-title-md); margin: var(--esm-space-5) 0 var(--esm-space-2); }
    .tiles { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: var(--esm-space-3); }
    .tile { padding: var(--esm-space-4); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); background: var(--esm-surface-raised); }
    .tile h2 { margin: 0 0 var(--esm-space-2); font: var(--esm-text-title-sm); }
    dl { display: grid; grid-template-columns: 1fr 1fr; gap: var(--esm-space-2); margin: 0; }
    dt { font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    dd { margin: 0; font: var(--esm-text-title-md); }
    .err { color: var(--esm-accent-red); }
    .table-wrap { overflow-x: auto; border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); }
    table { width: 100%; border-collapse: collapse; }
    th { text-align: left; font: var(--esm-text-body-sm); color: var(--esm-text-muted); padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); background: var(--esm-surface-sunken); }
    td { padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); font: var(--esm-text-body-sm); vertical-align: top; }
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
