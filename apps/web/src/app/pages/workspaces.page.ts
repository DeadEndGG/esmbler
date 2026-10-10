import { Component, inject, signal } from '@angular/core';
import { Api } from '../core/api';
import { Router, RouterLink } from '@angular/router';
import { Auth } from '../core/auth';
import { Session } from '../core/session';
import { Icon } from '../ui/icon';

/** Explicit tenant/environment switch (REQ-TEN-003): never inferred from a link or record ID. */
@Component({
  selector: 'esm-workspaces',
  imports: [Icon, RouterLink],
  template: `
    <main class="backdrop-glow grid min-h-dvh place-items-center p-6">
      <section class="surface w-full max-w-[520px] p-6" aria-labelledby="t">
        <div class="mb-5 flex items-center gap-3">
          <span class="brand-mark" aria-hidden="true"><esm-icon name="layers" [size]="16" [stroke]="2.25" /></span>
          <div>
            <h1 id="t" class="text-title-md">Choose a workspace</h1>
            <p class="text-body-sm text-muted">Pick the organization and environment to work in.</p>
          </div>
        </div>
        <ul class="flex flex-col gap-2.5">
          @for (w of auth.workspaces(); track w.tenantId) {
            <li class="flex items-center justify-between gap-3 rounded-md border border-subtle bg-base p-3 transition hover:border-strong">
              <div class="flex min-w-0 items-center gap-3">
                <span class="grid size-9 flex-none place-items-center rounded-md bg-selected text-brand"><esm-icon name="building" [size]="18" /></span>
                <div class="min-w-0">
                  <div class="truncate text-body font-semibold">{{ w.name }}</div>
                  <div class="mono text-muted">{{ w.slug }}</div>
                </div>
              </div>
              <div class="flex flex-wrap justify-end gap-2">
                @for (e of w.environments; track e.environmentId) {
                  <button class="btn btn-sm" (click)="open(w.slug, e.key)">{{ e.name }} <esm-icon name="chevron-right" [size]="14" /></button>
                }
              </div>
            </li>
          } @empty {
            <li class="rounded-md border border-dashed border-strong p-6 text-center text-muted">You are not a member of any workspace yet.{{ canCreate() ? ' Create one to get started.' : ' Ask an administrator to invite you.' }}</li>
          }
        </ul>
        <div class="mt-5 flex justify-between gap-2 border-t border-subtle pt-4">
          @if (canCreate()) {
            <a class="btn btn-sm" routerLink="/workspaces/new"><esm-icon name="plus" [size]="14" /> Create a workspace</a>
          } @else {
            <span></span>
          }
          <button class="btn btn-ghost btn-sm" (click)="signOut()"><esm-icon name="log-out" [size]="14" /> Sign out</button>
        </div>
      </section>
    </main>
  `,
})
export class WorkspacesPage {
  protected readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly session = inject(Session);
  /** Self-service creation is an operator setting (off by default, ADR 0020). */
  protected readonly canCreate = signal(false);

  constructor() {
    inject(Api)
      .get<{ enabled: boolean }>('/api/starters')
      .then((r) => this.canCreate.set(r.enabled), () => this.canCreate.set(false));
  }

  protected async open(slug: string, env: string): Promise<void> {
    await this.auth.switchTo(slug, env);
    this.session.app.set(null);
    await this.router.navigateByUrl('/');
  }

  protected async signOut(): Promise<void> {
    await this.auth.logout();
    await this.router.navigateByUrl('/login');
  }
}
