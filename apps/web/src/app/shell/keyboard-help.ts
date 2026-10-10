import { Component, inject, signal } from '@angular/core';
import { Shortcuts } from '../core/shortcuts';
import { Icon } from '../ui/icon';

@Component({
  selector: 'esm-keyboard-help',
  imports: [Icon],
  template: `
    @if (open()) {
      <div class="scrim" (click)="open.set(false)"></div>
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="kh" (keydown.escape)="open.set(false)" tabindex="-1">
        <div class="head">
          <span class="icon" aria-hidden="true"><esm-icon name="keyboard" [size]="16" /></span>
          <h2 id="kh">Keyboard shortcuts</h2>
        </div>
        <table>
          @for (s of shortcuts.list(); track s.keys + s.description) {
            <tr><td class="keys">@for (k of pretty(s.keys); track $index) { <kbd>{{ k }}</kbd> }</td><td>{{ s.description }}</td><td class="muted group">{{ s.group }}</td></tr>
          }
        </table>
        <div class="actions"><button class="btn" (click)="open.set(false)">Close</button></div>
      </div>
    }
  `,
  styles: `
    .scrim { position: fixed; inset: 0; background: var(--esm-scrim); backdrop-filter: blur(2px); z-index: 900; }
    .dialog { position: fixed; top: 12vh; left: 50%; transform: translateX(-50%); width: min(560px, 92vw); max-height: 76vh; overflow: auto; z-index: 901; padding: var(--esm-space-6);
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-xl); box-shadow: var(--esm-shadow-overlay); }
    .head { display: flex; align-items: center; gap: var(--esm-space-3); margin-bottom: var(--esm-space-4); }
    .icon { display: grid; place-items: center; width: 32px; height: 32px; border-radius: var(--esm-radius-md); background: var(--esm-state-selected); color: var(--esm-accent-brand); }
    h2 { margin: 0; font: var(--esm-text-title-md); }
    table { width: 100%; border-collapse: collapse; }
    td { padding: var(--esm-space-2) 0; font: var(--esm-text-body-sm); border-bottom: 1px solid var(--esm-border-subtle); }
    tr:last-child td { border-bottom: 0; }
    .keys { display: flex; gap: var(--esm-space-1); width: 150px; }
    .group { text-align: right; }
    .actions { display: flex; justify-content: flex-end; margin-top: var(--esm-space-4); }
  `,
})
export class KeyboardHelp {
  protected readonly shortcuts = inject(Shortcuts);
  readonly open = signal(false);

  protected pretty(keys: string): string[] {
    return keys.split(/[+ ]/).map((k) => (k === 'mod' ? '⌘/Ctrl' : k === 'enter' ? '↵' : k.length === 1 ? k.toUpperCase() : k[0].toUpperCase() + k.slice(1)));
  }
}
