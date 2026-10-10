import { Component, inject } from '@angular/core';
import { Toasts } from '../core/toast';
import { Icon, IconName } from './icon';

@Component({
  selector: 'esm-toast-host',
  imports: [Icon],
  template: `
    <div class="host" aria-live="polite">
      @for (t of toasts.items(); track t.id) {
        <div class="toast" [class]="'tone-' + tone(t.tone)" [attr.role]="t.tone === 'critical' ? 'alert' : 'status'">
          <esm-icon [name]="icon(t.tone)" [size]="16" class="icon" />
          <span class="text">{{ t.text }}</span>
          @if (t.actionLabel) {
            <button class="btn btn-sm btn-ghost" (click)="t.action?.(); toasts.dismiss(t.id)">{{ t.actionLabel }}</button>
          }
          <button class="close" aria-label="Dismiss" (click)="toasts.dismiss(t.id)"><esm-icon name="x" [size]="14" /></button>
        </div>
      }
    </div>
  `,
  styles: `
    .host { position: fixed; bottom: var(--esm-space-5); left: 50%; transform: translateX(-50%); display: flex; flex-direction: column; gap: var(--esm-space-2); z-index: 1000; }
    .toast { display: flex; align-items: center; gap: var(--esm-space-3); min-width: 300px; max-width: 540px; padding: var(--esm-space-2) var(--esm-space-2) var(--esm-space-2) var(--esm-space-3);
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); box-shadow: var(--esm-shadow-overlay); font: var(--esm-text-body-sm);
      animation: in var(--esm-motion-base) var(--esm-ease-out); }
    .icon { color: var(--tone); }
    .text { flex: 1; font-weight: 500; }
    .close { display: grid; place-items: center; width: 24px; height: 24px; border: 0; border-radius: var(--esm-radius-sm); background: transparent; color: var(--esm-text-muted); }
    .close:hover { background: var(--esm-state-hover); }
    @keyframes in { from { opacity: 0; transform: translateY(6px) scale(0.98); } }
  `,
})
export class ToastHost {
  protected readonly toasts = inject(Toasts);

  protected icon(t: string): IconName {
    return ({ success: 'circle-check', warning: 'warning', critical: 'circle-alert' } as Record<string, IconName>)[t] ?? 'info';
  }

  protected tone(t: string): string {
    return { info: 'blue', success: 'green', warning: 'amber', critical: 'red' }[t] ?? 'gray';
  }
}
