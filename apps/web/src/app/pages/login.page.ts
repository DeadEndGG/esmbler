import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth } from '../core/auth';

@Component({
  selector: 'esm-login',
  imports: [FormsModule],
  template: `
    <main class="wrap">
      <form class="panel" (ngSubmit)="submit()" aria-labelledby="title">
        <div class="brand" aria-hidden="true"><span class="mark">E</span> Esmbler</div>
        <h1 id="title">Sign in</h1>
        <label>Email <input class="input" name="email" type="email" autocomplete="username" [(ngModel)]="email" required autofocus /></label>
        <label>Password <input class="input" name="password" type="password" autocomplete="current-password" [(ngModel)]="password" required /></label>
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button class="btn btn-primary" type="submit" [disabled]="busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
        <p class="hint muted">Demo: <span class="mono">alex&#64;acme.example</span> / <span class="mono">Esmbler!2026</span></p>
      </form>
    </main>
  `,
  styles: `
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: var(--esm-space-4); }
    .panel { width: min(360px, 100%); display: flex; flex-direction: column; gap: var(--esm-space-3); padding: var(--esm-space-8);
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); }
    .brand { display: flex; align-items: center; gap: var(--esm-space-2); font: var(--esm-text-title-sm); }
    .mark { display: grid; place-items: center; width: 24px; height: 24px; border-radius: 6px; background: var(--esm-accent-brand); color: var(--esm-text-on-accent); }
    h1 { margin: 0; font: var(--esm-text-title-lg); }
    label { display: flex; flex-direction: column; gap: var(--esm-space-1); font: var(--esm-text-body-sm); color: var(--esm-text-muted); }
    .error { color: var(--esm-accent-red); margin: 0; }
    .btn { justify-content: center; height: 36px; }
    .hint { font: var(--esm-text-body-sm); margin: 0; }
  `,
})
export class LoginPage {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  protected email = '';
  protected password = '';
  protected readonly error = signal('');
  protected readonly busy = signal(false);

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.email, this.password);
      const ws = this.auth.workspaces();
      if (ws.length === 1) {
        await this.auth.switchTo(ws[0].slug);
        await this.router.navigateByUrl('/');
      } else {
        await this.router.navigateByUrl('/workspaces');
      }
    } catch (e: any) {
      this.error.set(e?.error?.title ?? "That email and password don't match.");
    } finally {
      this.busy.set(false);
    }
  }
}
