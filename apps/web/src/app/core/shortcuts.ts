import { Injectable, NgZone, OnDestroy, inject, signal } from '@angular/core';

export interface Shortcut {
  /** e.g. "mod+k", "g t", "?", "j", "mod+i", "mod+z", "mod+shift+z", "alt+arrowup" */
  keys: string;
  description: string;
  group: string;
  run: (e: KeyboardEvent) => void;
  /** Single-key shortcuts are ignored while typing (WCAG 2.1.4). */
  allowInInput?: boolean;
}

/** Keyboard-first registry (docs/explanation/design-principles.md, shortcut map). */
@Injectable({ providedIn: 'root' })
export class Shortcuts implements OnDestroy {
  private readonly registered = new Map<number, Shortcut>();
  private nextId = 1;
  private pending: string | null = null;
  private pendingTimer: ReturnType<typeof setTimeout> | null = null;
  readonly helpOpen = signal(false);
  private readonly zone = inject(NgZone);
  private readonly handler = (e: KeyboardEvent) => this.onKey(e);

  constructor() {
    document.addEventListener('keydown', this.handler);
  }

  ngOnDestroy(): void {
    document.removeEventListener('keydown', this.handler);
  }

  register(shortcut: Shortcut): () => void {
    const id = this.nextId++;
    this.registered.set(id, shortcut);
    return () => this.registered.delete(id);
  }

  list(): Shortcut[] {
    return [...this.registered.values()];
  }

  private onKey(e: KeyboardEvent): void {
    const target = e.target as HTMLElement | null;
    const typing = !!target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName));
    const key = e.key.toLowerCase();
    const combo = [e.metaKey || e.ctrlKey ? 'mod' : '', e.shiftKey && key.length > 1 ? 'shift' : '', e.altKey ? 'alt' : '', key].filter(Boolean).join('+');
    const sequence = this.pending ? `${this.pending} ${combo}` : null;
    for (const s of this.registered.values()) {
      const matches = s.keys === combo || (sequence !== null && s.keys === sequence);
      if (!matches) continue;
      const single = !s.keys.includes('mod');
      if (typing && single && !s.allowInInput) continue;
      e.preventDefault();
      this.clearPending();
      this.zone.run(() => s.run(e));
      return;
    }
    if (!typing && !e.metaKey && !e.ctrlKey && [...this.registered.values()].some((s) => s.keys.startsWith(combo + ' '))) {
      this.pending = combo;
      this.pendingTimer = setTimeout(() => this.clearPending(), 900);
    }
  }

  private clearPending(): void {
    this.pending = null;
    if (this.pendingTimer) clearTimeout(this.pendingTimer);
  }
}
