import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from '../core/api';
import { DraftEvent, Live } from '../core/live';
import { DraftState, PublishRequestView } from '../core/models';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';

export interface Selection {
  nodeId: string;
  type: string;
  label: string;
  view: string;
}

/**
 * Live-editing state (REQ-LIVE-*): whether the chat panel and draft mode are on, the current draft, the selection
 * attached to the chat (point-and-prompt), and highlights for elements the latest steps touched.
 */
@Injectable({ providedIn: 'root' })
export class BuilderState {
  private readonly api = inject(Api);
  private readonly live = inject(Live);
  private readonly session = inject(Session);
  private readonly toasts = inject(Toasts);

  readonly open = signal(false);
  readonly draft = signal<DraftState | null>(null);
  readonly selection = signal<Selection[]>([]);
  readonly flash = signal<Set<string>>(new Set());
  readonly focusedStep = signal<number | null>(null);
  readonly pendingPublish = signal<PublishRequestView | null>(null);
  /** Incremented on every draft change so pages reload their draft spec. */
  readonly revision = signal(0);
  readonly lastEventAt = signal(0);
  readonly draftMode = computed(() => this.open() && this.session.canCustomize());

  constructor() {
    this.live.draft$.subscribe((e) => void this.onEvent(e));
  }

  async toggle(): Promise<void> {
    if (this.open()) {
      await this.close();
    } else {
      await this.start();
    }
  }

  async start(): Promise<void> {
    if (!this.session.canCustomize()) {
      this.toasts.show("You don't have builder access in this workspace.", 'warning');
      return;
    }
    this.open.set(true);
    await this.live.watchDraft().catch(() => this.toasts.show('Live updates are unavailable; changes appear when you refresh.', 'warning'));
    await this.refresh();
  }

  async close(): Promise<void> {
    this.open.set(false);
    this.selection.set([]);
    await this.live.unwatchDraft();
  }

  async ensureDraft(): Promise<DraftState> {
    const existing = this.draft();
    if (existing) return existing;
    const d = await this.api.post<DraftState>('/api/drafts');
    this.draft.set(d);
    return d;
  }

  async refresh(): Promise<void> {
    const [draft, pending] = await Promise.all([
      this.api.get<DraftState | null>('/api/drafts/current').catch(() => null),
      this.session.canPublish() ? this.api.get<PublishRequestView | null>('/api/config/publish-requests/pending').catch(() => null) : Promise.resolve(null),
    ]);
    this.draft.set(draft ?? null);
    this.pendingPublish.set(pending ?? null);
    this.revision.update((r) => r + 1);
  }

  toggleSelect(sel: Selection, additive: boolean): void {
    const list = this.selection();
    const exists = list.some((s) => s.nodeId === sel.nodeId);
    if (additive) this.selection.set(exists ? list.filter((s) => s.nodeId !== sel.nodeId) : [...list, sel]);
    else this.selection.set(exists && list.length === 1 ? [] : [sel]);
  }

  highlight(targets: string[]): void {
    const set = new Set(targets.flatMap((t) => [t, t.includes('/') ? t.split('/')[1] : t]));
    this.flash.set(set);
    setTimeout(() => {
      if (this.flash() === set) this.flash.set(new Set());
    }, 2600);
  }

  /** Undoes one step, or with `wholeChange` every step of the change it belongs to. */
  async undo(seq: number, wholeChange = false): Promise<void> {
    const d = this.draft();
    if (!d) return;
    try {
      this.draft.set(await this.api.post<DraftState>(`/api/drafts/current/steps/${seq}/undo`, { expectedVersion: d.version, wholeChange }));
      this.revision.update((r) => r + 1);
    } catch (e: any) {
      this.toasts.show(e.message ?? 'Could not undo.', 'warning');
      await this.refresh();
    }
  }

  async redo(seq: number, wholeChange = false): Promise<void> {
    const d = this.draft();
    if (!d) return;
    try {
      this.draft.set(await this.api.post<DraftState>(`/api/drafts/current/steps/${seq}/redo`, { expectedVersion: d.version, wholeChange }));
      this.revision.update((r) => r + 1);
    } catch (e: any) {
      this.toasts.show(e.message ?? 'Could not redo.', 'warning');
    }
  }

  async discard(): Promise<void> {
    await this.api.delete('/api/drafts/current');
    this.draft.set(null);
    this.revision.update((r) => r + 1);
    this.toasts.show('Draft discarded. The published pages are unchanged.', 'info');
  }

  private async onEvent(e: DraftEvent): Promise<void> {
    this.lastEventAt.set(performance.now());
    if (e.targets?.length) this.highlight(e.targets);
    if (e.k === 'published') {
      await this.session.configChanged();
      this.toasts.show(`Published revision ${e.r}.`, 'success');
    }
    await this.refresh();
  }
}
