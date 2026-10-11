import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface Entry {
  id: number;
  at: string;
  actor?: string | null;
  agent: boolean;
  channel: string;
  operation: string;
  targetType: string;
  targetId?: string | null;
  outcome: string;
  workspaceWide: boolean;
  details?: unknown;
}

interface Chain {
  sealed: number;
  anchorSeq: number;
  sealedAt?: string | null;
  verifiedThrough: number;
  verifiedAt?: string | null;
  brokenAt?: number | null;
  brokenDetectedAt?: string | null;
  verifying: boolean;
}

interface Hold {
  id: string;
  reason: string;
  createdBy: string;
  createdAt: string;
  releasedBy?: string | null;
  releasedAt?: string | null;
}

interface Policy {
  retentionDays: number;
  minDays: number;
  maxDays: number;
  holds: Hold[];
}

/**
 * The audit log (ADR 0022, REQ-AUD-001): search and export (both audited server-side), the tamper-evidence chain with
 * on-demand verification, retention and legal holds. The server checks every right; agents never reach it.
 */
@Component({
  selector: 'esm-audit-page',
  imports: [DatePipe, FormsModule, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="scroll-text" [size]="18" /></span>
        <div>
          <h1 class="text-title-lg">Audit log</h1>
          <p class="section-lead">Every change and decision in this workspace, sealed into a chain that shows tampering.</p>
        </div>
      </header>
      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }

      <div class="mb-6 grid grid-cols-2 gap-4 max-[900px]:grid-cols-1">
        <section class="card-item" aria-labelledby="chain-title">
          <h2 id="chain-title" class="text-title-sm">Tamper evidence</h2>
          @if (chain(); as c) {
            @if (c.brokenAt) {
              <div class="callout tone-red mt-2" role="alert">The chain is broken at entry {{ c.brokenAt }} (found {{ c.brokenDetectedAt | date: 'MMM d, HH:mm' }}). An entry was changed or removed outside the platform.</div>
            }
            <p class="mt-2 text-body-sm">{{ c.sealed }} entries sealed{{ c.anchorSeq ? ', starting after entry ' + c.anchorSeq + ' (older ones expired)' : '' }}.</p>
            <p class="text-body-sm text-muted">{{ c.verifiedAt ? 'Verified through entry ' + c.verifiedThrough + ' on ' + (c.verifiedAt | date: 'MMM d, HH:mm') + '.' : 'Not verified yet.' }}</p>
            @if (canManage()) { <button class="btn btn-sm mt-2" (click)="verify()" [disabled]="busy()">Verify now</button> }
          } @else {
            <span class="skeleton mt-2 block h-4 w-2/3"></span>
          }
        </section>
        <section class="card-item" aria-labelledby="retention-title">
          <h2 id="retention-title" class="text-title-sm">Retention and legal holds</h2>
          @if (policy(); as p) {
            <form class="mt-2 flex flex-wrap items-end gap-2" (ngSubmit)="saveRetention()" aria-label="Retention">
              <label class="field-row">Keep entries (days) <input class="input w-32" type="number" name="days" [min]="p.minDays" [max]="p.maxDays" [(ngModel)]="days" [disabled]="!canManage()" /></label>
              @if (canManage()) { <button class="btn btn-sm" type="submit">Save</button> }
            </form>
            <ul class="mt-3 flex flex-col gap-2" aria-label="Legal holds">
              @for (h of p.holds; track h.id) {
                <li class="flex flex-wrap items-center gap-2 text-body-sm">
                  <span class="badge" [class]="h.releasedAt ? 'tone-gray' : 'tone-amber'">{{ h.releasedAt ? 'released' : 'active' }}</span>
                  <span class="flex-1">{{ h.reason }} · {{ h.createdBy }}, {{ h.createdAt | date: 'MMM d' }}</span>
                  @if (!h.releasedAt && canManage()) { <button class="btn btn-ghost btn-sm" (click)="release(h)">Release</button> }
                </li>
              } @empty {
                <li class="text-body-sm text-muted">No legal holds. Retention runs as set.</li>
              }
            </ul>
            @if (canManage()) {
              <form class="mt-2 flex flex-wrap items-end gap-2" (ngSubmit)="placeHold()" aria-label="Place a legal hold">
                <label class="field-row min-w-[220px] flex-1">Hold reason <input class="input" name="reason" [(ngModel)]="holdReason" maxlength="500" /></label>
                <button class="btn btn-sm" type="submit" [disabled]="!holdReason.trim()">Place hold</button>
              </form>
            }
          }
        </section>
      </div>

      <form class="card-item mb-3 flex flex-wrap items-end gap-3" (ngSubmit)="search()" aria-label="Search the audit log">
        <label class="field-row">Operation starts with <input class="input mono w-48" name="operation" [(ngModel)]="operation" placeholder="record." /></label>
        <label class="field-row">Target type <input class="input mono w-48" name="targetType" [(ngModel)]="targetType" placeholder="core.tickets.ticket" /></label>
        <label class="field-row">Target ID <input class="input mono w-64" name="targetId" [(ngModel)]="targetId" /></label>
        <label class="field-row">Outcome
          <select class="input w-32" name="outcome" [(ngModel)]="outcome"><option value="">Any</option><option value="success">success</option><option value="denied">denied</option><option value="failed">failed</option></select>
        </label>
        <button class="btn btn-primary" type="submit">Search</button>
        <span class="flex-1"></span>
        @if (canExport()) {
          <button class="btn btn-sm" type="button" (click)="download('csv')">Export CSV</button>
          @if (canExportChain()) { <button class="btn btn-sm" type="button" (click)="download('jsonl')">Export with chain (JSON Lines)</button> }
        }
      </form>

      <div class="table-card">
        <table>
          <caption class="sr-only">Audit entries</caption>
          <thead><tr><th scope="col">When</th><th scope="col">Who</th><th scope="col">Operation</th><th scope="col">Target</th><th scope="col">Outcome</th></tr></thead>
          <tbody>
            @for (e of entries(); track e.id) {
              <tr>
                <td class="whitespace-nowrap">{{ e.at | date: 'MMM d, HH:mm:ss' }}</td>
                <td>{{ e.actor ?? 'system' }}@if (e.agent) { <span class="badge tone-blue ml-1">AI agent</span> }</td>
                <td><details><summary class="mono">{{ e.operation }}</summary><pre class="details">{{ json(e.details) }}</pre></details></td>
                <td class="mono">{{ e.targetType }}{{ e.targetId ? ' ' + e.targetId : '' }}@if (e.workspaceWide) { <span class="badge tone-gray ml-1">workspace</span> }</td>
                <td><span class="badge" [class]="e.outcome === 'success' ? 'tone-green' : 'tone-red'">{{ e.outcome }}</span></td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="text-muted">No entries match.</td></tr>
            }
          </tbody>
        </table>
      </div>
      @if (next()) { <div class="mt-3 flex justify-center"><button class="btn btn-sm" (click)="older()">Older</button></div> }
    </div>
  `,
  styles: `
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    .field-row { display: grid; gap: 4px; font: var(--esm-text-body-sm); font-weight: 500; }
    .details { margin: 4px 0 0; max-width: 560px; max-height: 240px; overflow: auto; font: var(--esm-text-caption); white-space: pre-wrap; overflow-wrap: anywhere; }
    summary { cursor: pointer; }
  `,
})
export class AuditPage {
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly toasts = inject(Toasts);

  protected readonly entries = signal<Entry[]>([]);
  protected readonly next = signal<number | null>(null);
  protected readonly chain = signal<Chain | null>(null);
  protected readonly policy = signal<Policy | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  private readonly actions = computed(() => this.session.app()?.actions ?? []);
  protected readonly canManage = computed(() => this.actions().some((a) => a === 'audit.manage' || a === 'permissions.admin'));
  protected readonly canExport = computed(() => this.actions().some((a) => a === 'audit.export' || a === 'permissions.admin'));
  /** The chain export carries unredacted entries, so it is for permission administrators only. */
  protected readonly canExportChain = computed(() => this.actions().includes('permissions.admin'));
  protected operation = '';
  protected targetType = '';
  protected targetId = '';
  protected outcome = '';
  protected days = 2555;
  protected holdReason = '';

  constructor() {
    void this.search();
    void this.loadChain();
    void this.loadPolicy();
  }

  private filters(): Record<string, string | undefined> {
    return { operation: this.operation.trim() || undefined, targetType: this.targetType.trim() || undefined, targetId: this.targetId.trim() || undefined, outcome: this.outcome || undefined };
  }

  protected async search(before?: number): Promise<void> {
    try {
      const page = await this.api.get<{ items: Entry[]; next?: number | null }>('/api/admin/audit', { ...this.filters(), before: before?.toString(), limit: '50' });
      this.entries.set(before ? [...this.entries(), ...page.items] : page.items);
      this.next.set(page.next ?? null);
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not search the audit log.');
    }
  }

  protected older(): void {
    const n = this.next();
    if (n) void this.search(n);
  }

  protected json(v: unknown): string {
    return JSON.stringify(v, null, 2);
  }

  private async loadChain(): Promise<void> {
    try {
      this.chain.set(await this.api.get<Chain>('/api/admin/audit/chain'));
    } catch {
      this.chain.set(null);
    }
  }

  private async loadPolicy(): Promise<void> {
    try {
      const p = await this.api.get<Policy>('/api/admin/audit/policy');
      this.policy.set(p);
      this.days = p.retentionDays;
    } catch {
      this.policy.set(null);
    }
  }

  protected async verify(): Promise<void> {
    this.busy.set(true);
    try {
      const c = await this.api.post<Chain>('/api/admin/audit/chain/verify');
      this.chain.set(c);
      this.toasts.show(c.brokenAt ? `The chain is broken at entry ${c.brokenAt}.` : 'The chain is intact.', c.brokenAt ? 'warning' : 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Verification failed.', 'warning');
    } finally {
      this.busy.set(false);
    }
  }

  protected async saveRetention(): Promise<void> {
    try {
      this.policy.set(await this.api.put<Policy>('/api/admin/audit/policy', { retentionDays: Number(this.days) }));
      this.toasts.show('Retention saved.', 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Retention was not saved.', 'warning');
      await this.loadPolicy();
    }
  }

  protected async placeHold(): Promise<void> {
    try {
      this.policy.set(await this.api.post<Policy>('/api/admin/audit/holds', { reason: this.holdReason.trim() }));
      this.holdReason = '';
      this.toasts.show('Legal hold placed: nothing expires until it is released.', 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The hold was not placed.', 'warning');
    }
  }

  protected async release(h: Hold): Promise<void> {
    try {
      this.policy.set(await this.api.post<Policy>(`/api/admin/audit/holds/${h.id}/release`));
      this.toasts.show('Legal hold released.', 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The hold was not released.', 'warning');
    }
  }

  protected async download(format: 'csv' | 'jsonl'): Promise<void> {
    try {
      const blob = await this.api.download('/api/admin/audit/export', { format, ...this.filters() });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `audit.${format}`;
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    } catch (e) {
      this.toasts.show(e instanceof ApiError && e.status === 429 ? 'Two exports a minute at most. Try again shortly.' : 'The export failed.', 'warning');
    }
  }
}
