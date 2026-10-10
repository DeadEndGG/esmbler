import { DatePipe } from '@angular/common';
import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { Api, ApiError } from '../core/api';
import { ApprovalStep, NodeSpec, RecordApproval } from '../core/models';
import { Toasts } from '../core/toast';
import { Icon, IconName } from '../ui/icon';
import { RenderContext } from './render-context';

const STEP_ICON: Record<ApprovalStep['status'], IconName> = {
  pending: 'clock',
  approved: 'circle-check',
  rejected: 'circle-x',
  expired: 'warning',
  cancelled: 'circle-dashed',
  skipped: 'circle-dashed',
};

const STATUS_TONE: Record<string, string> = {
  pending: 'tone-amber',
  waiting: 'tone-gray',
  approved: 'tone-green',
  rejected: 'tone-red',
  expired: 'tone-red',
  cancelled: 'tone-gray',
  skipped: 'tone-gray',
};

const STATUS_LABEL: Record<string, string> = {
  pending: 'Waiting',
  waiting: 'Not started',
  approved: 'Approved',
  rejected: 'Rejected',
  expired: 'Expired',
  cancelled: 'Cancelled',
  skipped: 'Not needed',
};

/**
 * activity.approvals (ADR 0016): the approval stages of a request, who decided what and on whose behalf, what is
 * pending and when it expires. People who may decide a pending step approve or reject right here; the server checks
 * eligibility, delegation and separation of duties again.
 */
@Component({
  selector: 'esm-approvals-panel',
  imports: [DatePipe, Icon],
  template: `
    @if (approval(); as a) {
      <section class="panel" [attr.aria-label]="title()">
        <header>
          <span class="title"><esm-icon name="stamp" [size]="15" /> {{ title() }}</span>
          <span class="badge" [class]="tone(a.status)">{{ label(a.status) }}</span>
        </header>
        <p class="policy">{{ a.policyLabel }}</p>
        @if (a.outcomeReason && a.status !== 'approved') { <p class="reason">{{ a.outcomeReason }}</p> }
        <ol class="stages">
          @for (stage of a.stages; track stage.id; let i = $index) {
            <li class="stage" [attr.data-status]="stage.status">
              <span class="dot" aria-hidden="true">{{ i + 1 }}</span>
              <div class="stage-body">
                <div class="stage-head">
                  <strong>{{ stage.label }}</strong>
                  <span class="badge no-dot" [class]="tone(stage.status)">{{ label(stage.status) }}</span>
                </div>
                @if (stage.steps.length > 1 && stage.status === 'pending') {
                  <p class="hint">{{ stage.mode === 'all' ? 'Every approver below must approve.' : 'One approval is enough.' }}</p>
                }
                <ul class="steps">
                  @for (s of stage.steps; track s.id) {
                    <li class="step" [attr.data-status]="s.status">
                      <esm-icon [name]="icon(s.status)" [size]="15" class="step-icon" />
                      <div class="step-body">
                        <span class="who">{{ s.approver }}@if (s.escalation) { <span class="badge no-dot tone-amber">Escalated</span> }</span>
                        <span class="what">{{ describe(s) }}</span>
                        @if (s.comment) { <blockquote>“{{ s.comment }}”</blockquote> }
                        @if (s.status === 'pending' && s.dueAt) {
                          <span class="due" [class.late]="late(s.dueAt)"><esm-icon name="clock" [size]="12" /> {{ late(s.dueAt) ? 'Overdue since' : 'Due' }} {{ s.dueAt | date: 'MMM d, HH:mm' }}</span>
                        }
                        @if (s.canDecide) {
                          @if (rejecting() === s.id) {
                            <div class="reject">
                              <label [for]="'why-' + s.id">Why are you rejecting?</label>
                              <textarea class="input" [id]="'why-' + s.id" rows="3" [value]="comment()" (input)="comment.set($any($event.target).value)"
                                        placeholder="The requester sees this" (keydown.escape)="rejecting.set(null)"></textarea>
                              <div class="row">
                                <button class="btn btn-sm" (click)="rejecting.set(null)">Back</button>
                                <button class="btn btn-danger btn-sm" [disabled]="busy() || !comment().trim()" (click)="decide(s, 'rejected')">Reject request</button>
                              </div>
                            </div>
                          } @else {
                            <div class="row">
                              <button class="btn btn-primary btn-sm" [disabled]="busy()" (click)="decide(s, 'approved')"><esm-icon name="check" [size]="14" /> Approve</button>
                              <button class="btn btn-sm" [disabled]="busy()" (click)="startReject(s)">Reject…</button>
                            </div>
                          }
                        }
                      </div>
                    </li>
                  } @empty {
                    <li class="muted waiting">{{ stage.status === 'skipped' ? 'Not needed for this request.' : 'Starts when the previous stage is approved.' }}</li>
                  }
                </ul>
              </div>
            </li>
          }
        </ol>
      </section>
    } @else if (preview()) {
      <section class="panel"><header><span class="title"><esm-icon name="stamp" [size]="15" /> {{ title() }}</span></header><p class="muted">Approval stages appear here on requests that need approval.</p></section>
    }
  `,
  styles: `
    .panel { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-card); margin-bottom: var(--esm-space-4); }
    header { display: flex; align-items: center; justify-content: space-between; gap: var(--esm-space-2); }
    .title { display: inline-flex; align-items: center; gap: 6px; font: var(--esm-text-title-sm); }
    .policy { margin: 2px 0 var(--esm-space-3); font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    .reason { margin: 0 0 var(--esm-space-3); font: var(--esm-text-body-sm); }
    .stages { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--esm-space-3); }
    .stage { display: flex; gap: var(--esm-space-3); position: relative; }
    .stage:not(:last-child)::before { content: ""; position: absolute; left: 11px; top: 26px; bottom: calc(-1 * var(--esm-space-3)); width: 1px; background: var(--esm-border-subtle); }
    .dot { flex: none; display: grid; place-items: center; width: 23px; height: 23px; border-radius: var(--esm-radius-full); font: var(--esm-text-caption); font-weight: 600;
      background: var(--esm-surface-sunken); color: var(--esm-text-muted); border: 1px solid var(--esm-border-subtle); }
    .stage[data-status="pending"] .dot { background: color-mix(in srgb, var(--esm-accent-amber) 18%, var(--esm-surface-raised)); color: var(--esm-text-default); border-color: color-mix(in srgb, var(--esm-accent-amber) 45%, transparent); }
    .stage[data-status="approved"] .dot { background: color-mix(in srgb, var(--esm-accent-green) 18%, var(--esm-surface-raised)); color: var(--esm-text-default); border-color: color-mix(in srgb, var(--esm-accent-green) 45%, transparent); }
    .stage[data-status="rejected"] .dot { background: color-mix(in srgb, var(--esm-accent-red) 18%, var(--esm-surface-raised)); color: var(--esm-text-default); border-color: color-mix(in srgb, var(--esm-accent-red) 45%, transparent); }
    .stage-body { flex: 1; min-width: 0; }
    .stage-head { display: flex; align-items: center; justify-content: space-between; gap: var(--esm-space-2); font: var(--esm-text-body-sm); min-height: 23px; }
    .hint { margin: 2px 0 0; font: var(--esm-text-caption); color: var(--esm-text-muted); }
    .steps { list-style: none; margin: var(--esm-space-2) 0 0; padding: 0; display: flex; flex-direction: column; gap: var(--esm-space-2); }
    .step { display: flex; gap: var(--esm-space-2); padding: var(--esm-space-2); border-radius: var(--esm-radius-md); background: var(--esm-surface-sunken); }
    .step-icon { flex: none; margin-top: 2px; color: var(--esm-text-muted); }
    .step[data-status="approved"] .step-icon { color: var(--esm-accent-green); }
    .step[data-status="rejected"] .step-icon, .step[data-status="expired"] .step-icon { color: var(--esm-accent-red); }
    .step[data-status="pending"] .step-icon { color: var(--esm-accent-amber); }
    .step-body { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; font: var(--esm-text-body-sm); }
    .who { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; font-weight: 600; }
    .what { color: var(--esm-text-muted); }
    blockquote { margin: 4px 0 0; padding: 4px var(--esm-space-2); border-left: 2px solid var(--esm-border-strong); color: var(--esm-text-default); }
    .due { display: inline-flex; align-items: center; gap: 4px; font: var(--esm-text-caption); color: var(--esm-text-muted); }
    .due.late { color: var(--esm-text-default); font-weight: 600; }
    .row { display: flex; flex-wrap: wrap; gap: var(--esm-space-2); margin-top: var(--esm-space-2); }
    .reject { display: flex; flex-direction: column; gap: 4px; margin-top: var(--esm-space-2); }
    .reject label { font: var(--esm-text-caption); font-weight: 600; }
    .waiting { font: var(--esm-text-caption); padding: 2px 0; }
  `,
})
export class ApprovalsPanel {
  readonly node = input.required<NodeSpec>();
  private readonly ctx = inject(RenderContext);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly approval = signal<RecordApproval | null>(null);
  protected readonly busy = signal(false);
  protected readonly rejecting = signal<string | null>(null);
  protected readonly comment = signal('');
  protected readonly title = computed(() => (this.node().props['title'] as string | undefined) ?? 'Approvals');
  protected readonly preview = computed(() => this.ctx.mode() === 'draft' || !this.ctx.record()?.id || this.ctx.record()?.id === '00000000-0000-0000-0000-000000000000');
  private readonly key = computed(() => (this.preview() ? null : `${this.ctx.record()?.entity}/${this.ctx.record()?.id}/${this.ctx.record()?.revision}`));

  constructor() {
    effect(() => {
      const key = this.key();
      untracked(() => void this.load(key));
    });
  }

  protected tone(status: string): string {
    return STATUS_TONE[status] ?? 'tone-gray';
  }

  protected label(status: string): string {
    return STATUS_LABEL[status] ?? status;
  }

  protected icon(status: ApprovalStep['status']): IconName {
    return STEP_ICON[status];
  }

  protected late(due: string): boolean {
    return new Date(due).getTime() < Date.now();
  }

  protected describe(s: ApprovalStep): string {
    const by = s.decidedBy ? `${s.decidedBy}${s.onBehalfOf ? ` for ${s.onBehalfOf}` : ''}` : '';
    switch (s.status) {
      case 'approved': return `Approved by ${by}`;
      case 'rejected': return `Rejected by ${by}`;
      case 'expired': return 'No decision before the deadline';
      case 'cancelled': return 'Cancelled';
      case 'skipped': return 'Not needed any more';
      default: return s.approverKind === 'user' ? 'Waiting for their decision' : 'Waiting for one of them to decide';
    }
  }

  protected startReject(s: ApprovalStep): void {
    this.comment.set('');
    this.rejecting.set(s.id);
    setTimeout(() => document.getElementById('why-' + s.id)?.focus());
  }

  protected async decide(s: ApprovalStep, decision: 'approved' | 'rejected'): Promise<void> {
    this.busy.set(true);
    try {
      const view = await this.api.post<RecordApproval>(`/api/approvals/steps/${s.id}/decision`, { decision, comment: decision === 'rejected' ? this.comment().trim() : null });
      this.approval.set(view);
      this.rejecting.set(null);
      this.toasts.show(decision === 'approved' ? 'Approved. Thanks!' : 'Rejected. The requester will see your reason.', decision === 'approved' ? 'success' : 'info');
      await this.ctx.refreshRecord();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Your decision was not saved.', 'warning');
      await this.load(this.key());
    } finally {
      this.busy.set(false);
    }
  }

  private async load(key: string | null): Promise<void> {
    if (!key) {
      this.approval.set(null);
      return;
    }
    try {
      const list = await this.api.get<RecordApproval[]>(`/api/records/${key.split('/').slice(0, 2).join('/')}/approvals`);
      if (key === this.key()) this.approval.set(list[0] ?? null);
    } catch {
      this.approval.set(null);
    }
  }
}

