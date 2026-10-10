import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from '../core/api';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface Channel {
  key: string;
  type: 'email' | 'sms';
  label: string;
  mode: 'live' | 'capture' | 'disabled';
  settings: Record<string, any>;
  secrets: Record<string, boolean>;
  ratePerMinute: number;
  hostAllowed: boolean;
  updatedAt: string;
}

interface Delivery {
  id: string;
  eventId: string;
  eventType: string;
  route: string;
  channel: string;
  recipient?: string | null;
  external: boolean;
  mandatory: boolean;
  status: string;
  reason?: string | null;
  attempts: number;
  subject: string;
  body?: string | null;
  createdAt: string;
  sentAt?: string | null;
  replayOf?: string | null;
}

interface PreviewRecipient {
  recipient: string;
  external: boolean;
  channels: string[];
  suppressed?: string | null;
  subject: string;
  body: string;
}

interface Preview {
  route: string;
  template: string;
  profile: string;
  mandatory: boolean;
  recipients: PreviewRecipient[];
  note?: string | null;
}

interface ChannelForm {
  key: string;
  type: 'email' | 'sms';
  label: string;
  mode: 'live' | 'capture' | 'disabled';
  host: string;
  port: string;
  from: string;
  accountSid: string;
  secretRefs: string;
  ratePerMinute: number;
}

const STATUS_TONES: Record<string, string> = { sent: 'tone-green', captured: 'tone-blue', queued: 'tone-gray', sending: 'tone-gray', failed: 'tone-red', ambiguous: 'tone-amber', suppressed: 'tone-gray' };

/**
 * Notification administration (ADR 0021, REQ-NOT-001, REQ-NOT-005, REQ-NOT-006): the environment's channel instances
 * (settings and secret names only; sandboxes capture), delivery history with replay, and route previews that send nothing.
 */
@Component({
  selector: 'esm-notifications-admin',
  imports: [DatePipe, FormsModule, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="send" [size]="18" /></span>
        <div>
          <h1 class="text-title-lg">Notification channels</h1>
          <p class="section-lead">How this environment sends email and text messages, what was delivered, and what a route would send.</p>
        </div>
      </header>

      <div class="mb-4 flex flex-wrap gap-2" role="tablist" aria-label="Notification administration">
        @for (t of tabs; track t.id) {
          <button class="pill" role="tab" [attr.aria-selected]="tab() === t.id" [class.on]="tab() === t.id" (click)="open(t.id)">{{ t.label }}</button>
        }
      </div>
      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }

      @switch (tab()) {
        @case ('channels') {
          <div class="grid grid-cols-[minmax(0,1fr)_360px] items-start gap-6 max-[960px]:grid-cols-1">
            <ul class="flex flex-col gap-3" aria-label="Channels">
              @for (c of channels(); track c.key) {
                <li class="card-item">
                  <div class="flex flex-wrap items-center gap-2">
                    <strong>{{ c.label }}</strong>
                    <span class="mono text-muted">{{ c.key }}</span>
                    <span class="badge" [class]="c.mode === 'live' ? 'tone-green' : c.mode === 'capture' ? 'tone-blue' : 'tone-gray'">{{ c.mode }}</span>
                    <span class="flex-1"></span>
                    <button class="btn btn-ghost btn-sm" (click)="edit(c)">Edit</button>
                    @if (c.type === 'email') { <button class="btn btn-sm" (click)="test(c)">Send me a test</button> }
                  </div>
                  <p class="mt-1 text-body-sm text-muted">
                    {{ c.type === 'email' ? 'Email' : 'Text messages' }} through {{ c.settings['host'] ?? (c.type === 'sms' ? 'api.twilio.com' : 'no host') }}, at most {{ c.ratePerMinute }} a minute.
                    @if (!c.hostAllowed) { <span class="text-fg">This host is not allowed for live sending on this platform.</span> }
                  </p>
                  @if (keys(c.secrets).length) {
                    <p class="mt-1 flex flex-wrap gap-2 text-caption">
                      @for (s of keys(c.secrets); track s) { <span class="badge" [class]="c.secrets[s] ? 'tone-green' : 'tone-amber'">{{ s }}: {{ c.secrets[s] ? 'set' : 'missing' }}</span> }
                    </p>
                  }
                </li>
              } @empty {
                <li class="rounded-lg border border-dashed border-strong p-6 text-center text-muted">No channels yet. In-app notifications need none; add email or text messages here.</li>
              }
            </ul>

            <form class="card-item grid gap-3" (ngSubmit)="save()" aria-label="Channel settings">
              <h2 class="text-title-sm">{{ editing() ? 'Change channel' : 'Add a channel' }}</h2>
              <label class="field-row">Key <input class="input" name="key" [(ngModel)]="form.key" [disabled]="editing()" required pattern="[a-z][a-z0-9_]{1,40}" /></label>
              <label class="field-row">Type
                <select class="input" name="type" [(ngModel)]="form.type"><option value="email">Email</option><option value="sms">Text message</option></select>
              </label>
              <label class="field-row">Label <input class="input" name="label" [(ngModel)]="form.label" /></label>
              <label class="field-row">Mode
                <select class="input" name="mode" [(ngModel)]="form.mode">
                  <option value="capture">Capture (keep messages, send nothing)</option>
                  <option value="live">Live</option>
                  <option value="disabled">Disabled</option>
                </select>
              </label>
              <label class="field-row">Host <input class="input" name="host" [(ngModel)]="form.host" [placeholder]="form.type === 'sms' ? 'api.twilio.com' : 'smtp.example.com'" /></label>
              @if (form.type === 'email') {
                <label class="field-row">Port <input class="input" name="port" [(ngModel)]="form.port" placeholder="587" /></label>
              } @else {
                <label class="field-row">Account SID <input class="input" name="accountSid" [(ngModel)]="form.accountSid" /></label>
              }
              <label class="field-row">From <input class="input" name="from" [(ngModel)]="form.from" [placeholder]="form.type === 'sms' ? '+15550100' : 'desk@example.com'" /></label>
              <label class="field-row">Secret names
                <input class="input" name="secretRefs" [(ngModel)]="form.secretRefs" [placeholder]="form.type === 'sms' ? 'SMS_AUTH_TOKEN' : 'SMTP_USERNAME, SMTP_PASSWORD'" />
              </label>
              <p class="text-caption text-muted">Names only. Values live in the platform's secret store and are never shown here.</p>
              <label class="field-row">Per minute <input class="input" type="number" name="rate" min="1" max="10000" [(ngModel)]="form.ratePerMinute" /></label>
              <div class="flex justify-end gap-2">
                @if (editing()) { <button class="btn btn-ghost" type="button" (click)="reset()">Cancel</button> }
                <button class="btn btn-primary" type="submit">Save</button>
              </div>
            </form>
          </div>
        }
        @case ('deliveries') {
          <div class="mb-3 flex flex-wrap items-center gap-2">
            <label class="text-body-sm">Status
              <select class="input ml-1 h-8 min-h-8 w-auto py-0" [ngModel]="status()" (ngModelChange)="status.set($event); loadDeliveries()">
                <option value="">Any</option>
                @for (s of statuses; track s) { <option [value]="s">{{ s }}</option> }
              </select>
            </label>
          </div>
          <div class="table-card">
            <table>
              <caption class="sr-only">Deliveries</caption>
              <thead><tr><th scope="col">When</th><th scope="col">Message</th><th scope="col">To</th><th scope="col">Channel</th><th scope="col">Status</th><th scope="col"><span class="sr-only">Actions</span></th></tr></thead>
              <tbody>
                @for (d of deliveries(); track d.id) {
                  <tr>
                    <td class="whitespace-nowrap">{{ d.createdAt | date: 'MMM d, HH:mm' }}</td>
                    <td>
                      <details><summary>{{ d.subject }}</summary><p class="mt-1 text-body-sm whitespace-pre-line text-muted">{{ d.body ?? 'Only people who manage notifications see the text.' }}</p>
                      <p class="text-caption text-muted">{{ d.eventType }} · {{ d.route }}{{ d.replayOf ? ' · replay' : '' }}</p></details>
                    </td>
                    <td>{{ d.recipient ?? '—' }} @if (d.external) { <span class="badge tone-amber">external</span> }</td>
                    <td>{{ d.channel }}</td>
                    <td><span class="badge" [class]="tone(d.status)">{{ d.status }}</span> @if (d.reason) { <span class="text-caption text-muted">{{ d.reason }}</span> } <span class="text-caption text-muted">· {{ d.attempts }} attempt{{ d.attempts === 1 ? '' : 's' }}</span></td>
                    <td class="text-right">
                      @if (d.status === 'failed' || d.status === 'ambiguous') {
                        <button class="btn btn-sm" (click)="replay(d, false)">Retry</button>
                      } @else if (d.status === 'sent' || d.status === 'captured') {
                        <button class="btn btn-ghost btn-sm" (click)="replay(d, true)">Send again</button>
                      }
                    </td>
                  </tr>
                } @empty {
                  <tr><td colspan="6" class="text-muted">No deliveries{{ status() ? ' with this status' : '' }}.</td></tr>
                }
              </tbody>
            </table>
          </div>
        }
        @case ('preview') {
          <form class="card-item mb-4 flex flex-wrap items-end gap-3" (ngSubmit)="runPreview()" aria-label="Preview a route">
            <label class="field-row min-w-[240px] flex-1">Route
              <select class="input" name="route" [(ngModel)]="previewRoute" required>
                @for (r of routes(); track r.route) { <option [value]="r.route">{{ r.label }}</option> }
              </select>
            </label>
            <label class="field-row min-w-[280px] flex-1">Record ID (optional) <input class="input mono" name="record" [(ngModel)]="previewRecord" placeholder="00000000-0000-0000-0000-000000000000" /></label>
            <button class="btn btn-primary" type="submit" [disabled]="!previewRoute">Preview</button>
          </form>
          @if (preview(); as p) {
            <p class="mb-2 text-body-sm text-muted">Nothing is sent. {{ p.mandatory ? 'This route is mandatory: people cannot mute it.' : '' }} {{ p.note ?? '' }}</p>
            <ul class="flex flex-col gap-3">
              @for (r of p.recipients; track $index) {
                <li class="card-item">
                  <div class="flex flex-wrap items-center gap-2"><strong>{{ r.recipient }}</strong>
                    @if (r.external) { <span class="badge tone-amber">external</span> }
                    @for (c of r.channels; track c) { <span class="badge tone-gray">{{ c }}</span> }
                    @if (r.suppressed) { <span class="badge tone-gray">not sent: {{ r.suppressed }}</span> }
                  </div>
                  <p class="mt-2 font-semibold">{{ r.subject }}</p>
                  <p class="text-body-sm whitespace-pre-line text-muted">{{ r.body }}</p>
                </li>
              } @empty {
                <li class="text-muted">Nobody would be notified.</li>
              }
            </ul>
          }
        }
      }
    </div>
  `,
  styles: `
    .pill { display: inline-flex; align-items: center; height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .pill.on { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); border-color: var(--esm-accent-brand); }
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); }
    .field-row { display: grid; gap: 4px; font: var(--esm-text-body-sm); font-weight: 500; }
    summary { cursor: pointer; }
  `,
})
export class NotificationsAdminPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  protected readonly tabs = [
    { id: 'channels' as const, label: 'Channels' },
    { id: 'deliveries' as const, label: 'Deliveries' },
    { id: 'preview' as const, label: 'Preview a route' },
  ];
  protected readonly statuses = ['queued', 'sending', 'sent', 'captured', 'suppressed', 'failed', 'ambiguous'];
  protected readonly tab = signal<'channels' | 'deliveries' | 'preview'>('channels');
  protected readonly error = signal<string | null>(null);
  protected readonly channels = signal<Channel[]>([]);
  protected readonly deliveries = signal<Delivery[]>([]);
  protected readonly routes = signal<{ route: string; label: string }[]>([]);
  protected readonly preview = signal<Preview | null>(null);
  protected readonly status = signal('');
  protected readonly editing = signal(false);
  protected form: ChannelForm = blankForm();
  protected previewRoute = '';
  protected previewRecord = '';

  constructor() {
    void this.loadChannels();
  }

  protected open(tab: 'channels' | 'deliveries' | 'preview'): void {
    this.tab.set(tab);
    this.error.set(null);
    if (tab === 'deliveries') void this.loadDeliveries();
    if (tab === 'preview') void this.loadRoutes();
  }

  protected keys(o: Record<string, unknown>): string[] {
    return Object.keys(o);
  }

  protected tone(status: string): string {
    return STATUS_TONES[status] ?? 'tone-gray';
  }

  private async run<T>(work: () => Promise<T>, fallback: string): Promise<T | undefined> {
    try {
      const r = await work();
      this.error.set(null);
      return r;
    } catch (e) {
      this.error.set(e instanceof ApiError ? `${e.message}${e.problem.suggestion ? ' ' + e.problem.suggestion : ''}` : fallback);
      return undefined;
    }
  }

  private async loadChannels(): Promise<void> {
    const list = await this.run(() => this.api.get<Channel[]>('/api/admin/notifications/channels'), 'Could not load channels.');
    if (list) this.channels.set(list);
  }

  protected async loadDeliveries(): Promise<void> {
    const list = await this.run(() => this.api.get<Delivery[]>('/api/admin/notifications/deliveries', { status: this.status() || undefined }), 'Could not load deliveries.');
    if (list) this.deliveries.set(list);
  }

  private async loadRoutes(): Promise<void> {
    const list = await this.run(() => this.api.get<{ route: string; label: string }[]>('/api/me/notification-preferences'), 'Could not load routes.');
    if (list) {
      this.routes.set(list);
      this.previewRoute ||= list[0]?.route ?? '';
    }
  }

  protected edit(c: Channel): void {
    this.editing.set(true);
    this.form = {
      key: c.key,
      type: c.type,
      label: c.label,
      mode: c.mode,
      host: c.settings['host'] ?? '',
      port: String(c.settings['port'] ?? ''),
      from: c.settings['from'] ?? '',
      accountSid: c.settings['accountSid'] ?? '',
      secretRefs: Object.keys(c.secrets).join(', '),
      ratePerMinute: c.ratePerMinute,
    };
  }

  protected reset(): void {
    this.editing.set(false);
    this.form = blankForm();
  }

  protected async save(): Promise<void> {
    const f = this.form;
    const settings: Record<string, unknown> = { from: f.from || undefined, host: f.host || undefined };
    if (f.type === 'email' && f.port) settings['port'] = Number(f.port);
    if (f.type === 'sms' && f.accountSid) settings['accountSid'] = f.accountSid;
    const body = {
      key: f.key,
      type: f.type,
      label: f.label || f.key,
      mode: f.mode,
      settings: JSON.parse(JSON.stringify(settings)),
      secretRefs: f.secretRefs.split(',').map((s) => s.trim()).filter((s) => !!s),
      ratePerMinute: f.ratePerMinute,
    };
    const saved = await this.run(() => this.api.put<Channel>(`/api/admin/notifications/channels/${encodeURIComponent(f.key)}`, body), 'The channel was not saved.');
    if (saved) {
      this.toasts.show(`Channel "${saved.label}" saved.`, 'success');
      this.reset();
      await this.loadChannels();
    }
  }

  protected async test(c: Channel): Promise<void> {
    const r = await this.run(() => this.api.post<{ delivery: string }>(`/api/admin/notifications/channels/${encodeURIComponent(c.key)}/test`), 'The test was not sent.');
    if (r) this.toasts.show(c.mode === 'capture' ? 'Test captured: see Deliveries.' : 'Test queued to your email address.', 'success');
  }

  protected async replay(d: Delivery, force: boolean): Promise<void> {
    if (force && !confirm(`Send "${d.subject}" again? The recipient gets it twice.`)) return;
    const r = await this.run(() => this.api.post<{ delivery: string }>(`/api/admin/notifications/deliveries/${d.id}/replay${force ? '?force=true' : ''}`), 'Could not replay it.');
    if (r) {
      this.toasts.show(force ? 'Sent again.' : 'Queued for another try.', 'success');
      await this.loadDeliveries();
    }
  }

  protected async runPreview(): Promise<void> {
    const p = await this.run(() => this.api.get<Preview>('/api/admin/notifications/preview', { route: this.previewRoute, record: this.previewRecord.trim() || undefined }), 'Could not preview it.');
    if (p) this.preview.set(p);
  }
}

function blankForm(): ChannelForm {
  return { key: 'email', type: 'email', label: 'Email', mode: 'capture', host: '', port: '', from: '', accountSid: '', secretRefs: '', ratePerMinute: 60 };
}
