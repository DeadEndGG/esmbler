import { Component, DestroyRef, inject, signal, viewChild } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../core/auth';
import { Session } from '../core/session';
import { Shortcuts } from '../core/shortcuts';
import { ThemeService } from '../core/theme';
import { BuilderState } from '../builder/builder-state';
import { ChatPanel } from '../builder/chat-panel';
import { CommandPalette } from './command-palette';
import { KeyboardHelp } from './keyboard-help';

@Component({
  selector: 'esm-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, CommandPalette, KeyboardHelp, ChatPanel],
  template: `
    <div class="layout" [class.with-chat]="builder.open()">
      <nav class="sidebar" aria-label="Main">
        <div class="brand"><span class="mark" aria-hidden="true">E</span> <span>{{ session.app()?.tenant?.name }}</span></div>
        <button class="env" (click)="router.navigate(['/workspaces'])" title="Switch workspace">
          <span class="badge" [class]="session.app()?.environment?.kind === 'production' ? 'tone-green' : 'tone-amber'">{{ session.app()?.environment?.name }}</span>
          <span class="muted">Switch</span>
        </button>
        <button class="search" (click)="palette()?.toggle()"><span>Search or run a command</span><kbd>⌘K</kbd></button>
        <div class="group">Workspace</div>
        @for (n of session.app()?.navigation ?? []; track n.entity) {
          <a [routerLink]="['/records', n.entity]" routerLinkActive="active">{{ n.pluralLabel }}</a>
        }
        @if (session.canCustomize() || session.isAdmin()) {
          <div class="group">Build</div>
          @if (session.canCustomize()) {
            <button class="link" (click)="builder.toggle()" [class.active]="builder.open()">✦ AI builder <kbd>⌘I</kbd></button>
            <a routerLink="/admin/configuration" routerLinkActive="active">Configuration</a>
          }
          @if (session.isAdmin()) {
            <a routerLink="/admin/people" routerLinkActive="active">People and roles</a>
          }
        }
        <div class="spacer"></div>
        <div class="me">
          <span class="avatar" aria-hidden="true">{{ initials() }}</span>
          <span class="name">{{ session.app()?.user?.displayName }}</span>
          <button class="btn btn-ghost btn-sm" (click)="theme.cycle()" [attr.aria-label]="'Theme: ' + theme.theme()">{{ theme.theme() === 'dark' ? '☾' : theme.theme() === 'light' ? '☀' : '◐' }}</button>
          <button class="btn btn-ghost btn-sm" (click)="signOut()">Sign out</button>
        </div>
      </nav>
      <main class="content" id="main"><router-outlet /></main>
      @if (builder.open()) {
        <esm-chat-panel class="chat" />
      }
    </div>
    <esm-command-palette />
    <esm-keyboard-help />
  `,
  styles: `
    .layout { display: grid; grid-template-columns: 232px 1fr; height: 100vh; }
    .layout.with-chat { grid-template-columns: 232px 1fr 400px; }
    .sidebar { display: flex; flex-direction: column; gap: 2px; padding: var(--esm-space-3); border-right: 1px solid var(--esm-border-subtle); background: var(--esm-surface-base); overflow: auto; }
    .brand { display: flex; align-items: center; gap: var(--esm-space-2); font: var(--esm-text-title-sm); padding: var(--esm-space-1) var(--esm-space-2) var(--esm-space-2); }
    .mark { display: grid; place-items: center; width: 22px; height: 22px; border-radius: 6px; background: var(--esm-accent-brand); color: var(--esm-text-on-accent); font-size: 0.8rem; }
    .env, .search, .link { display: flex; align-items: center; justify-content: space-between; gap: var(--esm-space-2); width: 100%; padding: 6px var(--esm-space-2); border: 0; border-radius: var(--esm-radius-sm); background: transparent; cursor: pointer; text-align: left; font: var(--esm-text-body-sm); }
    .search { border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); color: var(--esm-text-muted); margin: var(--esm-space-2) 0; }
    .group { margin-top: var(--esm-space-4); padding: 0 var(--esm-space-2) var(--esm-space-1); font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    a, .link { display: flex; padding: 6px var(--esm-space-2); border-radius: var(--esm-radius-sm); color: var(--esm-text-default); font: var(--esm-text-body-sm); font-weight: 500; text-decoration: none; }
    a:hover, .link:hover, .env:hover { background: var(--esm-state-hover); text-decoration: none; }
    a.active, .link.active { background: var(--esm-state-selected); }
    .spacer { flex: 1; }
    .me { display: flex; align-items: center; gap: var(--esm-space-2); padding-top: var(--esm-space-3); border-top: 1px solid var(--esm-border-subtle); }
    .avatar { display: grid; place-items: center; width: 24px; height: 24px; border-radius: 50%; background: var(--esm-state-selected); font: var(--esm-text-body-sm); font-weight: 600; flex: none; }
    .name { flex: 1; font: var(--esm-text-body-sm); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .content { overflow: auto; min-width: 0; }
    .chat { border-left: 1px solid var(--esm-border-subtle); min-width: 0; }
    @media (max-width: 900px) {
      .layout, .layout.with-chat { grid-template-columns: 1fr; }
      .sidebar { display: none; }
      .chat { position: fixed; inset: 0; z-index: 800; background: var(--esm-surface-base); }
    }
  `,
})
export class Shell {
  protected readonly session = inject(Session);
  protected readonly builder = inject(BuilderState);
  protected readonly theme = inject(ThemeService);
  protected readonly router = inject(Router);
  private readonly auth = inject(Auth);
  protected readonly palette = viewChild(CommandPalette);
  protected readonly help = viewChild(KeyboardHelp);

  constructor() {
    const shortcuts = inject(Shortcuts);
    const off = [
      shortcuts.register({ keys: 'mod+k', description: 'Open the command palette', group: 'Global', run: () => this.palette()?.toggle() }),
      shortcuts.register({ keys: 'mod+i', description: 'Open or close the AI builder', group: 'Global', run: () => void this.builder.toggle() }),
      shortcuts.register({ keys: '?', description: 'Show keyboard shortcuts', group: 'Global', run: () => this.help()?.open.set(true) }),
      shortcuts.register({ keys: 'g t', description: 'Go to Tickets', group: 'Global', run: () => this.router.navigate(['/records', 'core.tickets.ticket']) }),
      shortcuts.register({ keys: 'g h', description: 'Go home', group: 'Global', run: () => this.router.navigate(['/']) }),
    ];
    inject(DestroyRef).onDestroy(() => off.forEach((f) => f()));
  }

  protected initials(): string {
    return (this.session.app()?.user.displayName ?? '?').split(' ').map((p) => p[0]).slice(0, 2).join('');
  }

  protected async signOut(): Promise<void> {
    await this.auth.logout();
    this.session.app.set(null);
    await this.router.navigateByUrl('/login');
  }
}
