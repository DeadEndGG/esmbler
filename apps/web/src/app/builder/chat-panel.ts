import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from '../core/api';
import { AssistantEvent, Live } from '../core/live';
import { DraftStep } from '../core/models';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { BuilderState } from './builder-state';
import { Icon } from '../ui/icon';

interface ChatItem {
  id: number;
  role: 'user' | 'assistant' | 'progress' | 'question' | 'error';
  text: string;
  options?: string[];
  chips?: string[];
}

const EXAMPLES = [
  'Add a required "Customer impact" field to tickets and put it in the Details card',
  'When priority is P1, require an impact description',
  'Show a warning callout when a ticket is unassigned',
];

/**
 * The AI builder side panel (REQ-LIVE-001..004). Messages go to the assistant, which edits the draft through MCP;
 * every applied step appears here linked to the elements it touched, can be undone on its own, and the draft
 * is published by a person after reviewing readiness and the exact change set (REQ-CFG-007).
 */
@Component({
  selector: 'esm-chat-panel',
  imports: [FormsModule, Icon],
  template: `
    <aside class="panel" aria-label="AI builder">
      <header class="head">
        <span class="mark" aria-hidden="true"><esm-icon name="sparkles" [size]="16" /></span>
        <div class="min-w-0 flex-1">
          <div class="flex items-center gap-2">
            <strong class="text-title-sm">AI builder</strong>
            @if (planner() === 'scripted') { <span class="badge no-dot tone-gray" title="No AI model is configured; a built-in assistant handles common requests.">Built-in</span> }
          </div>
          <span class="status">
            @if (busy()) { <span class="pulse" aria-hidden="true"></span> Working… } @else if (builder.draft(); as d) { Draft · {{ activeSteps().length }} change{{ activeSteps().length === 1 ? '' : 's' }} } @else { Changes go to a private draft }
          </span>
        </div>
        <button class="btn btn-ghost btn-sm btn-icon" (click)="builder.close()" aria-label="Close the AI builder"><esm-icon name="x" [size]="16" /></button>
      </header>

      <div class="scroll" #scroll aria-live="polite">
        @if (!items().length) {
          <div class="intro">
            <span class="intro-mark" aria-hidden="true"><esm-icon name="sparkles" [size]="22" /></span>
            <h2 class="text-title-md">What should we change?</h2>
            <p class="text-body-sm text-muted">Describe it in plain words. Edits appear live on the page as a private draft; nothing changes for others until you publish.</p>
            <p class="tip"><esm-icon name="info" [size]="14" /> Click blocks on the page to point at them (Shift-click for several).</p>
            <div class="flex flex-col gap-2">
              @for (ex of examples; track ex) {
                <button class="example" (click)="text = ex; send()"><span class="flex-1">{{ ex }}</span><esm-icon name="arrow-up-right" [size]="14" /></button>
              }
            </div>
          </div>
        }
        @for (m of items(); track m.id) {
          <div class="msg" [class]="m.role">
            @if (m.role === 'assistant') { <span class="msg-mark" aria-hidden="true"><esm-icon name="sparkles" [size]="12" /></span> }
            @if (m.role === 'progress') { <span class="dot" aria-hidden="true"></span> }
            <div class="min-w-0 flex-1">
              @if (m.chips?.length) { <div class="chips">@for (c of m.chips; track c) { <span class="chip">{{ c }}</span> }</div> }
              <p>{{ m.text }}</p>
              @if (m.options?.length) {
                <div class="options">@for (o of m.options; track o) { <button class="btn btn-sm" (click)="text = o; send()">{{ o }}</button> }</div>
              }
            </div>
          </div>
        }

        @if (steps().length) {
          <section class="steps" aria-label="Draft changes">
            <h3><esm-icon name="history" [size]="14" /> Changes in this draft</h3>
            <ol>
              @for (s of steps(); track s.seq) {
                <li [class.undone]="s.undone" (mouseenter)="builder.highlight(s.targets)" (focusin)="builder.highlight(s.targets)">
                  <span class="who" [class.agent]="s.byAgent" [title]="s.byAgent ? 'Made by the AI' : 'Made by a person'"><esm-icon [name]="s.byAgent ? 'sparkles' : 'check'" [size]="12" /></span>
                  <span class="summary">{{ s.summary }}</span>
                  @if (s.undone) {
                    <button class="btn btn-ghost btn-sm" (click)="builder.redo(s.seq)">Redo</button>
                  } @else {
                    <button class="btn btn-ghost btn-sm" (click)="builder.undo(s.seq)" [attr.aria-label]="'Undo: ' + s.summary"><esm-icon name="undo" [size]="13" /> Undo</button>
                  }
                </li>
              }
            </ol>
          </section>
        }
      </div>

      @if (builder.draft(); as d) {
        @if (d.diagnostics.length) {
          <div class="diag" role="status">
            @for (x of d.diagnostics.slice(0, 3); track $index) {
              <p [class]="x.severity === 'error' ? 'err' : 'warn'"><esm-icon [name]="x.severity === 'error' ? 'circle-alert' : 'warning'" [size]="14" /> <span>{{ x.message }}@if (x.suggestion) { <span class="muted"> {{ x.suggestion }}</span> }</span></p>
            }
          </div>
        }
      }

      <footer class="composer">
        <div class="box">
          @if (builder.selection().length) {
            <div class="chips sel">
              @for (s of builder.selection(); track s.nodeId) {
                <span class="chip">{{ s.label }} <button aria-label="Remove from selection" (click)="builder.toggleSelect(s, true)"><esm-icon name="x" [size]="12" /></button></span>
              }
            </div>
          }
          <textarea rows="3" [(ngModel)]="text" (keydown)="onKey($event)" placeholder="Ask for a change…" aria-label="Message to the AI builder" #box></textarea>
          <div class="flex items-center justify-between gap-2">
            <span class="text-caption text-muted"><kbd>↵</kbd> to send · <kbd>⇧↵</kbd> new line</span>
            @if (busy()) {
              <button class="btn btn-sm" (click)="stop()">Stop</button>
            } @else {
              <button class="btn btn-primary btn-sm" (click)="send()" [disabled]="!text.trim()"><esm-icon name="send" [size]="13" /> Send</button>
            }
          </div>
        </div>
        @if (builder.draft()) {
          <div class="bar">
            <button class="btn btn-ghost btn-sm" (click)="discard()">Discard draft</button>
            <span class="flex-1"></span>
            <button class="btn btn-sm" [class.btn-primary]="!!builder.pendingPublish() || activeSteps().length > 0" (click)="review.set(true)" [disabled]="!activeSteps().length">
              {{ session.canPublish() ? 'Review and publish' : 'Request publish' }}
            </button>
          </div>
        }
      </footer>
    </aside>

    @if (review()) {
      <div class="scrim" (click)="review.set(false)"></div>
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="pub-title" (keydown.escape)="review.set(false)">
        <div class="mb-4 flex items-start gap-3">
          <span class="mark" aria-hidden="true"><esm-icon name="package" [size]="16" /></span>
          <div>
            <h2 id="pub-title">Publish changes</h2>
            @if (builder.pendingPublish(); as p) {
              <p class="text-body-sm text-muted">{{ p.requestedByAgent ? 'The AI requested' : 'Requested' }} publishing: {{ p.summary }}</p>
            } @else {
              <p class="text-body-sm text-muted">Check readiness and the exact change set before it goes live.</p>
            }
          </div>
        </div>
        @let d = builder.draft();
        @if (d) {
          <h3>Readiness</h3>
          @if (errors().length) {
            <ul class="readiness">@for (x of errors(); track $index) { <li class="err"><esm-icon name="circle-alert" [size]="14" /> {{ x.message }}</li> }</ul>
          } @else {
            <p class="ok"><esm-icon name="circle-check" [size]="16" /> The draft compiles and passes validation.</p>
          }
          @if (d.workflowImpact?.length) {
            <h3>Records already in a changed workflow</h3>
            <ul class="impact">
              @for (w of d.workflowImpact; track w.workflow) {
                <li>
                  <strong>{{ w.entityLabel }}</strong>
                  <span class="muted">{{ w.running }} running</span>
                  @if (w.policy === 'pin') {
                    <span class="badge tone-blue">keep their current version</span>
                  } @else if (w.policy === 'migrate') {
                    <span class="badge tone-purple">{{ w.moving }} move to the new version</span>
                  } @else {
                    <span class="badge tone-red">{{ w.moving }} will be cancelled</span>
                  }
                  @if (w.policy !== 'pin' && w.stayingPinned > 0) { <span class="muted">{{ w.stayingPinned }} stay on their version ({{ w.unmappedStates.join(', ') }})</span> }
                </li>
              }
            </ul>
          }
          @if (d.permissionImpact) { <p class="callout tone-amber mt-2 text-body-sm"><esm-icon name="shield" [size]="14" /> This draft changes who can see or edit data. Publishing requires permission administration.</p> }
          <h3>What changes</h3>
          <ul class="changes">
            @for (c of d.changes; track c.kind + c.id) {
              <li><span class="badge" [class]="c.change === 'added' ? 'tone-green' : c.change === 'removed' ? 'tone-red' : 'tone-blue'">{{ c.change }}</span> <span class="muted">{{ c.kind }}</span> <strong>{{ c.label }}</strong>@if (c.properties.length) { <span class="muted"> ({{ c.properties.join(', ') }})</span> }</li>
            } @empty { <li class="muted">No effective changes.</li> }
          </ul>
          <p class="muted mono hash">Artifact {{ d.artifactHash?.slice(0, 12) }}</p>
        }
        <div class="actions">
          <button class="btn" (click)="review.set(false)">Cancel</button>
          @if (session.canPublish()) {
            <button class="btn btn-primary" (click)="publish()" [disabled]="publishing() || errors().length > 0">{{ publishing() ? 'Publishing…' : 'Publish' }}</button>
          } @else {
            <button class="btn btn-primary" (click)="requestPublish()" [disabled]="publishing() || errors().length > 0">Request publish</button>
          }
        </div>
      </div>
    }
  `,
  styles: `
    :host { display: block; height: 100dvh; }
    .panel { display: flex; flex-direction: column; height: 100%; background: var(--esm-surface-raised); }
    .head { display: flex; align-items: center; gap: var(--esm-space-3); padding: var(--esm-space-3) var(--esm-space-4); border-bottom: 1px solid var(--esm-border-subtle); }
    .mark { display: grid; place-items: center; flex: none; width: 32px; height: 32px; border-radius: var(--esm-radius-md); background: var(--esm-gradient-brand); color: var(--esm-text-on-gradient); box-shadow: var(--esm-shadow-inset-highlight), var(--esm-shadow-brand); }
    .status { display: flex; align-items: center; gap: 6px; font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    .pulse { width: 7px; height: 7px; border-radius: 50%; background: var(--esm-accent-brand); animation: esm-pulse 1.2s ease-in-out infinite; }
    @keyframes esm-pulse { 50% { opacity: 0.3; } }
    @media (prefers-reduced-motion: reduce) { .pulse { animation: none; } }
    .scroll { flex: 1; overflow: auto; padding: var(--esm-space-4); display: flex; flex-direction: column; gap: var(--esm-space-3); background: var(--esm-surface-base); }
    .intro { display: flex; flex-direction: column; gap: var(--esm-space-3); padding: var(--esm-space-2) 0; }
    .intro-mark { display: grid; place-items: center; width: 44px; height: 44px; border-radius: var(--esm-radius-lg); background: var(--esm-gradient-brand); color: var(--esm-text-on-gradient); box-shadow: var(--esm-shadow-inset-highlight), var(--esm-shadow-brand); }
    .tip { display: flex; align-items: center; gap: var(--esm-space-2); font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    .example { display: flex; align-items: center; gap: var(--esm-space-3); width: 100%; text-align: left; padding: 10px var(--esm-space-3); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); background: var(--esm-surface-raised);
      font: var(--esm-text-body-sm); color: var(--esm-text-default); box-shadow: var(--esm-shadow-xs); transition: border-color var(--esm-motion-fast) var(--esm-ease-out), transform var(--esm-motion-fast) var(--esm-ease-out); }
    .example esm-icon { color: var(--esm-text-muted); }
    .example:hover { border-color: var(--esm-accent-brand); }
    .example:hover esm-icon { color: var(--esm-accent-brand); }
    .msg { display: flex; gap: var(--esm-space-2); }
    .msg p { margin: 0; white-space: pre-wrap; }
    .msg.user { align-self: flex-end; max-width: 85%; padding: var(--esm-space-2) var(--esm-space-3); border-radius: var(--esm-radius-lg) var(--esm-radius-lg) var(--esm-radius-sm) var(--esm-radius-lg); background: var(--esm-accent-brand); color: var(--esm-text-on-accent); box-shadow: var(--esm-shadow-xs); }
    .msg.assistant { padding: var(--esm-space-3); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); box-shadow: var(--esm-shadow-xs); }
    .msg-mark { display: grid; place-items: center; flex: none; width: 20px; height: 20px; border-radius: var(--esm-radius-full); background: var(--esm-gradient-brand); color: var(--esm-text-on-gradient); }
    .msg.progress { align-items: baseline; padding-left: var(--esm-space-1); }
    .msg.progress p { color: var(--esm-text-muted); font: var(--esm-text-body-sm); }
    .msg.progress .dot { flex: none; width: 6px; height: 6px; border-radius: 50%; background: var(--esm-border-strong); transform: translateY(-1px); }
    .msg.error { padding: var(--esm-space-3); border-radius: var(--esm-radius-md); background: color-mix(in srgb, var(--esm-accent-red) 8%, var(--esm-surface-raised)); }
    .msg.error p { color: var(--esm-accent-red); }
    .msg.question { padding: var(--esm-space-3); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); }
    .options { display: flex; flex-wrap: wrap; gap: var(--esm-space-2); margin-top: var(--esm-space-2); }
    .chips { display: flex; flex-wrap: wrap; gap: var(--esm-space-1); margin-bottom: var(--esm-space-1); }
    .chip { display: inline-flex; align-items: center; gap: var(--esm-space-1); height: 22px; padding: 0 var(--esm-space-2); border-radius: var(--esm-radius-full); background: var(--esm-state-selected); color: var(--esm-accent-brand); font: var(--esm-text-body-sm); font-weight: 500; }
    .msg.user .chip { background: color-mix(in srgb, var(--esm-text-on-accent) 18%, transparent); color: inherit; }
    .chip button { display: grid; place-items: center; border: 0; background: transparent; padding: 0; color: inherit; }
    .steps { padding: var(--esm-space-3); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-xs); }
    .steps h3 { display: flex; align-items: center; gap: var(--esm-space-2); font: var(--esm-text-caption); letter-spacing: 0.04em; text-transform: uppercase; color: var(--esm-text-muted); margin: 0 0 var(--esm-space-2); }
    .steps ol { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 2px; }
    .steps li { display: flex; align-items: center; gap: var(--esm-space-2); padding: var(--esm-space-1) var(--esm-space-1) var(--esm-space-1) var(--esm-space-2); border-radius: var(--esm-radius-sm); font: var(--esm-text-body-sm); }
    .steps li:hover { background: var(--esm-state-hover); }
    .steps li.undone .summary { text-decoration: line-through; color: var(--esm-text-muted); }
    .summary { flex: 1; min-width: 0; overflow-wrap: anywhere; }
    .who { display: grid; place-items: center; flex: none; width: 20px; height: 20px; border-radius: var(--esm-radius-full); background: var(--esm-surface-sunken); color: var(--esm-text-muted); }
    .who.agent { background: var(--esm-state-selected); color: var(--esm-accent-brand); }
    .diag { display: flex; flex-direction: column; gap: var(--esm-space-1); padding: var(--esm-space-2) var(--esm-space-4); border-top: 1px solid var(--esm-border-subtle); font: var(--esm-text-body-sm); background: var(--esm-surface-base); }
    .diag p { display: flex; gap: var(--esm-space-2); margin: 0; }
    .diag esm-icon { margin-top: 2px; }
    .err { color: var(--esm-accent-red); }
    .warn { color: var(--esm-accent-amber); }
    .ok { display: flex; align-items: center; gap: var(--esm-space-2); color: var(--esm-accent-green); font-weight: 500; }
    .composer { display: flex; flex-direction: column; gap: var(--esm-space-2); padding: var(--esm-space-3) var(--esm-space-4) var(--esm-space-4); border-top: 1px solid var(--esm-border-subtle); }
    .box { display: flex; flex-direction: column; gap: var(--esm-space-2); padding: var(--esm-space-2) var(--esm-space-2) var(--esm-space-2) var(--esm-space-3); border: 1px solid var(--esm-border-input); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-xs);
      transition: border-color var(--esm-motion-fast) var(--esm-ease-out), box-shadow var(--esm-motion-fast) var(--esm-ease-out); }
    .box:focus-within { border-color: var(--esm-accent-brand); box-shadow: 0 0 0 3px var(--esm-state-ring); }
    .box textarea { width: 100%; min-height: 60px; resize: none; border: 0; outline: none; background: transparent; padding: var(--esm-space-1) 0 0; font: var(--esm-text-body); }
    .box textarea::placeholder { color: var(--esm-text-muted); }
    .box textarea:focus-visible { outline: none; }
    .box kbd { height: 1rem; min-width: 1rem; box-shadow: none; }
    .bar { display: flex; align-items: center; gap: var(--esm-space-2); }
    .scrim { position: fixed; inset: 0; background: var(--esm-scrim); backdrop-filter: blur(2px); z-index: 900; }
    .dialog { position: fixed; z-index: 901; top: 10vh; left: 50%; transform: translateX(-50%); width: min(580px, calc(100vw - 32px)); max-height: 80vh; overflow: auto; padding: var(--esm-space-6);
      border-radius: var(--esm-radius-xl); background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); box-shadow: var(--esm-shadow-overlay); }
    .dialog h2 { margin: 0; font: var(--esm-text-title-md); }
    .dialog h3 { margin: var(--esm-space-5) 0 var(--esm-space-2); font: var(--esm-text-caption); letter-spacing: 0.04em; text-transform: uppercase; color: var(--esm-text-muted); }
    .changes, .readiness { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--esm-space-2); font: var(--esm-text-body-sm); }
    .impact { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: var(--esm-space-2); font: var(--esm-text-body-sm); }
    .impact li { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: var(--esm-space-2) var(--esm-space-3); border-radius: var(--esm-radius-md); background: var(--esm-surface-sunken); }
    .readiness li, .changes li { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
    .hash { margin-top: var(--esm-space-4); }
    .actions { display: flex; justify-content: flex-end; gap: var(--esm-space-2); margin-top: var(--esm-space-6); padding-top: var(--esm-space-4); border-top: 1px solid var(--esm-border-subtle); }
  `,
})
export class ChatPanel {
  protected readonly builder = inject(BuilderState);
  protected readonly session = inject(Session);
  private readonly api = inject(Api);
  private readonly live = inject(Live);
  private readonly toasts = inject(Toasts);
  private readonly scrollRef = viewChild<ElementRef<HTMLElement>>('scroll');
  private readonly boxRef = viewChild<ElementRef<HTMLTextAreaElement>>('box');

  protected readonly examples = EXAMPLES;
  protected text = '';
  protected readonly items = signal<ChatItem[]>([]);
  protected readonly busy = signal(false);
  protected readonly review = signal(false);
  protected readonly publishing = signal(false);
  protected readonly planner = signal<string | null>(null);
  private conversationId: string | null = null;
  private nextId = 1;

  protected readonly steps = computed<DraftStep[]>(() => this.builder.draft()?.steps ?? []);
  protected readonly activeSteps = computed(() => this.steps().filter((s) => !s.undone));
  protected readonly errors = computed(() => (this.builder.draft()?.diagnostics ?? []).filter((d) => d.severity === 'error'));

  constructor() {
    const sub = this.live.assistant$.subscribe((e) => this.onEvent(e));
    inject(DestroyRef).onDestroy(() => sub.unsubscribe());
    effect(() => {
      this.items();
      this.steps();
      queueMicrotask(() => {
        const el = this.scrollRef()?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
      });
    });
    setTimeout(() => this.boxRef()?.nativeElement.focus());
    this.api.get<{ planner: string }>('/api/assistant/status').then((s) => this.planner.set(s.planner)).catch(() => undefined);
  }

  protected onKey(e: KeyboardEvent): void {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      void this.send();
    }
  }

  protected async send(): Promise<void> {
    const text = this.text.trim();
    if (!text || this.busy()) return;
    const selection = this.builder.selection();
    this.push({ role: 'user', text, chips: selection.map((s) => s.label) });
    this.text = '';
    this.busy.set(true);
    try {
      await this.live.start();
      const res = await this.api.post<{ conversationId: string }>('/api/assistant/messages', {
        conversationId: this.conversationId,
        text,
        selection: selection.map((s) => ({ view: s.view, node: s.nodeId, type: s.type, label: s.label })),
      });
      this.conversationId = res.conversationId;
    } catch (e) {
      this.busy.set(false);
      this.push({ role: 'error', text: e instanceof ApiError ? e.message : 'The assistant is unavailable right now.' });
    }
  }

  protected async stop(): Promise<void> {
    if (!this.conversationId) return;
    await this.api.post(`/api/assistant/conversations/${this.conversationId}/stop`).catch(() => undefined);
  }

  protected async discard(): Promise<void> {
    if (!window.confirm('Discard every change in this draft? The published pages stay as they are.')) return;
    await this.builder.discard();
  }

  protected async requestPublish(): Promise<void> {
    this.publishing.set(true);
    try {
      await this.api.post('/api/drafts/current/publish-requests');
      this.toasts.show('Publish requested. A builder with publish rights will review it.', 'success');
      this.review.set(false);
      await this.builder.refresh();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not request a publish.', 'warning');
    } finally {
      this.publishing.set(false);
    }
  }

  protected async publish(): Promise<void> {
    const d = this.builder.draft();
    if (!d?.artifactHash) return;
    this.publishing.set(true);
    try {
      const res = await this.api.post<{ revision: number }>('/api/config/publish', { artifactHash: d.artifactHash, expectedRevision: d.publishedRevision });
      this.review.set(false);
      this.builder.draft.set(null);
      await this.session.configChanged();
      this.builder.revision.update((r) => r + 1);
      this.toasts.show(`Published revision ${res.revision}. Everyone sees the new version now.`, 'success');
    } catch (e) {
      const msg = e instanceof ApiError ? e.message : 'Publishing failed.';
      this.toasts.show(msg, 'warning');
      await this.builder.refresh();
    } finally {
      this.publishing.set(false);
    }
  }

  private onEvent(e: AssistantEvent): void {
    if (this.conversationId && e.conversationId !== this.conversationId) return;
    this.conversationId ??= e.conversationId;
    switch (e.type) {
      case 'thinking':
      case 'tool':
      case 'plan':
        if (e.text) this.push({ role: 'progress', text: e.text });
        break;
      case 'step':
        if (e.targets?.length) this.builder.highlight(e.targets);
        if (e.text) this.push({ role: 'progress', text: '✓ ' + e.text });
        break;
      case 'message':
        if (e.text) this.push({ role: 'assistant', text: e.text });
        break;
      case 'question':
        this.push({ role: 'question', text: e.text ?? '', options: e.options });
        this.busy.set(false);
        break;
      case 'error':
        this.push({ role: 'error', text: e.text ?? 'Something went wrong.' });
        this.busy.set(false);
        break;
      case 'stopped':
        this.push({ role: 'progress', text: 'Stopped. Changes made so far stay in the draft; undo them individually below.' });
        this.busy.set(false);
        break;
      case 'done':
        if (e.text) this.push({ role: 'assistant', text: e.text });
        this.busy.set(false);
        void this.builder.refresh();
        break;
    }
  }

  private push(item: Omit<ChatItem, 'id'>): void {
    this.items.update((list) => [...list, { ...item, id: this.nextId++ }]);
  }
}
