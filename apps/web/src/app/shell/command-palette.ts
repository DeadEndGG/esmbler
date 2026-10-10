import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { Session } from '../core/session';
import { ThemeService } from '../core/theme';
import { BuilderState } from '../builder/builder-state';
import { Auth } from '../core/auth';

interface Command {
  id: string;
  label: string;
  hint?: string;
  section: string;
  run: () => void;
}

/** action.command_palette: Cmd/Ctrl+K. Lists only what the viewer may do; fuzzy search. */
@Component({
  selector: 'esm-command-palette',
  template: `
    @if (open()) {
      <div class="scrim" (click)="close()"></div>
      <div class="palette" role="dialog" aria-modal="true" aria-label="Command palette">
        <input #input class="search" placeholder="Type a command or search…" [value]="query()" (input)="query.set($any($event.target).value); active.set(0)"
               (keydown)="onKey($event)" role="combobox" aria-expanded="true" aria-controls="cmd-list" [attr.aria-activedescendant]="'cmd-' + active()" />
        <ul id="cmd-list" role="listbox">
          @for (c of filtered(); track c.id; let i = $index) {
            @if (i === 0 || filtered()[i - 1].section !== c.section) {
              <li class="section" role="presentation">{{ c.section }}</li>
            }
            <li [id]="'cmd-' + i" role="option" [attr.aria-selected]="i === active()" [class.active]="i === active()" (click)="run(c)" (mousemove)="active.set(i)">
              <span>{{ c.label }}</span>
              @if (c.hint) { <kbd>{{ c.hint }}</kbd> }
            </li>
          } @empty {
            <li class="empty muted">No matching commands</li>
          }
        </ul>
      </div>
    }
  `,
  styles: `
    .scrim { position: fixed; inset: 0; background: rgb(0 0 0 / 25%); z-index: 900; }
    .palette { position: fixed; top: 14vh; left: 50%; transform: translateX(-50%); width: min(560px, 92vw); z-index: 901;
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); box-shadow: var(--esm-shadow-overlay); overflow: hidden;
      animation: pop var(--esm-motion-fast) var(--esm-ease-out); }
    .search { width: 100%; border: 0; border-bottom: 1px solid var(--esm-border-subtle); padding: var(--esm-space-4); background: transparent; font: var(--esm-text-title-sm); font-weight: 400; outline: none; }
    ul { list-style: none; margin: 0; padding: var(--esm-space-2); max-height: 50vh; overflow: auto; }
    li { display: flex; justify-content: space-between; align-items: center; padding: var(--esm-space-2) var(--esm-space-3); border-radius: var(--esm-radius-sm); cursor: pointer; }
    li.active { background: var(--esm-state-selected); }
    .section { font: var(--esm-text-body-sm); color: var(--esm-text-muted); cursor: default; padding-top: var(--esm-space-3); }
    @keyframes pop { from { opacity: 0; transform: translate(-50%, -4px); } }
  `,
})
export class CommandPalette {
  private readonly router = inject(Router);
  private readonly session = inject(Session);
  private readonly theme = inject(ThemeService);
  private readonly builder = inject(BuilderState);
  private readonly auth = inject(Auth);
  private readonly input = viewChild<ElementRef<HTMLInputElement>>('input');
  readonly open = signal(false);
  protected readonly query = signal('');
  protected readonly active = signal(0);

  constructor() {
    effect(() => {
      if (this.open()) setTimeout(() => this.input()?.nativeElement.focus());
    });
  }

  private readonly commands = computed<Command[]>(() => {
    const app = this.session.app();
    const list: Command[] = [];
    for (const n of app?.navigation ?? []) {
      list.push({ id: 'go:' + n.entity, label: `Go to ${n.pluralLabel}`, hint: n.entity === 'core.tickets.ticket' ? 'G T' : undefined, section: 'Navigate', run: () => this.router.navigate(['/records', n.entity]) });
      if (n.canCreate) list.push({ id: 'new:' + n.entity, label: `New ${n.label.toLowerCase()}`, hint: 'C', section: 'Create', run: () => this.router.navigate(['/records', n.entity, 'new']) });
    }
    if (this.session.canCustomize()) {
      list.push({ id: 'builder', label: this.builder.open() ? 'Close AI builder' : 'Customize this page with AI', hint: '⌘ I', section: 'Build', run: () => void this.builder.toggle() });
      list.push({ id: 'config', label: 'Configuration history and upgrades', section: 'Build', run: () => this.router.navigate(['/admin/configuration']) });
    }
    if (this.session.isAdmin()) list.push({ id: 'people', label: 'People, groups, and roles', section: 'Admin', run: () => this.router.navigate(['/admin/people']) });
    list.push({ id: 'theme', label: 'Toggle theme (system, dark, light)', section: 'Preferences', run: () => this.theme.cycle() });
    list.push({ id: 'switch', label: 'Switch workspace', section: 'Preferences', run: () => this.router.navigate(['/workspaces']) });
    list.push({ id: 'logout', label: 'Sign out', section: 'Preferences', run: () => void this.auth.logout().then(() => this.router.navigate(['/login'])) });
    return list;
  });

  protected readonly filtered = computed(() => {
    const q = this.query().trim().toLowerCase();
    if (!q) return this.commands();
    return this.commands()
      .map((c) => ({ c, score: fuzzy(c.label.toLowerCase(), q) }))
      .filter((x) => x.score > 0)
      .sort((a, b) => b.score - a.score)
      .map((x) => x.c);
  });

  toggle(): void {
    this.open.update((o) => !o);
    this.query.set('');
    this.active.set(0);
  }

  close(): void {
    this.open.set(false);
  }

  protected run(c: Command): void {
    this.close();
    c.run();
  }

  protected onKey(e: KeyboardEvent): void {
    const n = this.filtered().length;
    if (e.key === 'ArrowDown') {
      this.active.update((a) => Math.min(n - 1, a + 1));
      e.preventDefault();
    } else if (e.key === 'ArrowUp') {
      this.active.update((a) => Math.max(0, a - 1));
      e.preventDefault();
    } else if (e.key === 'Enter') {
      const c = this.filtered()[this.active()];
      if (c) this.run(c);
    } else if (e.key === 'Escape') {
      this.close();
    }
  }
}

function fuzzy(text: string, q: string): number {
  if (text.includes(q)) return 100 - text.indexOf(q);
  let ti = 0;
  let score = 0;
  for (const ch of q) {
    const found = text.indexOf(ch, ti);
    if (found < 0) return 0;
    score += found === ti ? 3 : 1;
    ti = found + 1;
  }
  return score;
}
