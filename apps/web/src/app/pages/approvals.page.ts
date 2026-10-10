import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Directory } from '../core/directory';
import { ApprovalInbox, ApprovalInboxItem, Delegation, EntityClientSpec, RecordApproval } from '../core/models';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { Answer, answersOf } from '../renderer/render-context';
import { Icon } from '../ui/icon';

/**
 * The approvals inbox (ADR 0016, REQ-BIZ-004): requests waiting for the signed-in person's decision, directly or as
 * someone's delegate, their recent decisions, and out-of-office delegation. Decisions are checked again on the server.
 */
@Component({
  selector: 'esm-approvals-page',
  imports: [DatePipe, RouterLink, Icon],
  template: `
    <div class="admin-page">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="stamp" [size]="18" /></span>
        <div>
          <h1 class="text-title-lg">Approvals</h1>
          <p class="section-lead">Requests waiting for your decision. Fulfillment starts as soon as every stage is approved.</p>
        </div>
      </header>

      <div class="grid grid-cols-[minmax(0,1fr)_320px] items-start gap-6 max-[960px]:grid-cols-1">
        <section aria-labelledby="queue-title">
          <div class="mb-4 flex flex-wrap items-center gap-2" role="tablist" aria-label="Approvals">
            <button class="pill" role="tab" id="queue-title" [attr.aria-selected]="tab() === 'pending'" [class.on]="tab() === 'pending'" (click)="tab.set('pending')">
              Waiting for you <span class="count">{{ inbox()?.pending?.length ?? 0 }}</span>
            </button>
            <button class="pill" role="tab" [attr.aria-selected]="tab() === 'decided'" [class.on]="tab() === 'decided'" (click)="tab.set('decided')">Decided by you</button>
          </div>

          @if (error()) {
            <div class="callout tone-red" role="alert">{{ error() }}</div>
          } @else if (!inbox()) {
            <div class="flex flex-col gap-3" aria-busy="true">
              @for (i of [1, 2]; track i) { <div class="item"><span class="skeleton block h-4 w-1/3"></span><span class="skeleton mt-3 block h-3 w-2/3"></span></div> }
            </div>
          } @else {
            <ul class="flex flex-col gap-3" [attr.aria-label]="tab() === 'pending' ? 'Waiting for you' : 'Decided by you'">
              @for (it of list(); track it.stepId) {
                <li class="item" [attr.data-status]="it.status">
                  <div class="item-head">
                    <div class="min-w-0">
                      @if (it.record; as r) {
                        <a class="title" [routerLink]="['/records', it.entity, it.recordId]">{{ r.number ? r.number + ' · ' : '' }}{{ r.data['title'] ?? 'Request' }}</a>
                      } @else {
                        <span class="title">A request you cannot open</span>
                      }
                      <p class="meta">
                        @if (it.requester) { <span>Requested by {{ it.requester }}</span> }
                        @if (it.record?.display?.['catalog_item']; as item) { <span>{{ item }}</span> }
                        <span>{{ it.stage }}</span>
                        @if (it.status === 'pending' && it.dueAt) { <span [class.late]="late(it.dueAt)"><esm-icon name="clock" [size]="12" /> {{ late(it.dueAt) ? 'Overdue since' : 'Due' }} {{ it.dueAt | date: 'MMM d, HH:mm' }}</span> }
                        @if (it.status !== 'pending' && it.decidedAt) { <span>{{ it.decidedAt | date: 'MMM d, HH:mm' }}</span> }
                      </p>
                    </div>
                    <div class="flex flex-wrap items-center gap-2">
                      @if (it.onBehalfOf) { <span class="badge no-dot tone-purple"><esm-icon name="forward" [size]="12" /> For {{ it.onBehalfOf }}</span> }
                      @if (it.escalation) { <span class="badge no-dot tone-amber">Escalated</span> }
                      @if (it.status !== 'pending') { <span class="badge" [class]="it.status === 'approved' ? 'tone-green' : 'tone-red'">{{ it.status === 'approved' ? 'Approved' : 'Rejected' }}</span> }
                    </div>
                  </div>

                  @if (answers(it); as qa) {
                    @if (qa.length) {
                      <dl class="answers">
                        @for (a of qa; track a.key) {
                          <div><dt>{{ a.label }}</dt><dd [class.muted]="a.empty">{{ a.empty ? '—' : a.text }}</dd></div>
                        }
                      </dl>
                    }
                  }
                  @if (it.record?.data?.['description']; as notes) { <p class="notes">{{ notes }}</p> }
                  @if (it.status !== 'pending' && it.comment) { <blockquote>“{{ it.comment }}”</blockquote> }

                  @if (it.status === 'pending') {
                    @if (rejecting() === it.stepId) {
                      <div class="reject">
                        <label [for]="'why-' + it.stepId">Why are you rejecting?</label>
                        <textarea class="input" [id]="'why-' + it.stepId" rows="3" [value]="comment()" (input)="comment.set($any($event.target).value)" placeholder="The requester sees this"
                                  (keydown.escape)="rejecting.set(null)"></textarea>
                        <div class="actions">
                          <button class="btn btn-sm" (click)="rejecting.set(null)">Back</button>
                          <button class="btn btn-danger btn-sm" [disabled]="busy() || !comment().trim()" (click)="decide(it, 'rejected')">Reject request</button>
                        </div>
                      </div>
                    } @else {
                      <div class="actions">
                        <button class="btn btn-primary btn-sm" [disabled]="busy()" (click)="decide(it, 'approved')"><esm-icon name="check" [size]="14" /> Approve</button>
                        <button class="btn btn-sm" [disabled]="busy()" (click)="startReject(it)">Reject…</button>
                      </div>
                    }
                  }
                </li>
              } @empty {
                <li class="empty">
                  <esm-icon name="circle-check" [size]="28" />
                  <strong class="text-title-sm">{{ tab() === 'pending' ? 'Nothing waiting for you' : 'No decisions yet' }}</strong>
                  <p class="text-body-sm text-muted">{{ tab() === 'pending' ? 'New requests that need your approval appear here.' : 'Requests you approve or reject appear here.' }}</p>
                </li>
              }
            </ul>
          }
        </section>

        <aside class="side" aria-labelledby="ooo-title">
          <h2 class="text-title-sm" id="ooo-title"><esm-icon name="forward" [size]="15" /> Out of office</h2>
          <p class="text-body-sm text-muted">Let someone decide your approvals while you are away. You stay accountable; their decisions show they acted for you.</p>
          @for (d of mine(); track d.id) {
            <div class="delegation">
              <div><strong>{{ d.delegate }}</strong> decides for you</div>
              <div class="text-caption text-muted">{{ d.startsAt | date: 'MMM d' }} – {{ d.endsAt | date: 'MMM d, y' }} · {{ d.active ? 'Active now' : 'Scheduled' }}</div>
              <button class="btn btn-sm mt-2" (click)="revoke(d)">End delegation</button>
            </div>
          }
          @if (!mine().length) {
            <form class="mt-3 flex flex-col gap-2" (submit)="$event.preventDefault(); delegate()">
              <label for="delegate-to" class="text-caption font-semibold">Delegate to</label>
              <select id="delegate-to" class="input" [value]="delegateTo()" (change)="delegateTo.set($any($event.target).value)">
                <option value="">Choose a person</option>
                @for (p of people(); track p.id) { <option [value]="p.id">{{ p.displayName }}</option> }
              </select>
              <div class="grid grid-cols-2 gap-2">
                <div><label for="ooo-from" class="text-caption font-semibold">From</label><input id="ooo-from" class="input" type="date" [value]="from()" (input)="from.set($any($event.target).value)" /></div>
                <div><label for="ooo-to" class="text-caption font-semibold">Until</label><input id="ooo-to" class="input" type="date" [value]="until()" (input)="until.set($any($event.target).value)" /></div>
              </div>
              <button class="btn btn-primary btn-sm self-start" type="submit" [disabled]="busy() || !delegateTo() || !from() || !until()">Delegate my approvals</button>
            </form>
          }
          @if (forOthers().length) {
            <h3 class="mt-5 text-caption font-semibold uppercase tracking-wide text-muted">You decide for</h3>
            @for (d of forOthers(); track d.id) {
              <div class="text-body-sm">{{ d.delegator }} <span class="text-muted">until {{ d.endsAt | date: 'MMM d' }}</span></div>
            }
          }
        </aside>
      </div>
    </div>
  `,
  styles: `
    .pill { display: inline-flex; align-items: center; gap: 6px; height: 30px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-full); border: 1px solid var(--esm-border-strong); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .pill.on { color: var(--esm-text-on-accent); background: var(--esm-accent-brand); border-color: var(--esm-accent-brand); }
    .count { min-width: 18px; padding: 0 5px; border-radius: var(--esm-radius-full); background: color-mix(in srgb, currentColor 18%, transparent); font: var(--esm-text-caption); font-weight: 600; text-align: center; }
    .item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); }
    .item[data-status="pending"] { border-left: 3px solid var(--esm-accent-amber); }
    .item-head { display: flex; justify-content: space-between; gap: var(--esm-space-3); flex-wrap: wrap; }
    .title { font: var(--esm-text-title-sm); color: var(--esm-text-default); }
    .meta { display: flex; flex-wrap: wrap; gap: 4px var(--esm-space-3); margin: 2px 0 0; font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    .meta span { display: inline-flex; align-items: center; gap: 4px; }
    .meta .late { color: var(--esm-text-default); font-weight: 600; }
    .answers { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: var(--esm-space-2) var(--esm-space-4); margin: var(--esm-space-3) 0 0; }
    dt { font: var(--esm-text-caption); color: var(--esm-text-muted); }
    dd { margin: 0; font: var(--esm-text-body-sm); }
    .notes { margin: var(--esm-space-3) 0 0; font: var(--esm-text-body-sm); white-space: pre-line; }
    blockquote { margin: var(--esm-space-3) 0 0; padding: 4px var(--esm-space-2); border-left: 2px solid var(--esm-border-strong); font: var(--esm-text-body-sm); }
    .actions { display: flex; flex-wrap: wrap; gap: var(--esm-space-2); margin-top: var(--esm-space-3); }
    .reject { display: flex; flex-direction: column; gap: 4px; margin-top: var(--esm-space-3); }
    .reject label { font: var(--esm-text-caption); font-weight: 600; }
    .empty { display: flex; flex-direction: column; align-items: center; gap: var(--esm-space-1); padding: var(--esm-space-10) var(--esm-space-4); border: 1px dashed var(--esm-border-strong); border-radius: var(--esm-radius-lg); text-align: center; color: var(--esm-text-muted); }
    .empty strong { color: var(--esm-text-default); }
    .side { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); }
    .side h2 { display: flex; align-items: center; gap: 6px; margin: 0 0 var(--esm-space-1); }
    .delegation { margin-top: var(--esm-space-3); padding: var(--esm-space-3); border-radius: var(--esm-radius-md); background: var(--esm-surface-sunken); font: var(--esm-text-body-sm); }
  `,
})
export class ApprovalsPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly directory = inject(Directory);
  private readonly session = inject(Session);
  protected readonly inbox = signal<ApprovalInbox | null>(null);
  protected readonly delegations = signal<Delegation[]>([]);
  protected readonly error = signal<string | null>(null);
  protected readonly tab = signal<'pending' | 'decided'>('pending');
  protected readonly busy = signal(false);
  protected readonly rejecting = signal<string | null>(null);
  protected readonly comment = signal('');
  protected readonly delegateTo = signal('');
  protected readonly from = signal(new Date().toISOString().slice(0, 10));
  protected readonly until = signal('');
  private readonly specs = signal<Record<string, EntityClientSpec>>({});

  protected readonly list = computed(() => (this.tab() === 'pending' ? this.inbox()?.pending : this.inbox()?.decided) ?? []);
  private readonly me = computed(() => this.session.app()?.user.id);
  protected readonly mine = computed(() => this.delegations().filter((d) => d.delegatorId === this.me()));
  protected readonly forOthers = computed(() => this.delegations().filter((d) => d.delegateId === this.me()));
  protected readonly people = computed(() => this.directory.people().filter((p) => p.id !== this.me()));

  constructor() {
    void this.load();
    void this.directory.load();
  }

  protected late(due: string): boolean {
    return new Date(due).getTime() < Date.now();
  }

  protected answers(it: ApprovalInboxItem): Answer[] {
    const spec = this.specs()[it.entity];
    if (!it.record || !spec) return [];
    const name = (type: string, id: string) =>
      type === 'group' ? this.directory.groups().find((g) => g.id === id)?.name : this.directory.people().find((p) => p.id === id)?.displayName;
    return answersOf(spec, it.record.data, name).slice(0, 8);
  }

  protected startReject(it: ApprovalInboxItem): void {
    this.comment.set('');
    this.rejecting.set(it.stepId);
    setTimeout(() => document.getElementById('why-' + it.stepId)?.focus());
  }

  protected async decide(it: ApprovalInboxItem, decision: 'approved' | 'rejected'): Promise<void> {
    this.busy.set(true);
    try {
      const result = await this.api.post<RecordApproval>(`/api/approvals/steps/${it.stepId}/decision`, { decision, comment: decision === 'rejected' ? this.comment().trim() : null });
      this.rejecting.set(null);
      const next = result.status === 'approved' ? 'Approved: fulfillment starts now.' : result.status === 'rejected' ? 'Rejected. The requester will see your reason.' : 'Approved. The next stage is with its approvers.';
      this.toasts.show(next, decision === 'approved' ? 'success' : 'info');
      await this.load();
      this.session.refreshApprovals();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Your decision was not saved.', 'warning');
      await this.load();
    } finally {
      this.busy.set(false);
    }
  }

  protected async delegate(): Promise<void> {
    this.busy.set(true);
    try {
      const startsAt = this.from() === new Date().toISOString().slice(0, 10) ? new Date().toISOString() : new Date(this.from() + 'T00:00:00').toISOString();
      const endsAt = new Date(this.until() + 'T23:59:59').toISOString();
      await this.api.post<Delegation>('/api/approvals/delegations', { delegate: this.delegateTo(), startsAt, endsAt });
      this.toasts.show('Your approvals are delegated.', 'success');
      this.delegateTo.set('');
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The delegation was not saved.', 'warning');
    } finally {
      this.busy.set(false);
    }
  }

  protected async revoke(d: Delegation): Promise<void> {
    try {
      await this.api.delete(`/api/approvals/delegations/${d.id}`);
      this.toasts.show(`${d.delegate} no longer decides for you.`, 'info');
      await this.load();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The delegation was not ended.', 'warning');
    }
  }

  private async load(): Promise<void> {
    try {
      const [inbox, delegations] = await Promise.all([this.api.get<ApprovalInbox>('/api/approvals'), this.api.get<Delegation[]>('/api/approvals/delegations')]);
      this.delegations.set(delegations);
      const entities = [...new Set([...inbox.pending, ...inbox.decided].filter((i) => i.record).map((i) => i.entity))].filter((e) => !this.specs()[e]);
      const loaded = await Promise.all(entities.map((e) => this.session.entity(e).then((s) => [e, s] as const).catch(() => null)));
      this.specs.update((m) => ({ ...m, ...Object.fromEntries(loaded.filter((x): x is readonly [string, EntityClientSpec] => !!x)) }));
      this.inbox.set(inbox);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'Approvals could not be loaded.');
    }
  }
}
