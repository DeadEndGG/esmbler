import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface InboxItem {
  id: string;
  subject: string;
  body: string;
  createdAt: string;
  readAt?: string | null;
  severity?: string | null;
  entity?: string | null;
  recordId?: string | null;
}

interface InboxPage {
  items: InboxItem[];
  unread: number;
}

interface Preference {
  route: string;
  label: string;
  subscription: string;
  channels: string[];
  mandatory: boolean;
  muted: string[];
}

const CHANNEL_LABELS: Record<string, string> = { in_app: 'In the app', email: 'Email', sms: 'Text message' };

/**
 * The signed-in person's notifications (ADR 0021, REQ-NOT-004): their inbox with read state and links to records, and
 * which notifications they receive. Mandatory notifications cannot be muted; the server enforces both.
 */
@Component({
  selector: 'esm-notifications-page',
  imports: [DatePipe, RouterLink, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="bell" [size]="18" /></span>
        <div class="flex-1">
          <h1 class="text-title-lg">Notifications</h1>
          <p class="section-lead">What happened on records you follow. Only what you may see is shown.</p>
        </div>
      </header>

      <div class="mb-4 flex flex-wrap items-center gap-2" role="tablist" aria-label="Notifications">
        <button class="pill" role="tab" [attr.aria-selected]="tab() === 'inbox'" [class.on]="tab() === 'inbox'" (click)="tab.set('inbox')">
          Inbox <span class="count">{{ page()?.unread ?? 0 }}</span>
        </button>
        <button class="pill" role="tab" [attr.aria-selected]="tab() === 'settings'" [class.on]="tab() === 'settings'" (click)="openSettings()">What I receive</button>
        <span class="flex-1"></span>
        @if (tab() === 'inbox') {
          <label class="flex items-center gap-2 text-body-sm"><input type="checkbox" [checked]="unreadOnly()" (change)="toggleUnread()" /> Unread only</label>
          <button class="btn btn-sm" (click)="readAll()" [disabled]="!page()?.unread">Mark all read</button>
        }
      </div>

      @if (error()) {
        <div class="callout tone-red mb-3" role="alert">{{ error() }}</div>
      }

      @if (tab() === 'inbox') {
        @if (!page()) {
          <div class="flex flex-col gap-3" aria-busy="true">@for (i of [1, 2, 3]; track i) { <div class="item"><span class="skeleton block h-4 w-1/2"></span></div> }</div>
        } @else {
          <ul class="flex flex-col gap-2" aria-label="Inbox">
            @for (n of page()!.items; track n.id) {
              <li class="item" [class.unread]="!n.readAt">
                <div class="flex items-start gap-3">
                  <span class="dot" [class.on]="!n.readAt" [attr.aria-label]="n.readAt ? null : 'Unread'"></span>
                  <div class="min-w-0 flex-1">
                    <div class="flex flex-wrap items-baseline gap-2">
                      @if (n.entity && n.recordId) {
                        <a class="title" [routerLink]="['/records', n.entity, n.recordId]" (click)="markRead(n)">{{ n.subject }}</a>
                      } @else {
                        <span class="title">{{ n.subject }}</span>
                      }
                      @if (n.severity === 'high' || n.severity === 'critical') { <span class="badge tone-red">{{ n.severity }}</span> }
                      <span class="text-caption text-muted">{{ n.createdAt | date: 'MMM d, HH:mm' }}</span>
                    </div>
                    <p class="body">{{ n.body }}</p>
                  </div>
                  @if (!n.readAt) { <button class="btn btn-ghost btn-sm" (click)="markRead(n)" [attr.aria-label]="'Mark read: ' + n.subject">Mark read</button> }
                </div>
              </li>
            } @empty {
              <li class="rounded-lg border border-dashed border-strong p-8 text-center text-muted">{{ unreadOnly() ? 'Nothing unread.' : 'No notifications yet.' }}</li>
            }
          </ul>
          @if (page()!.items.length >= 30) { <div class="mt-3 flex justify-center"><button class="btn btn-sm" (click)="more()">Older</button></div> }
        }
      } @else {
        <div class="table-card">
          <table>
            <caption class="sr-only">Notifications you receive</caption>
            <thead><tr><th scope="col">Notification</th><th scope="col">Event</th><th scope="col">Channels</th></tr></thead>
            <tbody>
              @for (p of prefs(); track p.route) {
                <tr>
                  <th scope="row">{{ p.label }} @if (p.mandatory) { <span class="badge tone-gray">Required</span> }</th>
                  <td>{{ p.subscription }}</td>
                  <td>
                    <div class="flex flex-wrap gap-3">
                      @for (c of p.channels; track c) {
                        <label class="flex items-center gap-1.5 text-body-sm" [class.text-muted]="p.mandatory">
                          <input type="checkbox" [checked]="!isMuted(p, c)" [disabled]="p.mandatory || isMuted(p, '*')" (change)="toggle(p, c)" />
                          {{ channelLabel(c) }}
                        </label>
                      }
                      <label class="flex items-center gap-1.5 text-body-sm" [class.text-muted]="p.mandatory">
                        <input type="checkbox" [checked]="isMuted(p, '*')" [disabled]="p.mandatory" (change)="toggle(p, '*')" /> Mute all
                      </label>
                    </div>
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="3" class="text-muted">This workspace sends no configurable notifications yet.</td></tr>
              }
            </tbody>
          </table>
        </div>
      }
    </div>
  `,
  styles: `
    .pill { display: inline-flex; align-items: center; gap: 6px; height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .pill.on { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); border-color: var(--esm-accent-brand); }
    .count { min-width: 18px; padding: 0 5px; border-radius: var(--esm-radius-full); background: color-mix(in srgb, currentColor 18%, transparent); font: var(--esm-text-caption); font-weight: 600; text-align: center; }
    .item { padding: var(--esm-space-3) var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    .item.unread { box-shadow: var(--esm-shadow-card); }
    .title { font: var(--esm-text-body); font-weight: 600; color: var(--esm-text-default); }
    .body { margin: 4px 0 0; font: var(--esm-text-body-sm); color: var(--esm-text-muted); white-space: pre-line; overflow-wrap: anywhere; }
    .dot { flex: none; width: 8px; height: 8px; margin-top: 7px; border-radius: 50%; background: transparent; }
    .dot.on { background: var(--esm-accent-brand); }
  `,
})
export class NotificationsPage {
  private readonly api = inject(Api);
  private readonly session = inject(Session);
  private readonly toasts = inject(Toasts);

  protected readonly tab = signal<'inbox' | 'settings'>('inbox');
  protected readonly page = signal<InboxPage | null>(null);
  protected readonly prefs = signal<Preference[]>([]);
  protected readonly unreadOnly = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly oldest = computed(() => this.page()?.items.at(-1)?.createdAt);

  constructor() {
    void this.load();
  }

  private async load(before?: string): Promise<void> {
    try {
      const res = await this.api.get<InboxPage>('/api/me/notifications', { unread: this.unreadOnly() ? 'true' : undefined, before });
      this.page.set(before && this.page() ? { items: [...this.page()!.items, ...res.items], unread: res.unread } : res);
      this.session.unreadNotifications.set(res.unread);
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load notifications.');
    }
  }

  protected more(): void {
    void this.load(this.oldest());
  }

  protected toggleUnread(): void {
    this.unreadOnly.update((v) => !v);
    this.page.set(null);
    void this.load();
  }

  protected async markRead(n: InboxItem): Promise<void> {
    if (n.readAt) return;
    try {
      await this.api.post(`/api/me/notifications/${n.id}/read`);
      const now = new Date().toISOString();
      this.page.update((p) => (p ? { items: p.items.map((x) => (x.id === n.id ? { ...x, readAt: now } : x)), unread: Math.max(0, p.unread - 1) } : p));
      this.session.unreadNotifications.update((u) => Math.max(0, u - 1));
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not mark it read.', 'warning');
    }
  }

  protected async readAll(): Promise<void> {
    try {
      await this.api.post('/api/me/notifications/read-all');
      this.session.unreadNotifications.set(0);
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not mark them read.', 'warning');
    }
  }

  protected async openSettings(): Promise<void> {
    this.tab.set('settings');
    try {
      this.prefs.set(await this.api.get<Preference[]>('/api/me/notification-preferences'));
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load your settings.');
    }
  }

  protected isMuted(p: Preference, channel: string): boolean {
    return p.muted.includes(channel);
  }

  protected channelLabel(c: string): string {
    return CHANNEL_LABELS[c] ?? c;
  }

  /** Optimistic toggle with rollback (the server refuses muting mandatory notifications). */
  protected async toggle(p: Preference, channel: string): Promise<void> {
    const muted = !this.isMuted(p, channel);
    const before = this.prefs();
    this.prefs.update((list) => list.map((x) => (x.route === p.route ? { ...x, muted: muted ? [...x.muted, channel] : x.muted.filter((m) => m !== channel) } : x)));
    try {
      await this.api.put('/api/me/notification-preferences', { route: p.route, channel, muted });
    } catch (e) {
      this.prefs.set(before);
      this.toasts.show(e instanceof ApiError ? e.message : 'Your setting was not saved.', 'warning');
    }
  }
}
