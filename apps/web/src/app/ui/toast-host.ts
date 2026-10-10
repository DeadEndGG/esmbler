import { Component, inject } from '@angular/core';
import { Toasts } from '../core/toast';

@Component({
  selector: 'esm-toast-host',
  template: `
    <div class="host" aria-live="polite">
      @for (t of toasts.items(); track t.id) {
        <div class="toast" [class]="'tone-' + tone(t.tone)" [attr.role]="t.tone === 'critical' ? 'alert' : 'status'">
          <span class="dot"></span>
          <span class="text">{{ t.text }}</span>
          @if (t.actionLabel) {
            <button class="btn btn-sm btn-ghost" (click)="t.action?.(); toasts.dismiss(t.id)">{{ t.actionLabel }}</button>
          }
          <button class="close" aria-label="Dismiss" (click)="toasts.dismiss(t.id)">×</button>
        </div>
      }
    </div>
  `,
  styles: `
    .host { position: fixed; bottom: var(--esm-space-4); left: 50%; transform: translateX(-50%); display: flex; flex-direction: column; gap: var(--esm-space-2); z-index: 1000; }
    .toast { display: flex; align-items: center; gap: var(--esm-space-2); min-width: 280px; max-width: 520px; padding: var(--esm-space-2) var(--esm-space-3);
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); box-shadow: var(--esm-shadow-overlay);
      animation: in var(--esm-motion-base) var(--esm-ease-out); }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--tone); flex: none; }
    .text { flex: 1; }
    .close { border: 0; background: transparent; color: var(--esm-text-muted); cursor: pointer; font-size: 1rem; }
    @keyframes in { from { opacity: 0; transform: translateY(6px); } }
  `,
})
export class ToastHost {
  protected readonly toasts = inject(Toasts);

  protected tone(t: string): string {
    return { info: 'blue', success: 'green', warning: 'amber', critical: 'red' }[t] ?? 'gray';
  }
}
