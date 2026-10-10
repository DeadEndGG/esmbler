import { Component, ElementRef, computed, effect, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { Session } from '../core/session';
import { ThemeService } from '../core/theme';
import { BuilderState } from '../builder/builder-state';
import { Auth } from '../core/auth';
import { Icon, IconName } from '../ui/icon';

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
  imports: [Icon],
  template: `
    @if (open()) {
      <div class="scrim" (click)="close()"></div>
      <div class="palette" role="dialog" aria-modal="true" aria-label="Command palette">
        <div class="search-row"><esm-icon name="search" [size]="18" />
        <input #input class="search" placeholder="Type a command or search…" [value]="query()" (input)="query.set($any($event.target).value); active.set(0)"
               (keydown)="onKey($event)" role="combobox" aria-expanded="true" aria-controls="cmd-list" [attr.aria-activedescendant]="'cmd-' + active()" />
        <kbd>Esc</kbd></div>
        <ul id="cmd-list" role="listbox">
          @for (c of filtered(); track c.id; let i = $index) {
            @if (i === 0 || filtered()[i - 1].section !== c.section) {
              <li class="section" role="presentation">{{ c.section }}</li>
            }
            <li [id]="'cmd-' + i" role="option" [attr.aria-selected]="i === active()" [class.active]="i === active()" (click)="run(c)" (mousemove)="active.set(i)">
              <span class="label"><esm-icon [name]="iconFor(c)" [size]="15" />{{ c.label }}</span>
              @if (c.hint) { <kbd>{{ c.hint }}</kbd> } @else if (i === active()) { <esm-icon name="enter" [size]="14" class="muted" /> }
            </li>
          } @empty {
            <li class="empty muted">No matching commands</li>
          }
        </ul>
        <div class="foot"><span><kbd>↑</kbd> <kbd>↓</kbd> to move</span><span><kbd>↵</kbd> to run</span></div>
      </div>
    }
  `,
  styles: `
    .scrim { position: fixed; inset: 0; background: var(--esm-scrim); backdrop-filter: blur(2px); z-index: 900; }
    .palette { position: fixed; top: 14vh; left: 50%; transform: translateX(-50%); width: min(600px, 92vw); z-index: 901;
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-xl); box-shadow: var(--esm-shadow-overlay); overflow: hidden;
      animation: pop var(--esm-motion-fast) var(--esm-ease-out); }
    .search-row { display: flex; align-items: center; gap: var(--esm-space-3); padding: 0 var(--esm-space-4); border-bottom: 1px solid var(--esm-border-subtle); color: var(--esm-text-muted); }
    .search { flex: 1; border: 0; padding: var(--esm-space-4) 0; background: transparent; font: var(--esm-text-title-sm); font-weight: 400; color: var(--esm-text-default); outline: none; }
    .search::placeholder { color: var(--esm-text-muted); }
    .search:focus-visible { outline: none; }
    ul { list-style: none; margin: 0; padding: var(--esm-space-2); max-height: 50vh; overflow: auto; }
    li { display: flex; justify-content: space-between; align-items: center; min-height: 38px; padding: 0 var(--esm-space-3); border-radius: var(--esm-radius-md); cursor: pointer; font: var(--esm-text-body); }
    .label { display: flex; align-items: center; gap: var(--esm-space-3); }
    .label esm-icon { color: var(--esm-text-muted); }
    li.active { background: var(--esm-state-selected); }
    li.active .label esm-icon { color: var(--esm-accent-brand); }
    .section { min-height: 0; font: var(--esm-text-caption); letter-spacing: 0.04em; text-transform: uppercase; color: var(--esm-text-muted); cursor: default; padding: var(--esm-space-3) var(--esm-space-3) var(--esm-space-1); }
    .empty { justify-content: center; padding: var(--esm-space-6); cursor: default; }
    .foot { display: flex; gap: var(--esm-space-4); padding: var(--esm-space-2) var(--esm-space-4); border-top: 1px solid var(--esm-border-subtle); background: var(--esm-surface-base); font: var(--esm-text-caption); color: var(--esm-text-muted); }
    .foot kbd { box-shadow: none; }
    @keyframes pop { from { opacity: 0; transform: translate(-50%, -4px) scale(0.99); } }
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
    list.push({ id: 'go:notifications', label: 'Go to Notifications', section: 'Navigate', run: () => this.router.navigate(['/notifications']) });
    for (const d of app?.dashboards ?? []) {
      list.push({ id: 'dash:' + d.view, label: `Go to ${d.label}`, section: 'Navigate', run: () => this.router.navigate(['/dashboards', d.view]) });
    }
    for (const n of app?.navigation ?? []) {
      list.push({ id: 'go:' + n.entity, label: `Go to ${n.pluralLabel}`, hint: n.entity === 'core.tickets.ticket' ? 'G T' : undefined, section: 'Navigate', run: () => this.router.navigate(['/records', n.entity]) });
      if (n.canCreate) list.push({ id: 'new:' + n.entity, label: `New ${n.label.toLowerCase()}`, hint: 'C', section: 'Create', run: () => (n.requests ? this.router.navigate(['/catalog']) : this.router.navigate(['/records', n.entity, 'new'])) });
    }
    if (app?.catalog) list.push({ id: 'catalog', label: 'Request something from the service catalog', section: 'Create', run: () => this.router.navigate(['/catalog']) });
    if (app?.approvals) list.push({ id: 'approvals', label: 'Go to approvals waiting for me', section: 'Navigate', run: () => this.router.navigate(['/approvals']) });
    if (this.session.canCustomize()) {
      list.push({ id: 'builder', label: this.builder.open() ? 'Close AI builder' : 'Customize this page with AI', hint: '⌘ I', section: 'Build', run: () => void this.builder.toggle() });
      list.push({ id: 'config', label: 'Configuration history and upgrades', section: 'Build', run: () => this.router.navigate(['/admin/configuration']) });
    }
    if (this.session.isAdmin()) list.push({ id: 'people', label: 'People, groups, and roles', section: 'Admin', run: () => this.router.navigate(['/admin/people']) });
    if (this.session.isAdmin()) list.push({ id: 'operations', label: 'Operations: queues and dead letters', section: 'Admin', run: () => this.router.navigate(['/admin/operations']) });
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

  protected iconFor(c: Command): IconName {
    if (c.id.startsWith('go:')) return 'inbox';
    if (c.id.startsWith('new:')) return 'plus';
    return ({ catalog: 'store', approvals: 'stamp', builder: 'sparkles', config: 'sliders', people: 'users', operations: 'activity', theme: 'moon', switch: 'building', logout: 'log-out' } as Record<string, IconName>)[c.id] ?? 'command';
  }

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
