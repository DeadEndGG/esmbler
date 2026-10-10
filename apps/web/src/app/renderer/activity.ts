import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { Api } from '../core/api';
import { ActivityView, HistoryEntry, NodeSpec } from '../core/models';
import { Toasts } from '../core/toast';
import { RenderContext } from './render-context';
import { Icon } from '../ui/icon';

/** activity.feed: public replies and internal work notes (REQ-BIZ-002). Requesters never receive work notes. */
@Component({
  selector: 'esm-activity-feed',
  imports: [DatePipe, Icon],
  template: `
    <div class="feed">
      @if (ctx.record()?.access?.canUpdate || canReply()) {
        <div class="composer" [class.internal]="kind() === 'work_note'">
          <div class="kinds" role="radiogroup" aria-label="Message type">
            @if (canNote()) {
              <button role="radio" [attr.aria-checked]="kind() === 'work_note'" (click)="kind.set('work_note')"><esm-icon name="shield" [size]="13" /> Internal note</button>
            }
            <button role="radio" [attr.aria-checked]="kind() === 'public_reply'" (click)="kind.set('public_reply')"><esm-icon name="message" [size]="13" /> Reply to requester</button>
          </div>
          <textarea class="input" [placeholder]="kind() === 'work_note' ? 'Only agents see internal notes' : 'The requester sees this reply'" [value]="text()" (input)="text.set($any($event.target).value)"
                    (keydown)="onKey($event)" aria-label="Message" [disabled]="preview()"></textarea>
          <div class="send"><span class="muted hint"><kbd>⌘</kbd> <kbd>↵</kbd> to send</span>
            <button class="btn btn-primary btn-sm" [disabled]="!text().trim() || busy() || preview()" (click)="send()">{{ kind() === 'work_note' ? 'Add note' : 'Send reply' }}</button></div>
        </div>
      }
      <ol class="items">
        @for (a of items(); track a.id) {
          <li [class.note]="a.kind === 'work_note'">
            <span class="avatar" aria-hidden="true">{{ initials(a.authorName) }}</span>
            <div class="entry">
            <div class="meta"><strong>{{ a.authorName ?? 'Someone' }}</strong>
              @if (a.byAgent) { <span class="badge tone-brand">AI</span> }
              <span class="badge" [class]="a.kind === 'work_note' ? 'tone-amber' : 'tone-blue'">{{ a.kind === 'work_note' ? 'Internal' : 'Public' }}</span>
              <span class="muted">{{ a.createdAt | date: 'MMM d, HH:mm' }}</span></div>
            <div class="body" [innerHTML]="a.body"></div>
            </div>
          </li>
        } @empty {
          <li class="muted empty">{{ preview() ? 'Activity appears here on real records.' : 'No activity yet.' }}</li>
        }
      </ol>
    </div>
  `,
  styles: `
    .composer { border: 1px solid var(--esm-border-input); border-radius: var(--esm-radius-lg); padding: var(--esm-space-2) var(--esm-space-3) var(--esm-space-2); margin-bottom: var(--esm-space-5); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-xs);
      transition: border-color var(--esm-motion-fast) var(--esm-ease-out), box-shadow var(--esm-motion-fast) var(--esm-ease-out); }
    .composer:focus-within { border-color: var(--esm-accent-brand); box-shadow: 0 0 0 3px var(--esm-state-ring); }
    .composer.internal { background: color-mix(in srgb, var(--esm-accent-amber) 6%, var(--esm-surface-raised)); }
    .kinds { display: inline-flex; gap: 2px; padding: 2px; margin-bottom: var(--esm-space-1); border-radius: var(--esm-radius-sm); background: var(--esm-surface-sunken); }
    .kinds button { display: inline-flex; align-items: center; gap: 6px; border: 0; background: transparent; height: 26px; padding: 0 var(--esm-space-2); border-radius: 5px; font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .kinds button[aria-checked="true"] { background: var(--esm-surface-raised); color: var(--esm-text-default); box-shadow: var(--esm-shadow-xs); }
    textarea { border: 0; min-height: 64px; background: transparent; box-shadow: none; padding-inline: 0; }
    textarea:focus, textarea:focus-visible { box-shadow: none; outline: none; }
    .send { display: flex; justify-content: space-between; align-items: center; }
    .hint { font: var(--esm-text-caption); }
    .hint kbd { box-shadow: none; }
    .items { position: relative; list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: var(--esm-space-4); }
    .items::before { content: ""; position: absolute; left: 13px; top: 8px; bottom: 8px; width: 1px; background: var(--esm-border-subtle); }
    li { position: relative; display: flex; gap: var(--esm-space-3); }
    .entry { flex: 1; min-width: 0; padding: var(--esm-space-3); border-radius: var(--esm-radius-md); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    li.note .entry { background: color-mix(in srgb, var(--esm-accent-amber) 5%, var(--esm-surface-raised)); border-color: color-mix(in srgb, var(--esm-accent-amber) 25%, var(--esm-border-subtle)); }
    .meta { display: flex; flex-wrap: wrap; align-items: center; gap: var(--esm-space-2); font: var(--esm-text-body-sm); margin-bottom: var(--esm-space-1); }
    .body :first-child { margin-top: 0; } .body :last-child { margin-bottom: 0; }
    .empty { padding-left: var(--esm-space-10); }
    .items:has(.empty)::before { display: none; }
  `,
})
export class ActivityFeed {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly items = signal<ActivityView[]>([]);
  protected readonly text = signal('');
  protected readonly busy = signal(false);
  protected readonly preview = computed(() => this.ctx.mode() === 'draft' || !this.ctx.record()?.id || this.ctx.record()?.id === '00000000-0000-0000-0000-000000000000');
  protected readonly canNote = computed(() => !!this.ctx.record()?.access?.canUpdate && this.node().props['showInternal'] !== false && this.isAgentView());
  protected readonly canReply = computed(() => true);
  protected readonly kind = signal<'public_reply' | 'work_note'>('public_reply');

  constructor() {
    effect(() => {
      const r = this.ctx.record();
      if (r && !this.preview()) void this.load(r.entity, r.id);
      if (this.canNote() && this.node().props['defaultComposer'] !== 'public') this.kind.set('work_note');
    });
  }

  private isAgentView(): boolean {
    // Agents can edit the assignment group; requesters cannot. Mirrors the server rule for work notes.
    return this.ctx.record()?.access?.fields['assignment_group'] === 'write' || this.ctx.record()?.access?.fields['priority'] === 'write';
  }

  private async load(entity: string, id: string): Promise<void> {
    this.items.set(await this.api.get<ActivityView[]>(`/api/records/${entity}/${id}/activity`).catch(() => []));
  }

  protected onKey(e: KeyboardEvent): void {
    if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) void this.send();
  }

  protected async send(): Promise<void> {
    const r = this.ctx.record();
    if (!r) return;
    this.busy.set(true);
    try {
      const body = this.text().split(/\n{2,}/).map((p) => `<p>${p.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/\n/g, '<br>')}</p>`).join('');
      const created = await this.api.post<ActivityView>(`/api/records/${r.entity}/${r.id}/activity`, { kind: this.kind(), body });
      this.items.update((list) => [...list, created]);
      this.text.set('');
    } catch (e: any) {
      this.toasts.show(e.message ?? 'Could not send.', 'critical');
    } finally {
      this.busy.set(false);
    }
  }

  protected initials(name: string | null | undefined): string {
    return (name ?? '?').split(' ').map((p) => p[0]).slice(0, 2).join('');
  }
}

/** activity.audit_history: who changed what, when, under which configuration revision (REQ-REC-007). */
@Component({
  selector: 'esm-audit-history',
  imports: [DatePipe],
  template: `
    <ol class="history">
      @for (h of items(); track h.id) {
        <li>
          <div class="meta"><strong>{{ h.actor ?? 'System' }}</strong> @if (h.byAgent) { <span class="badge tone-brand">AI</span> }
            <span class="muted">{{ verb(h.operation) }} · {{ h.at | date: 'MMM d, HH:mm' }}</span>
            @if (h.configRevision) { <span class="muted mono">rev {{ h.configRevision }}</span> }</div>
          @for (c of h.changes; track c.field) {
            <div class="change"><span class="muted">{{ c.label }}</span> <span class="from">{{ show(c.from) }}</span> → <span>{{ show(c.to) }}</span></div>
          }
        </li>
      } @empty {
        <li class="muted">No history yet.</li>
      }
    </ol>
  `,
  styles: `
    .history { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: var(--esm-space-4); }
    .history li { position: relative; padding-left: var(--esm-space-5); }
    .history li::before { content: ""; position: absolute; left: 3px; top: 6px; width: 8px; height: 8px; border-radius: 50%; background: var(--esm-surface-raised); border: 2px solid var(--esm-accent-brand); }
    .history li:not(:last-child)::after { content: ""; position: absolute; left: 7px; top: 18px; bottom: calc(-1 * var(--esm-space-4) + 4px); width: 1px; background: var(--esm-border-subtle); }
    .meta { display: flex; flex-wrap: wrap; gap: var(--esm-space-2); align-items: center; font: var(--esm-text-body-sm); margin-bottom: 2px; }
    .change { font: var(--esm-text-body-sm); }
    .from { text-decoration: line-through; color: var(--esm-text-muted); }
  `,
})
export class AuditHistory {
  readonly node = input.required<NodeSpec>();
  private readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  protected readonly items = signal<HistoryEntry[]>([]);

  constructor() {
    effect(() => {
      const r = this.ctx.record();
      if (r && this.ctx.mode() !== 'draft' && r.revision > 0) {
        void this.api.get<HistoryEntry[]>(`/api/records/${r.entity}/${r.id}/history`).then((h) => this.items.set(h)).catch(() => this.items.set([]));
      }
    });
  }

  protected verb(op: string): string {
    return { 'record.create': 'created', 'record.update': 'edited', 'record.transition': 'moved', 'record.migrate': 'migrated', 'record.delete': 'deleted' }[op] ?? op;
  }

  protected show(v: unknown): string {
    if (v === null || v === undefined || v === '') return '∅';
    const s = typeof v === 'string' ? v.replace(/<[^>]+>/g, ' ') : JSON.stringify(v);
    return s.length > 80 ? s.slice(0, 80) + '…' : s;
  }
}
