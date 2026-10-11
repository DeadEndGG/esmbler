import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface Alert {
  id: string;
  rule: string;
  ruleLabel: string;
  status: 'open' | 'acknowledged' | 'resolved' | 'suppressed';
  severity: string;
  title: string;
  entity?: string | null;
  recordId?: string | null;
  ownerGroup?: string | null;
  owner?: string | null;
  assignee?: string | null;
  assigneeId?: string | null;
  level: number;
  occurrences: number;
  reopened: number;
  openedAt: string;
  lastOccurredAt: string;
  acknowledgedAt?: string | null;
  acknowledgedBy?: string | null;
  resolvedAt?: string | null;
  resolution?: string | null;
  version: number;
  canRespond: boolean;
}

interface Activity {
  kind: string;
  actor?: string | null;
  level?: number | null;
  details?: Record<string, unknown> | null;
  at: string;
}

interface Suppression {
  id: string;
  reason: string;
  rule?: string | null;
  startsAt: string;
  endsAt: string;
  createdBy: string;
  cancelledAt?: string | null;
}

const SEVERITY_TONES: Record<string, string> = { critical: 'tone-red', high: 'tone-red', medium: 'tone-amber', low: 'tone-blue', info: 'tone-gray' };
const STATUS_TONES: Record<string, string> = { open: 'tone-red', acknowledged: 'tone-amber', suppressed: 'tone-gray', resolved: 'tone-green' };

/**
 * Alerts (ADR 0022, REQ-ALR-001): what needs a person's attention now. Owners and responders acknowledge (which stops
 * escalation), resolve and assign; alerts about a ticket resolve themselves when the ticket is done. People who manage
 * alerts open maintenance windows. The server decides who sees and may do what.
 */
@Component({
  selector: 'esm-alerts-page',
  imports: [DatePipe, FormsModule, RouterLink, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="siren" [size]="18" /></span>
        <div class="flex-1">
          <h1 class="text-title-lg">Alerts</h1>
          <p class="section-lead">Problems that need someone now. Acknowledge to stop escalation; alerts about a ticket resolve when it is done.</p>
        </div>
      </header>

      <div class="mb-4 flex flex-wrap items-center gap-2">
        <div class="flex gap-2" role="tablist" aria-label="Alert status">
          @for (t of tabs; track t.id) {
            <button class="pill" role="tab" [attr.aria-selected]="tab() === t.id" [class.on]="tab() === t.id" (click)="open(t.id)">{{ t.label }}</button>
          }
        </div>
        <span class="flex-1"></span>
        <label class="flex items-center gap-2 text-body-sm"><input type="checkbox" [checked]="mine()" (change)="toggleMine()" /> Only mine</label>
      </div>

      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }

      @if (tab() === 'windows') {
        <div class="grid grid-cols-[minmax(0,1fr)_340px] items-start gap-6 max-[960px]:grid-cols-1">
          <ul class="flex flex-col gap-3" aria-label="Maintenance windows">
            @for (w of windows(); track w.id) {
              <li class="card-item">
                <div class="flex flex-wrap items-center gap-2">
                  <strong>{{ w.reason }}</strong>
                  <span class="badge" [class]="w.cancelledAt ? 'tone-gray' : 'tone-amber'">{{ w.cancelledAt ? 'ended' : 'active or planned' }}</span>
                  <span class="flex-1"></span>
                  @if (!w.cancelledAt && canManage()) { <button class="btn btn-ghost btn-sm" (click)="cancelWindow(w)">End now</button> }
                </div>
                <p class="mt-1 text-body-sm text-muted">{{ w.startsAt | date: 'MMM d, HH:mm' }} to {{ w.endsAt | date: 'MMM d, HH:mm' }} · {{ w.rule ?? 'every alert rule' }} · by {{ w.createdBy }}</p>
              </li>
            } @empty {
              <li class="rounded-lg border border-dashed border-strong p-6 text-center text-muted">No maintenance windows in the last 30 days.</li>
            }
          </ul>
          @if (canManage()) {
            <form class="card-item grid gap-3" (ngSubmit)="addWindow()" aria-label="New maintenance window">
              <h2 class="text-title-sm">Silence alerts for a while</h2>
              <p class="text-caption text-muted">Alerts raised during the window are recorded but nobody is told; if the problem is still there after it, the alert is announced then.</p>
              <label class="field-row">Reason <input class="input" name="reason" [(ngModel)]="windowReason" required maxlength="500" /></label>
              <label class="field-row">Hours (up to 336) <input class="input" type="number" name="hours" min="1" max="336" [(ngModel)]="windowHours" /></label>
              <label class="field-row">Alert rule (optional) <input class="input mono" name="rule" [(ngModel)]="windowRule" placeholder="every rule" /></label>
              <div class="flex justify-end"><button class="btn btn-primary" type="submit" [disabled]="!windowReason.trim()">Start window</button></div>
            </form>
          }
        </div>
      } @else if (!alerts()) {
        <div class="flex flex-col gap-3" aria-busy="true">@for (i of [1, 2, 3]; track i) { <div class="card-item"><span class="skeleton block h-4 w-1/2"></span></div> }</div>
      } @else {
        <ul class="flex flex-col gap-3" aria-label="Alerts">
          @for (a of alerts()!; track a.id) {
            <li class="card-item" [class.urgent]="a.status === 'open'">
              <div class="flex flex-wrap items-center gap-2">
                <span class="badge" [class]="severityTone(a.severity)">{{ a.severity }}</span>
                <span class="badge" [class]="statusTone(a.status)">{{ a.status }}</span>
                @if (a.entity && a.recordId) {
                  <a class="title" [routerLink]="['/records', a.entity, a.recordId]">{{ a.title }}</a>
                } @else {
                  <span class="title">{{ a.title }}</span>
                }
              </div>
              <p class="mt-1 text-body-sm text-muted">
                {{ a.ruleLabel }} · {{ a.ownerGroup ?? a.owner ?? 'no owner' }}{{ a.assignee ? ' · assigned to ' + a.assignee : '' }} · level {{ a.level }} · {{ a.occurrences }} time{{ a.occurrences === 1 ? '' : 's' }} · opened {{ a.openedAt | date: 'MMM d, HH:mm' }}
              </p>
              <div class="mt-2 flex flex-wrap items-center gap-2">
                @if (a.canRespond && a.status !== 'resolved') {
                  @if (a.status !== 'acknowledged') { <button class="btn btn-primary btn-sm" (click)="acknowledge(a)">Acknowledge</button> }
                  @if (a.assigneeId !== me()) { <button class="btn btn-sm" (click)="assignToMe(a)">Assign to me</button> }
                  <button class="btn btn-sm" (click)="resolving.set(resolving() === a.id ? null : a.id)" [attr.aria-expanded]="resolving() === a.id">Resolve…</button>
                }
                <button class="btn btn-ghost btn-sm" (click)="toggleTimeline(a)" [attr.aria-expanded]="timelineFor() === a.id">Timeline</button>
              </div>
              @if (resolving() === a.id) {
                <form class="mt-2 flex flex-wrap items-end gap-2" (ngSubmit)="resolve(a)" [attr.aria-label]="'Resolve ' + a.title">
                  <label class="field-row min-w-[260px] flex-1">Note (optional) <input class="input" name="note" [(ngModel)]="note" maxlength="1000" /></label>
                  <button class="btn btn-primary btn-sm" type="submit">Resolve alert</button>
                </form>
              }
              @if (timelineFor() === a.id) {
                <ol class="timeline mt-2" aria-label="Timeline">
                  @for (t of timeline(); track $index) {
                    <li><span class="text-caption text-muted">{{ t.at | date: 'MMM d, HH:mm' }}</span> {{ describe(t) }}</li>
                  }
                </ol>
              }
            </li>
          } @empty {
            <li class="rounded-lg border border-dashed border-strong p-8 text-center text-muted">{{ tab() === 'resolved' ? 'Nothing resolved yet.' : 'No alerts. All quiet.' }}</li>
          }
        </ul>
        @if (more()) { <div class="mt-3 flex justify-center"><button class="btn btn-sm" (click)="loadMore()">More</button></div> }
      }
    </div>
  `,
  styles: `
    .pill { display: inline-flex; align-items: center; height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .pill.on { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); border-color: var(--esm-accent-brand); }
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    .card-item.urgent { box-shadow: var(--esm-shadow-card); }
    .title { font: var(--esm-text-body); font-weight: 600; color: var(--esm-text-default); overflow-wrap: anywhere; }
    .field-row { display: grid; gap: 4px; font: var(--esm-text-body-sm); font-weight: 500; }
    .timeline { margin: 0; padding-left: var(--esm-space-4); display: grid; gap: 4px; font: var(--esm-text-body-sm); }
  `,
})
export class AlertsPage {
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly toasts = inject(Toasts);

  protected readonly tabs = [
    { id: 'active' as const, label: 'Active' },
    { id: 'suppressed' as const, label: 'Silenced' },
    { id: 'resolved' as const, label: 'Resolved' },
    { id: 'windows' as const, label: 'Maintenance windows' },
  ];
  protected readonly tab = signal<'active' | 'suppressed' | 'resolved' | 'windows'>('active');
  protected readonly mine = signal(false);
  protected readonly alerts = signal<Alert[] | null>(null);
  protected readonly more = signal(false);
  protected readonly windows = signal<Suppression[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly resolving = signal<string | null>(null);
  protected readonly timelineFor = signal<string | null>(null);
  protected readonly timeline = signal<Activity[]>([]);
  protected readonly me = computed(() => this.session.app()?.user.id);
  protected readonly canManage = computed(() => this.session.app()?.actions.some((a) => a === 'alerts.manage' || a === 'permissions.admin') ?? false);
  protected note = '';
  protected windowReason = '';
  protected windowHours = 2;
  protected windowRule = '';

  constructor() {
    void this.load();
  }

  protected severityTone(s: string): string {
    return SEVERITY_TONES[s] ?? 'tone-gray';
  }

  protected statusTone(s: string): string {
    return STATUS_TONES[s] ?? 'tone-gray';
  }

  protected open(tab: 'active' | 'suppressed' | 'resolved' | 'windows'): void {
    this.tab.set(tab);
    if (tab === 'windows') void this.loadWindows();
    else void this.load();
  }

  protected toggleMine(): void {
    this.mine.update((v) => !v);
    void this.load();
  }

  private async load(append = false): Promise<void> {
    if (!append) this.alerts.set(null);
    try {
      const offset = append ? (this.alerts()?.length ?? 0) : 0;
      const page = await this.api.get<{ items: Alert[]; more: boolean }>('/api/alerts', { status: this.tab(), mine: this.mine() ? 'true' : undefined, limit: '50', offset: String(offset) });
      this.alerts.set(append ? [...(this.alerts() ?? []), ...page.items] : page.items);
      this.more.set(page.more);
      this.error.set(null);
    } catch (e) {
      this.alerts.set([]);
      this.error.set(e instanceof ApiError ? e.message : 'Could not load alerts.');
    }
  }

  protected loadMore(): void {
    void this.load(true);
  }

  private async act(a: Alert, path: string, body: Record<string, unknown>, done: string): Promise<void> {
    try {
      const updated = await this.api.post<Alert>(`/api/alerts/${a.id}/${path}`, { version: a.version, ...body });
      this.alerts.update((list) => (list ?? []).map((x) => (x.id === a.id ? updated : x)).filter((x) => this.tab() !== 'active' || x.status === 'open' || x.status === 'acknowledged'));
      this.toasts.show(done, 'success');
      this.session.refreshAlerts();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'That did not work.', 'warning');
      await this.load();
    }
  }

  protected acknowledge(a: Alert): void {
    void this.act(a, 'acknowledge', {}, 'Acknowledged: escalation stops.');
  }

  protected assignToMe(a: Alert): void {
    void this.act(a, 'assign', { assignee: this.me() }, 'Assigned to you.');
  }

  protected resolve(a: Alert): void {
    const note = this.note.trim();
    this.resolving.set(null);
    this.note = '';
    void this.act(a, 'resolve', { note: note || undefined }, 'Resolved.');
  }

  protected async toggleTimeline(a: Alert): Promise<void> {
    if (this.timelineFor() === a.id) {
      this.timelineFor.set(null);
      return;
    }
    try {
      const detail = await this.api.get<{ activity: Activity[] }>(`/api/alerts/${a.id}`);
      this.timeline.set(detail.activity);
      this.timelineFor.set(a.id);
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not load the timeline.', 'warning');
    }
  }

  protected describe(t: Activity): string {
    const who = t.actor ? ` by ${t.actor}` : '';
    switch (t.kind) {
      case 'opened': return 'Opened';
      case 'occurred': return `Happened again (${t.details?.['occurrences'] ?? ''} times)`;
      case 'escalated': return `Escalated to level ${t.level}`;
      case 'acknowledged': return `Acknowledged${who}`;
      case 'assigned': return `Assigned${who}`;
      case 'resolved': return t.details?.['resolution'] === 'auto' ? 'Resolved automatically' : `Resolved${who}${t.details?.['note'] ? `: ${t.details['note']}` : ''}`;
      case 'reopened': return 'Reopened (happened again soon after it was resolved)';
      case 'suppressed': return 'Recorded during a maintenance window';
      case 'unsuppressed': return 'Announced after the maintenance window';
      default: return t.kind;
    }
  }

  private async loadWindows(): Promise<void> {
    try {
      this.windows.set(await this.api.get<Suppression[]>('/api/admin/alerts/suppressions'));
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load maintenance windows.');
    }
  }

  protected async addWindow(): Promise<void> {
    const hours = Math.min(336, Math.max(1, Number(this.windowHours) || 1));
    const now = new Date();
    try {
      await this.api.post('/api/admin/alerts/suppressions', {
        reason: this.windowReason.trim(),
        rule: this.windowRule.trim() || undefined,
        startsAt: now.toISOString(),
        endsAt: new Date(now.getTime() + hours * 3_600_000).toISOString(),
      });
      this.windowReason = '';
      this.windowRule = '';
      this.toasts.show('Maintenance window started.', 'success');
      await this.loadWindows();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The window was not started.', 'warning');
    }
  }

  protected async cancelWindow(w: Suppression): Promise<void> {
    try {
      await this.api.post(`/api/admin/alerts/suppressions/${w.id}/cancel`);
      await this.loadWindows();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The window was not ended.', 'warning');
    }
  }
}
