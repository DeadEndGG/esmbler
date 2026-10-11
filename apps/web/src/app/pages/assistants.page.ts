import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { Icon } from '../ui/icon';

interface Connection {
  id: string;
  clientName: string;
  clientHost: string;
  person: string;
  environment: string;
  scopes: string[];
  createdAt: string;
  expiresAt: string;
  lastUsedAt?: string | null;
  revokedAt?: string | null;
}

/**
 * Connected assistants (ADR 0025): the external assistants (ChatGPT, Claude) a person connected to this workspace, with
 * where and what they may do, and a way to end each connection. Permission administrators see everyone's.
 */
@Component({
  selector: 'esm-assistants-page',
  imports: [DatePipe, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="sparkles" [size]="18" /></span>
        <div class="flex-1">
          <h1 class="text-title-lg">Connected assistants</h1>
          <p class="section-lead">Assistants from other apps that may work in this workspace's drafts on someone's behalf. End a connection at any time.</p>
        </div>
        @if (session.isAdmin()) {
          <label class="flex items-center gap-2 text-body-sm"><input type="checkbox" [checked]="all()" (change)="toggleAll()" /> Everyone's</label>
        }
      </header>
      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }
      <ul class="flex flex-col gap-3" aria-label="Connections">
        @for (c of connections(); track c.id) {
          <li class="card-item">
            <div class="flex flex-wrap items-center gap-2">
              <strong>{{ c.clientName }}</strong> <span class="text-body-sm text-muted">{{ c.clientHost }}</span>
              <span class="badge" [class]="c.revokedAt ? 'tone-gray' : 'tone-green'">{{ c.revokedAt ? 'ended' : 'active' }}</span>
              <span class="flex-1"></span>
              @if (!c.revokedAt) { <button class="btn btn-sm" (click)="revoke(c)">End connection</button> }
            </div>
            <p class="mt-1 text-body-sm text-muted">
              {{ all() ? c.person + ' · ' : '' }}{{ c.environment }} · {{ c.scopes.join(', ') }} · connected {{ c.createdAt | date: 'MMM d, y' }}{{ c.lastUsedAt ? ' · last used ' + (c.lastUsedAt | date: 'MMM d, HH:mm') : '' }}
            </p>
          </li>
        } @empty {
          <li class="rounded-lg border border-dashed border-strong p-8 text-center text-muted">No assistants are connected.</li>
        }
      </ul>
    </div>
  `,
  styles: `
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
  `,
})
export class AssistantsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly session = inject(Session);

  protected readonly connections = signal<Connection[]>([]);
  protected readonly all = signal(false);
  protected readonly error = signal<string | null>(null);

  constructor() {
    void this.load();
  }

  protected toggleAll(): void {
    this.all.update((v) => !v);
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      this.connections.set(await this.api.get<Connection[]>(this.all() ? '/api/admin/connections' : '/api/me/connections'));
      this.error.set(null);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Could not load connections.');
    }
  }

  protected async revoke(c: Connection): Promise<void> {
    try {
      await this.api.post(`/api/connections/${c.id}/revoke`);
      this.toasts.show(`${c.clientName} can no longer work in this workspace.`, 'success');
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The connection was not ended.', 'warning');
    }
  }
}
