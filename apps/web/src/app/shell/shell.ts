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
import { Icon, IconName } from '../ui/icon';

@Component({
  selector: 'esm-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, CommandPalette, KeyboardHelp, ChatPanel, Icon],
  template: `
    <div class="layout grid h-dvh grid-cols-[256px_minmax(0,1fr)] max-[900px]:grid-cols-1" [class.with-chat]="builder.open()">
      <nav class="sidebar flex flex-col gap-0.5 overflow-auto border-r border-subtle bg-nav px-3 pt-3 pb-3 max-[900px]:hidden" aria-label="Main">
        <button class="group flex w-full items-center gap-2.5 rounded-md p-1.5 text-left transition hover:bg-hover" (click)="router.navigate(['/workspaces'])" title="Switch workspace">
          <span class="brand-mark" aria-hidden="true"><esm-icon name="layers" [size]="16" [stroke]="2.25" /></span>
          <span class="flex min-w-0 flex-1 flex-col">
            <span class="truncate text-body font-semibold">{{ session.app()?.tenant?.name }}</span>
            <span class="flex items-center gap-1.5 text-caption text-muted">
              <span class="size-1.5 rounded-full" [class]="session.app()?.environment?.kind === 'production' ? 'bg-green' : 'bg-amber'"></span>
              {{ session.app()?.environment?.name }}
            </span>
          </span>
          <esm-icon name="chevrons-up-down" [size]="14" class="text-muted" />
          <span class="sr-only">Switch</span>
        </button>

        <button class="my-3 flex h-8 w-full items-center gap-2 overflow-hidden rounded-md whitespace-nowrap border border-subtle bg-raised px-2.5 text-left text-body-sm text-muted shadow-xs transition hover:border-strong" (click)="palette()?.toggle()">
          <esm-icon name="search" [size]="14" />
          <span class="flex-1 truncate">Search or run a command</span>
          <kbd>⌘K</kbd>
        </button>

        <div class="nav-group">Workspace</div>
        @for (d of session.app()?.dashboards ?? []; track d.view) {
          <a class="nav-item" [routerLink]="['/dashboards', d.view]" routerLinkActive="active"><esm-icon name="layout-dashboard" /><span class="flex-1">{{ d.label }}</span></a>
        }
        @if (session.app()?.catalog) {
          <a class="nav-item" routerLink="/catalog" routerLinkActive="active"><esm-icon name="store" /><span class="flex-1">Service catalog</span></a>
        }
        @if (session.app()?.approvals) {
          <a class="nav-item" routerLink="/approvals" routerLinkActive="active">
            <esm-icon name="stamp" /><span class="flex-1">Approvals</span>
            @if (session.pendingApprovals()) { <span class="nav-count" [attr.aria-label]="session.pendingApprovals() + ' waiting for you'">{{ session.pendingApprovals() }}</span> }
          </a>
        }
        @if (session.app()?.alerts) {
          <a class="nav-item" routerLink="/alerts" routerLinkActive="active">
            <esm-icon name="siren" /><span class="flex-1">Alerts</span>
            @if (session.openAlerts()) { <span class="nav-count" [attr.aria-label]="session.openAlerts() + ' open for you'">{{ session.openAlerts() }}</span> }
          </a>
        }
        <a class="nav-item" routerLink="/notifications" routerLinkActive="active">
          <esm-icon name="bell" /><span class="flex-1">Notifications</span>
          @if (session.unreadNotifications()) { <span class="nav-count" [attr.aria-label]="session.unreadNotifications() + ' unread'">{{ session.unreadNotifications() }}</span> }
        </a>
        @for (n of session.app()?.navigation ?? []; track n.entity) {
          <a class="nav-item" [routerLink]="['/records', n.entity]" routerLinkActive="active">
            <esm-icon [name]="navIcon(n.entity, n.requests)" />
            <span class="flex-1">{{ n.pluralLabel }}</span>
          </a>
        }
        @if (session.canCustomize() || session.isAdmin()) {
          <div class="nav-group">Build</div>
          @if (session.canCustomize()) {
            <button class="nav-item" (click)="builder.toggle()" [class.active]="builder.open()">
              <esm-icon name="sparkles" class="text-brand" />
              <span class="flex-1 text-left">AI builder</span>
              <kbd>⌘I</kbd>
            </button>
            <a class="nav-item" routerLink="/admin/configuration" routerLinkActive="active"><esm-icon name="sliders" /><span class="flex-1">Configuration</span></a>
          }
          @if (session.isAdmin()) {
            <a class="nav-item" routerLink="/admin/people" routerLinkActive="active"><esm-icon name="users" /><span class="flex-1">People and roles</span></a>
            <a class="nav-item" routerLink="/admin/operations" routerLinkActive="active"><esm-icon name="activity" /><span class="flex-1">Operations</span></a>
          }
          @if (session.canManageNotifications()) {
            <a class="nav-item" routerLink="/admin/notifications" routerLinkActive="active"><esm-icon name="send" /><span class="flex-1">Notification channels</span></a>
          }
        }
        <div class="flex-1"></div>
        <button class="nav-item mb-1 text-muted" (click)="help()?.open.set(true)"><esm-icon name="keyboard" /><span class="flex-1 text-left">Keyboard shortcuts</span><kbd>?</kbd></button>
        <div class="flex items-center gap-2 rounded-lg border border-subtle bg-raised p-2 shadow-xs">
          <span class="avatar" aria-hidden="true">{{ initials() }}</span>
          <span class="flex min-w-0 flex-1 flex-col">
            <span class="truncate text-body-sm font-medium">{{ session.app()?.user?.displayName }}</span>
            <span class="truncate text-caption text-muted">{{ session.app()?.tenant?.name }}</span>
          </span>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="theme.cycle()" [attr.aria-label]="'Theme: ' + theme.theme()" [title]="'Theme: ' + theme.theme()">
            <esm-icon [name]="theme.theme() === 'dark' ? 'moon' : theme.theme() === 'light' ? 'sun' : 'monitor'" [size]="15" />
          </button>
          <button class="btn btn-ghost btn-sm btn-icon" (click)="signOut()" aria-label="Sign out" title="Sign out"><esm-icon name="log-out" [size]="15" /></button>
        </div>
      </nav>
      <main class="content min-w-0 overflow-auto" id="main"><router-outlet /></main>
      @if (builder.open()) {
        <esm-chat-panel class="chat min-w-0 border-l border-subtle bg-raised max-[900px]:fixed max-[900px]:inset-0 max-[900px]:z-[800]" />
      }
    </div>
    <esm-command-palette />
    <esm-keyboard-help />
  `,
  styles: `
    .layout.with-chat { grid-template-columns: 256px minmax(0, 1fr) 420px; }
    @media (max-width: 900px) { .layout.with-chat { grid-template-columns: 1fr; } }
    .nav-group { margin: var(--esm-space-4) 0 var(--esm-space-1); padding: 0 var(--esm-space-2); font: var(--esm-text-caption); letter-spacing: 0.04em; text-transform: uppercase; color: var(--esm-text-muted); }
    .nav-item { position: relative; display: flex; align-items: center; gap: 10px; width: 100%; height: 32px; padding: 0 var(--esm-space-2); border-radius: var(--esm-radius-sm);
      color: var(--esm-text-default); font: var(--esm-text-body-sm); font-weight: 500; text-decoration: none; transition: background-color var(--esm-motion-fast) var(--esm-ease-out); }
    .nav-item esm-icon { color: var(--esm-text-muted); }
    .nav-item:hover { background: var(--esm-state-hover); text-decoration: none; }
    .nav-item.active { background: var(--esm-surface-raised); box-shadow: var(--esm-shadow-xs); outline: 1px solid var(--esm-border-subtle); outline-offset: -1px; }
    .nav-item.active esm-icon { color: var(--esm-accent-brand); }
    .nav-item kbd { box-shadow: none; }
    .nav-count { min-width: 20px; height: 18px; padding: 0 6px; border-radius: var(--esm-radius-full); display: inline-grid; place-items: center;
      background: var(--esm-accent-brand); color: var(--esm-text-on-accent); font: var(--esm-text-caption); font-weight: 600; }
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
    // Keep the notifications badge current while the app is visible (no push channel yet).
    const poll = setInterval(() => document.visibilityState === 'visible' && (this.session.refreshNotifications(), this.session.refreshAlerts()), 60_000);
    inject(DestroyRef).onDestroy(() => {
      off.forEach((f) => f());
      clearInterval(poll);
    });
  }

  protected navIcon(entity: string, requests?: boolean): IconName {
    if (requests) return 'package';
    if (this.session.app()?.navigation.some((n) => n.requests && entity.startsWith(n.entity))) return 'list-checks';
    return 'inbox';
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
