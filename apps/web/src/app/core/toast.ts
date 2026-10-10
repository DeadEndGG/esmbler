import { Injectable, signal } from '@angular/core';

export interface Toast {
  id: number;
  tone: 'info' | 'success' | 'warning' | 'critical';
  text: string;
  actionLabel?: string;
  action?: () => void;
}

/** feedback.toast: short-lived messages; optimistic actions offer Undo here. */
@Injectable({ providedIn: 'root' })
export class Toasts {
  private next = 1;
  readonly items = signal<Toast[]>([]);

  show(text: string, tone: Toast['tone'] = 'info', action?: { label: string; run: () => void }, durationMs = 4500): void {
    const toast: Toast = { id: this.next++, tone, text, actionLabel: action?.label, action: action?.run };
    this.items.update((list) => [...list.slice(-3), toast]);
    setTimeout(() => this.dismiss(toast.id), durationMs);
  }

  dismiss(id: number): void {
    this.items.update((list) => list.filter((t) => t.id !== id));
  }
}
