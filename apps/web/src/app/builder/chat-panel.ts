import { Component, DestroyRef, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from '../core/api';
import { AssistantEvent, Live } from '../core/live';
import { DraftStep } from '../core/models';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { BuilderState } from './builder-state';

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
  imports: [FormsModule],
  template: `
    <aside class="panel" aria-label="AI builder">
      <header class="head">
        <div>
          <strong>AI builder</strong>
          <span class="muted status">
            @if (busy()) { Working… } @else if (builder.draft(); as d) { Draft · {{ activeSteps().length }} change{{ activeSteps().length === 1 ? '' : 's' }} } @else { Changes go to a private draft }
          </span>
        </div>
        <button class="btn btn-ghost btn-sm" (click)="builder.close()" aria-label="Close the AI builder">✕</button>
      </header>

      <div class="scroll" #scroll aria-live="polite">
        @if (!items().length) {
          <div class="intro">
            <p>Describe what you want to change. Edits appear live on the page as a draft; nothing changes for others until you publish.</p>
            <p class="muted">Tip: click blocks on the page to point at them (Shift-click for several).</p>
            @for (ex of examples; track ex) { <button class="example" (click)="text = ex; send()">{{ ex }}</button> }
          </div>
        }
        @for (m of items(); track m.id) {
          <div class="msg" [class]="m.role">
            @if (m.chips?.length) { <div class="chips">@for (c of m.chips; track c) { <span class="chip">{{ c }}</span> }</div> }
            <p>{{ m.text }}</p>
            @if (m.options?.length) {
              <div class="options">@for (o of m.options; track o) { <button class="btn btn-sm" (click)="text = o; send()">{{ o }}</button> }</div>
            }
          </div>
        }

        @if (steps().length) {
          <section class="steps" aria-label="Draft changes">
            <h3>Changes in this draft</h3>
            <ol>
              @for (s of steps(); track s.seq) {
                <li [class.undone]="s.undone" (mouseenter)="builder.highlight(s.targets)" (focusin)="builder.highlight(s.targets)">
                  <span class="who" [title]="s.byAgent ? 'Made by the AI' : 'Made by a person'">{{ s.byAgent ? '✦' : '●' }}</span>
                  <span class="summary">{{ s.summary }}</span>
                  @if (s.undone) {
                    <button class="btn btn-ghost btn-sm" (click)="builder.redo(s.seq)">Redo</button>
                  } @else {
                    <button class="btn btn-ghost btn-sm" (click)="builder.undo(s.seq)" [attr.aria-label]="'Undo: ' + s.summary">Undo</button>
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
              <p [class]="x.severity === 'error' ? 'err' : 'warn'">{{ x.message }}@if (x.suggestion) { <span class="muted"> {{ x.suggestion }}</span> }</p>
            }
          </div>
        }
      }

      <footer class="composer">
        @if (builder.selection().length) {
          <div class="chips sel">
            @for (s of builder.selection(); track s.nodeId) {
              <span class="chip">{{ s.label }} <button aria-label="Remove from selection" (click)="builder.toggleSelect(s, true)">×</button></span>
            }
          </div>
        }
        <textarea class="input" rows="3" [(ngModel)]="text" (keydown)="onKey($event)" placeholder="Ask for a change…" aria-label="Message to the AI builder" #box></textarea>
        <div class="bar">
          @if (busy()) {
            <button class="btn" (click)="stop()">Stop</button>
          } @else {
            <button class="btn btn-primary" (click)="send()" [disabled]="!text.trim()">Send <kbd>↵</kbd></button>
          }
          <span class="flex"></span>
          @if (builder.draft()) {
            <button class="btn btn-ghost btn-sm" (click)="discard()">Discard draft</button>
            <button class="btn btn-sm" [class.btn-primary]="!!builder.pendingPublish()" (click)="review.set(true)" [disabled]="!activeSteps().length">
              {{ session.canPublish() ? 'Review and publish' : 'Request publish' }}
            </button>
          }
        </div>
      </footer>
    </aside>

    @if (review()) {
      <div class="scrim" (click)="review.set(false)"></div>
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="pub-title" (keydown.escape)="review.set(false)">
        <h2 id="pub-title">Publish changes</h2>
        @if (builder.pendingPublish(); as p) {
          <p class="muted">{{ p.requestedByAgent ? 'The AI requested' : 'Requested' }} publishing: {{ p.summary }}</p>
        }
        @let d = builder.draft();
        @if (d) {
          <h3>Readiness</h3>
          @if (errors().length) {
            <ul class="readiness">@for (x of errors(); track $index) { <li class="err">{{ x.message }}</li> }</ul>
          } @else {
            <p class="ok">✓ The draft compiles and passes validation.</p>
          }
          @if (d.permissionImpact) { <p class="warn">This draft changes who can see or edit data. Publishing requires permission administration.</p> }
          <h3>What changes</h3>
          <ul class="changes">
            @for (c of d.changes; track c.kind + c.id) {
              <li><span class="badge" [class]="c.change === 'added' ? 'tone-green' : c.change === 'removed' ? 'tone-red' : 'tone-blue'">{{ c.change }}</span> {{ c.kind }} <strong>{{ c.label }}</strong>@if (c.properties.length) { <span class="muted"> ({{ c.properties.join(', ') }})</span> }</li>
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
    :host { display: block; height: 100vh; }
    .panel { display: flex; flex-direction: column; height: 100%; background: var(--esm-surface-base); }
    .head { display: flex; justify-content: space-between; align-items: center; padding: var(--esm-space-3) var(--esm-space-4); border-bottom: 1px solid var(--esm-border-subtle); }
    .status { display: block; font: var(--esm-text-body-sm); }
    .scroll { flex: 1; overflow: auto; padding: var(--esm-space-4); display: flex; flex-direction: column; gap: var(--esm-space-3); }
    .intro p { margin: 0 0 var(--esm-space-3); }
    .example { display: block; width: 100%; text-align: left; margin-bottom: var(--esm-space-2); padding: var(--esm-space-2) var(--esm-space-3); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); background: var(--esm-surface-raised); cursor: pointer; font: var(--esm-text-body-sm); }
    .example:hover { background: var(--esm-state-hover); }
    .msg p { margin: 0; white-space: pre-wrap; }
    .msg.user { align-self: flex-end; max-width: 85%; padding: var(--esm-space-2) var(--esm-space-3); border-radius: var(--esm-radius-md); background: var(--esm-state-selected); }
    .msg.progress p { color: var(--esm-text-muted); font: var(--esm-text-body-sm); }
    .msg.error p { color: var(--esm-accent-red); }
    .msg.question { padding: var(--esm-space-3); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); }
    .options { display: flex; flex-wrap: wrap; gap: var(--esm-space-2); margin-top: var(--esm-space-2); }
    .chips { display: flex; flex-wrap: wrap; gap: var(--esm-space-1); margin-bottom: var(--esm-space-1); }
    .chip { display: inline-flex; align-items: center; gap: var(--esm-space-1); padding: 0 var(--esm-space-2); border-radius: var(--esm-radius-full); background: var(--esm-state-selected); font: var(--esm-text-body-sm); }
    .chip button { border: 0; background: transparent; cursor: pointer; padding: 0; }
    .steps h3 { font: var(--esm-text-title-sm); margin: var(--esm-space-2) 0; }
    .steps ol { list-style: none; margin: 0; padding: 0; }
    .steps li { display: flex; align-items: center; gap: var(--esm-space-2); padding: var(--esm-space-1) var(--esm-space-2); border-radius: var(--esm-radius-sm); font: var(--esm-text-body-sm); }
    .steps li:hover { background: var(--esm-state-hover); }
    .steps li.undone .summary { text-decoration: line-through; color: var(--esm-text-muted); }
    .summary { flex: 1; }
    .who { color: var(--esm-accent-purple); }
    .diag { padding: var(--esm-space-2) var(--esm-space-4); border-top: 1px solid var(--esm-border-subtle); font: var(--esm-text-body-sm); }
    .diag p { margin: 0; }
    .err { color: var(--esm-accent-red); }
    .warn { color: var(--esm-accent-amber); }
    .ok { color: var(--esm-accent-green); }
    .composer { padding: var(--esm-space-3) var(--esm-space-4); border-top: 1px solid var(--esm-border-subtle); }
    .composer textarea { width: 100%; resize: vertical; }
    .bar { display: flex; align-items: center; gap: var(--esm-space-2); margin-top: var(--esm-space-2); }
    .flex { flex: 1; }
    .scrim { position: fixed; inset: 0; background: rgb(0 0 0 / 0.35); z-index: 900; }
    .dialog { position: fixed; z-index: 901; top: 10vh; left: 50%; transform: translateX(-50%); width: min(560px, calc(100vw - 32px)); max-height: 80vh; overflow: auto; padding: var(--esm-space-5); border-radius: var(--esm-radius-lg); background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-overlay); }
    .dialog h2 { margin: 0 0 var(--esm-space-3); font: var(--esm-text-title-md); }
    .dialog h3 { margin: var(--esm-space-4) 0 var(--esm-space-2); font: var(--esm-text-title-sm); }
    .changes, .readiness { margin: 0; padding-left: var(--esm-space-4); font: var(--esm-text-body-sm); }
    .changes li { margin-bottom: var(--esm-space-1); }
    .hash { margin-top: var(--esm-space-3); }
    .actions { display: flex; justify-content: flex-end; gap: var(--esm-space-2); margin-top: var(--esm-space-5); }
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
