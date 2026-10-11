import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface InboundMessage {
  id: string;
  inbox: string;
  from?: string | null;
  fromName?: string | null;
  subject?: string | null;
  status: 'received' | 'processed' | 'suspended' | 'rejected' | 'failed';
  reason?: string | null;
  outcome?: string | null;
  recordId?: string | null;
  entityId?: string | null;
  receivedAt: string;
  processedAt?: string | null;
}

interface MessageDetail {
  message: InboundMessage;
  text?: string | null;
  to: string[];
  cc: string[];
  verification?: string | null;
  autoSignals: string[];
  attachments: { name?: string | null; contentType: string; size: number }[];
}

interface Mailbox {
  key: string;
  label: string;
  address: string;
  intake: string;
  mode: 'webhook' | 'imap' | 'disabled';
  settings: Record<string, string | number | undefined>;
  hasToken: boolean;
  secrets: Record<string, boolean>;
  hostAllowed: boolean;
  polledAt?: string | null;
  pollError?: string | null;
  version: number;
}

interface Member {
  id: string;
  displayName: string;
}

const REASONS: Record<string, string> = {
  auto_reply: 'Automatic reply or bulk mail',
  own_message: 'Sent by this workspace',
  too_many_from_sender: 'Too many messages from this sender in an hour',
  unverified_sender: 'Sender could not be verified',
  unknown_sender: 'Sender is not a member of this workspace',
  not_allowed: 'Sender may not do this',
  bad_sender: 'No usable sender address',
  empty: 'No subject or text',
  no_intake: 'Mailbox is not connected to an intake',
  invalid_record: 'The ticket would not be valid',
  unreadable: 'Not a readable email',
  processing_failed: 'Processing kept failing',
  rejected: 'Rejected by a reviewer',
};

const OUTCOMES: Record<string, string> = { created: 'opened a ticket', replied: 'added a reply', reopened: 'reopened the ticket', follow_up: 'opened a follow-up' };

/**
 * Inbound email (ADR 0023, REQ-BIZ-003): messages a mailbox suspended (unverified, unknown or too frequent senders)
 * wait here for people who work the tickets to accept or reject; managers set up mailboxes. The server decides who
 * sees and may do what, and processes accepted messages with the sender's own permissions.
 */
@Component({
  selector: 'esm-mail-page',
  imports: [DatePipe, FormsModule, RouterLink, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="mail" [size]="18" /></span>
        <div class="flex-1">
          <h1 class="text-title-lg">Inbound email</h1>
          <p class="section-lead">Email to a workspace mailbox opens tickets and adds replies. Messages that need a person's judgment wait here.</p>
        </div>
      </header>

      <div class="mb-4 flex flex-wrap gap-2" role="tablist" aria-label="Messages">
        @for (t of tabs(); track t.id) {
          <button class="pill" role="tab" [attr.aria-selected]="tab() === t.id" [class.on]="tab() === t.id" (click)="open(t.id)">{{ t.label }}</button>
        }
      </div>

      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }

      @if (tab() === 'mailboxes') {
        @if (token()) {
          <div class="callout tone-amber mb-3" role="status">
            <p><strong>Webhook token for {{ token()!.key }}</strong>: copy it now, it is shown only once. Send messages to <span class="mono">POST /api/mail/inbound</span> with <span class="mono">Authorization: Bearer &lt;token&gt;</span>.</p>
            <p class="mono mt-1 break-all">{{ token()!.value }}</p>
          </div>
        }
        <div class="grid grid-cols-[minmax(0,1fr)_380px] items-start gap-6 max-[960px]:grid-cols-1">
          <ul class="flex flex-col gap-3" aria-label="Mailboxes">
            @for (b of mailboxes(); track b.key) {
              <li class="card-item">
                <div class="flex flex-wrap items-center gap-2">
                  <strong>{{ b.label }}</strong>
                  <span class="mono text-body-sm">{{ b.address }}</span>
                  <span class="badge" [class]="b.mode === 'disabled' ? 'tone-gray' : 'tone-green'">{{ b.mode }}</span>
                  <span class="flex-1"></span>
                  <button class="btn btn-ghost btn-sm" (click)="edit(b)">Edit</button>
                  @if (b.mode === 'webhook') { <button class="btn btn-ghost btn-sm" (click)="rotate(b)">New token</button> }
                </div>
                <p class="mt-1 text-body-sm text-muted">
                  {{ b.intake }} · sender verification {{ b.settings['senderVerification'] ?? 'required' }}{{ b.settings['authservId'] ? ' by ' + b.settings['authservId'] : '' }}
                  @if (b.mode === 'imap') { · {{ b.settings['host'] }}{{ b.polledAt ? ', checked ' + (b.polledAt | date: 'MMM d, HH:mm') : '' }} }
                </p>
                @if (b.pollError) { <p class="callout tone-red mt-2 text-body-sm">Last check failed: {{ b.pollError }}</p> }
                @if (b.mode === 'imap' && !b.hostAllowed) { <p class="callout tone-red mt-2 text-body-sm">The IMAP server is not on the platform's allow-list.</p> }
                @for (s of secretNames(b); track s) {
                  <span class="badge mr-1 mt-1" [class]="b.secrets[s] ? 'tone-green' : 'tone-red'">{{ s }} {{ b.secrets[s] ? 'set' : 'missing' }}</span>
                }
              </li>
            } @empty {
              <li class="rounded-lg border border-dashed border-strong p-6 text-center text-muted">No mailboxes yet.</li>
            }
          </ul>
          <form class="card-item grid gap-3" (ngSubmit)="save()" aria-label="Mailbox settings">
            <h2 class="text-title-sm">{{ form.version ? 'Edit mailbox' : 'New mailbox' }}</h2>
            <label class="field-row">Key <input class="input mono" name="key" [(ngModel)]="form.key" required pattern="[a-z][a-z0-9_]{1,40}" [readonly]="!!form.version" /></label>
            <label class="field-row">Name <input class="input" name="label" [(ngModel)]="form.label" required maxlength="100" /></label>
            <label class="field-row">Address <input class="input" type="email" name="address" [(ngModel)]="form.address" required placeholder="support@example.com" /></label>
            <label class="field-row">Intake
              <select class="input" name="intake" [(ngModel)]="form.intake" required>
                @for (i of intakes(); track i.id) { <option [value]="i.id">{{ i.label }}{{ i.enabled ? '' : ' (off)' }}</option> }
              </select>
            </label>
            <label class="field-row">How mail arrives
              <select class="input" name="mode" [(ngModel)]="form.mode">
                <option value="webhook">Webhook (your mail provider posts each message)</option>
                <option value="imap">IMAP (the platform checks the mailbox)</option>
                <option value="disabled">Disabled</option>
              </select>
            </label>
            <label class="field-row">Sender verification
              <select class="input" name="verification" [(ngModel)]="form.senderVerification">
                <option value="required">Required (trusted Authentication-Results)</option>
                <option value="none">None (anyone can claim any sender)</option>
              </select>
            </label>
            @if (form.senderVerification === 'required') {
              <label class="field-row">Trusted mail server (authserv-id) <input class="input mono" name="authserv" [(ngModel)]="form.authservId" placeholder="mx.example.com" /></label>
            }
            <label class="field-row">Messages per sender and hour <input class="input w-32" type="number" name="max" min="1" max="500" [(ngModel)]="form.maxPerSenderPerHour" /></label>
            @if (form.mode === 'imap') {
              <label class="field-row">IMAP server <input class="input mono" name="host" [(ngModel)]="form.host" /></label>
              <label class="field-row">Port <input class="input w-32" type="number" name="port" [(ngModel)]="form.port" /></label>
              <label class="field-row">Folder <input class="input mono" name="folder" [(ngModel)]="form.folder" /></label>
              <label class="field-row">User name secret <input class="input mono" name="userRef" [(ngModel)]="form.usernameRef" placeholder="IMAP_USERNAME" /></label>
              <label class="field-row">Password secret <input class="input mono" name="passRef" [(ngModel)]="form.passwordRef" placeholder="IMAP_PASSWORD" /></label>
              <p class="text-caption text-muted">Secrets are names of environment variables on the server, never values.</p>
            }
            <div class="flex justify-end gap-2">
              @if (form.version) { <button class="btn btn-ghost" type="button" (click)="reset()">Cancel</button> }
              <button class="btn btn-primary" type="submit">Save mailbox</button>
            </div>
          </form>
        </div>
      } @else if (!messages()) {
        <div class="flex flex-col gap-3" aria-busy="true">@for (i of [1, 2, 3]; track i) { <div class="card-item"><span class="skeleton block h-4 w-1/2"></span></div> }</div>
      } @else {
        <ul class="flex flex-col gap-3" aria-label="Messages">
          @for (m of messages()!; track m.id) {
            <li class="card-item">
              <div class="flex flex-wrap items-center gap-2">
                <span class="badge" [class]="statusTone(m.status)">{{ m.status }}</span>
                <span class="title">{{ m.subject || '(no subject)' }}</span>
              </div>
              <p class="mt-1 text-body-sm text-muted">
                From {{ m.fromName ? m.fromName + ' ' : '' }}&lt;{{ m.from ?? 'unknown' }}&gt; · {{ m.inbox }} · {{ m.receivedAt | date: 'MMM d, HH:mm' }}
                @if (m.reason) { · {{ reason(m.reason) }} }
                @if (m.outcome) { · {{ outcome(m.outcome) }} }
              </p>
              @if (m.recordId && m.entityId) { <a class="mt-1 inline-block text-body-sm" [routerLink]="['/records', m.entityId, m.recordId]">Open the ticket</a> }
              <div class="mt-2 flex flex-wrap items-center gap-2">
                <button class="btn btn-ghost btn-sm" (click)="toggle(m)" [attr.aria-expanded]="openId() === m.id">{{ openId() === m.id ? 'Hide' : 'Read' }}</button>
                @if (m.status === 'suspended') {
                  @if (m.reason === 'unknown_sender') {
                    <label class="flex items-center gap-2 text-body-sm">On behalf of
                      <select class="input" [attr.aria-label]="'Requester for ' + (m.subject || 'message')" [(ngModel)]="requester[m.id]">
                        <option [ngValue]="undefined">Choose a person</option>
                        @for (p of people(); track p.id) { <option [ngValue]="p.id">{{ p.displayName }}</option> }
                      </select>
                    </label>
                  }
                  <button class="btn btn-primary btn-sm" (click)="accept(m)" [disabled]="m.reason === 'unknown_sender' && !requester[m.id]">Accept</button>
                  <button class="btn btn-sm" (click)="reject(m)">Reject</button>
                }
              </div>
              @if (openId() === m.id && detail()) {
                <div class="mt-2 grid gap-1 text-body-sm">
                  <p class="text-muted">To {{ detail()!.to.join(', ') || '—' }}{{ detail()!.cc.length ? ' · Cc ' + detail()!.cc.join(', ') : '' }}{{ detail()!.verification ? ' · ' + detail()!.verification : '' }}</p>
                  @if (detail()!.autoSignals.length) { <p class="text-muted">Automatic mail signs: {{ detail()!.autoSignals.join(', ') }}</p> }
                  <pre class="message">{{ detail()!.text ?? 'The text is no longer kept.' }}</pre>
                  @if (detail()!.attachments.length) {
                    <p class="text-muted">Attachments (not imported): @for (a of detail()!.attachments; track $index) { {{ a.name ?? 'unnamed' }} ({{ a.contentType }}){{ $last ? '' : ', ' }} }</p>
                  }
                </div>
              }
            </li>
          } @empty {
            <li class="rounded-lg border border-dashed border-strong p-8 text-center text-muted">{{ tab() === 'suspended' ? 'Nothing waiting for review.' : 'No messages.' }}</li>
          }
        </ul>
      }
    </div>
  `,
  styles: `
    .pill { display: inline-flex; align-items: center; height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .pill.on { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); border-color: var(--esm-accent-brand); }
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    .title { font: var(--esm-text-body); font-weight: 600; color: var(--esm-text-default); overflow-wrap: anywhere; }
    .field-row { display: grid; gap: 4px; font: var(--esm-text-body-sm); font-weight: 500; }
    .message { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; font: var(--esm-text-body-sm); padding: var(--esm-space-3); border-radius: var(--esm-radius-md); background: var(--esm-surface-sunken); border: 1px solid var(--esm-border-subtle); }
  `,
})
export class MailPage {
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly toasts = inject(Toasts);

  protected readonly canReview = computed(() => !!this.session.app()?.mail);
  protected readonly canManage = computed(() => this.session.canManageNotifications());
  protected readonly tabs = computed(() => [
    ...(this.canReview()
      ? [
          { id: 'suspended' as const, label: 'Waiting for review' },
          { id: 'processed' as const, label: 'Processed' },
          { id: 'rejected' as const, label: 'Rejected' },
          { id: 'failed' as const, label: 'Failed' },
        ]
      : []),
    ...(this.canManage() ? [{ id: 'mailboxes' as const, label: 'Mailboxes' }] : []),
  ]);
  protected readonly tab = signal<'suspended' | 'processed' | 'rejected' | 'failed' | 'mailboxes'>('suspended');
  protected readonly messages = signal<InboundMessage[] | null>(null);
  protected readonly mailboxes = signal<Mailbox[]>([]);
  protected readonly people = signal<Member[]>([]);
  protected readonly intakes = signal<{ id: string; label: string; enabled: boolean }[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly openId = signal<string | null>(null);
  protected readonly detail = signal<MessageDetail | null>(null);
  protected readonly token = signal<{ key: string; value: string } | null>(null);
  protected requester: Record<string, string | undefined> = {};
  protected form = this.blank();

  constructor() {
    this.open(this.canReview() ? 'suspended' : 'mailboxes');
  }

  protected reason(code: string): string {
    return REASONS[code] ?? code;
  }

  protected outcome(code: string): string {
    return OUTCOMES[code] ?? code;
  }

  protected statusTone(s: string): string {
    return s === 'suspended' ? 'tone-amber' : s === 'processed' ? 'tone-green' : s === 'rejected' || s === 'failed' ? 'tone-red' : 'tone-gray';
  }

  protected secretNames(b: Mailbox): string[] {
    return Object.keys(b.secrets);
  }

  protected open(tab: 'suspended' | 'processed' | 'rejected' | 'failed' | 'mailboxes'): void {
    this.tab.set(tab);
    this.openId.set(null);
    if (tab === 'mailboxes') void this.loadMailboxes();
    else void this.load();
  }

  private async load(): Promise<void> {
    this.messages.set(null);
    try {
      this.messages.set(await this.api.get<InboundMessage[]>('/api/mail/messages', { status: this.tab() }));
      this.error.set(null);
      if (this.tab() === 'suspended' && this.people().length === 0) {
        const people = await this.api.get<{ id: string; name: string }[]>('/api/directory/people').catch(() => []);
        this.people.set(people.map((p) => ({ id: p.id, displayName: p.name })));
      }
    } catch (e) {
      this.messages.set([]);
      this.error.set(e instanceof ApiError ? e.message : 'Could not load messages.');
    }
  }

  protected async toggle(m: InboundMessage): Promise<void> {
    if (this.openId() === m.id) {
      this.openId.set(null);
      return;
    }
    try {
      this.detail.set(await this.api.get<MessageDetail>(`/api/mail/messages/${m.id}`));
      this.openId.set(m.id);
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not read the message.', 'warning');
    }
  }

  protected async accept(m: InboundMessage): Promise<void> {
    try {
      const done = await this.api.post<InboundMessage>(`/api/mail/messages/${m.id}/accept`, { requester: this.requester[m.id] });
      this.toasts.show(done.status === 'processed' ? `Accepted: ${OUTCOMES[done.outcome ?? ''] ?? 'processed'}.` : `Still waiting: ${this.reason(done.reason ?? '')}.`, done.status === 'processed' ? 'success' : 'warning');
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The message was not accepted.', 'warning');
    }
  }

  protected async reject(m: InboundMessage): Promise<void> {
    try {
      await this.api.post(`/api/mail/messages/${m.id}/reject`);
      this.toasts.show('Rejected.', 'success');
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The message was not rejected.', 'warning');
    }
  }

  private async loadMailboxes(): Promise<void> {
    try {
      this.mailboxes.set(await this.api.get<Mailbox[]>('/api/admin/mail/inboxes'));
      this.intakes.set(await this.api.get<{ id: string; label: string; enabled: boolean }[]>('/api/admin/mail/intakes'));
      if (!this.form.intake && this.intakes().length > 0) this.form.intake = this.intakes()[0].id;
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load mailboxes.');
    }
  }

  private blank() {
    return {
      key: '', label: '', address: '', intake: this.intakes?.()[0]?.id ?? '', mode: 'webhook' as Mailbox['mode'], version: 0,
      senderVerification: 'required', authservId: '', maxPerSenderPerHour: 20, host: '', port: 993, folder: 'INBOX', usernameRef: '', passwordRef: '',
    };
  }

  protected reset(): void {
    this.form = this.blank();
  }

  protected edit(b: Mailbox): void {
    const s = b.settings;
    this.form = {
      key: b.key, label: b.label, address: b.address, intake: b.intake, mode: b.mode, version: b.version,
      senderVerification: String(s['senderVerification'] ?? 'required'), authservId: String(s['authservId'] ?? ''), maxPerSenderPerHour: Number(s['maxPerSenderPerHour'] ?? 20),
      host: String(s['host'] ?? ''), port: Number(s['port'] ?? 993), folder: String(s['folder'] ?? 'INBOX'), usernameRef: String(s['usernameRef'] ?? ''), passwordRef: String(s['passwordRef'] ?? ''),
    };
  }

  protected async save(): Promise<void> {
    const f = this.form;
    const settings: Record<string, string | number> = { senderVerification: f.senderVerification, maxPerSenderPerHour: Number(f.maxPerSenderPerHour) || 20 };
    if (f.senderVerification === 'required' && f.authservId.trim()) settings['authservId'] = f.authservId.trim();
    if (f.mode === 'imap') {
      Object.assign(settings, { host: f.host.trim(), port: Number(f.port) || 993, folder: f.folder.trim() || 'INBOX' });
      if (f.usernameRef.trim()) settings['usernameRef'] = f.usernameRef.trim();
      if (f.passwordRef.trim()) settings['passwordRef'] = f.passwordRef.trim();
    }
    try {
      const saved = await this.api.put<{ inbox: Mailbox; token?: string | null }>(`/api/admin/mail/inboxes/${encodeURIComponent(f.key.trim())}`, {
        label: f.label.trim(), address: f.address.trim(), intake: f.intake.trim(), mode: f.mode, settings, version: f.version || undefined,
      });
      if (saved.token) this.token.set({ key: saved.inbox.key, value: saved.token });
      this.toasts.show('Mailbox saved.', 'success');
      this.reset();
      await this.loadMailboxes();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The mailbox was not saved.', 'warning');
    }
  }

  protected async rotate(b: Mailbox): Promise<void> {
    try {
      const saved = await this.api.post<{ inbox: Mailbox; token?: string | null }>(`/api/admin/mail/inboxes/${encodeURIComponent(b.key)}/rotate-token`);
      if (saved.token) this.token.set({ key: b.key, value: saved.token });
      this.toasts.show('New token created; the old one no longer works.', 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'No new token was created.', 'warning');
    }
  }
}
