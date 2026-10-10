import { Component, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Auth } from '../core/auth';
import { Session } from '../core/session';

/** Explicit tenant/environment switch (REQ-TEN-003): never inferred from a link or record ID. */
@Component({
  selector: 'esm-workspaces',
  template: `
    <main class="wrap">
      <section class="panel" aria-labelledby="t">
        <h1 id="t">Choose a workspace</h1>
        <ul>
          @for (w of auth.workspaces(); track w.tenantId) {
            <li>
              <div class="name">{{ w.name }} <span class="muted mono">{{ w.slug }}</span></div>
              <div class="envs">
                @for (e of w.environments; track e.environmentId) {
                  <button class="btn" (click)="open(w.slug, e.key)">{{ e.name }}</button>
                }
              </div>
            </li>
          } @empty {
            <li class="muted">You are not a member of any workspace yet.</li>
          }
        </ul>
        <button class="btn btn-ghost" (click)="signOut()">Sign out</button>
      </section>
    </main>
  `,
  styles: `
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: var(--esm-space-4); }
    .panel { width: min(480px, 100%); padding: var(--esm-space-6); background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); }
    h1 { font: var(--esm-text-title-md); margin: 0 0 var(--esm-space-4); }
    ul { list-style: none; padding: 0; margin: 0 0 var(--esm-space-4); display: flex; flex-direction: column; gap: var(--esm-space-3); }
    li { display: flex; justify-content: space-between; align-items: center; gap: var(--esm-space-3); padding: var(--esm-space-3); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); }
    .envs { display: flex; gap: var(--esm-space-2); }
  `,
})
export class WorkspacesPage {
  protected readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly session = inject(Session);

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
