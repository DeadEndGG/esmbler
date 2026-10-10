import { Component, inject, signal } from '@angular/core';
import { Shortcuts } from '../core/shortcuts';

@Component({
  selector: 'esm-keyboard-help',
  template: `
    @if (open()) {
      <div class="scrim" (click)="open.set(false)"></div>
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="kh" (keydown.escape)="open.set(false)" tabindex="-1">
        <h2 id="kh">Keyboard shortcuts</h2>
        <table>
          @for (s of shortcuts.list(); track s.keys + s.description) {
            <tr><td><kbd>{{ pretty(s.keys) }}</kbd></td><td>{{ s.description }}</td><td class="muted">{{ s.group }}</td></tr>
          }
        </table>
        <button class="btn" (click)="open.set(false)">Close</button>
      </div>
    }
  `,
  styles: `
    .scrim { position: fixed; inset: 0; background: rgb(0 0 0 / 25%); z-index: 900; }
    .dialog { position: fixed; top: 12vh; left: 50%; transform: translateX(-50%); width: min(520px, 92vw); z-index: 901; padding: var(--esm-space-6);
      background: var(--esm-surface-raised); border-radius: var(--esm-radius-lg); box-shadow: var(--esm-shadow-overlay); }
    h2 { margin: 0 0 var(--esm-space-4); font: var(--esm-text-title-md); }
    table { width: 100%; border-collapse: collapse; margin-bottom: var(--esm-space-4); }
    td { padding: 6px 0; font: var(--esm-text-body-sm); }
  `,
})
export class KeyboardHelp {
  protected readonly shortcuts = inject(Shortcuts);
  readonly open = signal(false);

  protected pretty(keys: string): string {
    return keys.replace('mod', '⌘/Ctrl').replace(/\+/g, ' + ').toUpperCase();
  }
}
