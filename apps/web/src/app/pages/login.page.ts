import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Auth } from '../core/auth';
import { Icon } from '../ui/icon';

@Component({
  selector: 'esm-login',
  imports: [FormsModule, Icon],
  template: `
    <main class="grid min-h-dvh lg:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)]">
      <section class="relative hidden overflow-hidden bg-hero p-12 text-on-hero lg:flex lg:flex-col" aria-label="About Esmbler">
        <div class="backdrop-hero pointer-events-none absolute inset-0" aria-hidden="true"></div>
        <div class="relative flex items-center gap-2.5 text-title-sm">
          <span class="brand-mark" aria-hidden="true"><esm-icon name="layers" [size]="16" [stroke]="2.25" /></span> Esmbler
        </div>
        <div class="preview relative my-auto max-w-md" aria-hidden="true">
          <div class="glass p-4">
            <div class="flex items-center gap-2 text-caption text-on-hero-muted"><esm-icon name="ticket" [size]="13" /> TCK-000004</div>
            <div class="mt-1 text-body font-semibold">Email not syncing on mobile</div>
            <div class="mt-3 flex gap-2"><span class="pill pill-purple">In progress</span><span class="pill pill-red">Critical</span></div>
            <div class="mt-4 grid grid-cols-2 gap-3 text-body-sm">
              <div><div class="text-caption text-on-hero-muted">Assignee</div>Alex Rivera</div>
              <div class="added"><div class="text-caption text-on-hero-muted">Customer impact <span class="new">New</span></div>Sales team blocked</div>
            </div>
          </div>
          <div class="glass bubble mt-3 ml-12 flex gap-2.5 p-3 text-body-sm">
            <span class="brand-mark size-6 flex-none rounded-full"><esm-icon name="sparkles" [size]="12" /></span>
            <span>Added a required “Customer impact” field to the Details card. It’s in your draft; publish when you’re happy.</span>
          </div>
        </div>
        <div class="relative max-w-md">
          <p class="text-caption tracking-widest text-on-hero-muted uppercase">Service management</p>
          <h2 class="mt-3 text-title-xl text-balance">Assemble the service desk your teams need, by asking for it.</h2>
          <ul class="mt-8 flex flex-col gap-5">
            <li class="flex gap-3">
              <span class="hero-icon"><esm-icon name="sparkles" [size]="16" /></span>
              <span><strong class="block text-body font-semibold">Customize by chatting</strong><span class="text-body-sm text-on-hero-muted">Fields, layouts and rules change live as the AI builder works.</span></span>
            </li>
            <li class="flex gap-3">
              <span class="hero-icon"><esm-icon name="shield" [size]="16" /></span>
              <span><strong class="block text-body font-semibold">Validated and reversible</strong><span class="text-body-sm text-on-hero-muted">Every change is a draft you review, publish and roll back.</span></span>
            </li>
            <li class="flex gap-3">
              <span class="hero-icon"><esm-icon name="zap" [size]="16" /></span>
              <span><strong class="block text-body font-semibold">Upgrades keep your changes</strong><span class="text-body-sm text-on-hero-muted">Module updates merge with what you built.</span></span>
            </li>
          </ul>
        </div>
      </section>

      <div class="relative backdrop-glow grid place-items-center p-6">
        <form class="surface flex w-full max-w-[380px] flex-col gap-4 p-8" (ngSubmit)="submit()" aria-labelledby="title">
          <div class="flex items-center gap-2.5 text-title-sm lg:hidden" aria-hidden="true"><span class="brand-mark"><esm-icon name="layers" [size]="16" [stroke]="2.25" /></span> Esmbler</div>
          <div>
            <h1 id="title" class="text-title-lg">Sign in</h1>
            <p class="mt-1 text-body-sm text-muted">Welcome back. Use your work email.</p>
          </div>
          <label class="field-label">Email <input class="input" name="email" type="email" autocomplete="username" [(ngModel)]="email" required autofocus /></label>
          <label class="field-label">Password <input class="input" name="password" type="password" autocomplete="current-password" [(ngModel)]="password" required /></label>
          @if (error()) {
            <p class="callout tone-red text-body-sm" role="alert"><esm-icon name="circle-alert" [size]="14" class="self-center text-red" />{{ error() }}</p>
          }
          <button class="btn btn-primary mt-1 h-10" type="submit" [disabled]="busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
@if (demo(); as d) {
            <p class="rounded-md border border-dashed border-strong bg-sunken px-3 py-2 text-body-sm text-muted">Demo: <span class="mono text-fg">{{ d.email }}</span> / <span class="mono text-fg">{{ d.password }}</span></p>
          }
        </form>
      </div>
    </main>
  `,
  styles: `
    .field-label { display: flex; flex-direction: column; gap: 6px; font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-default); }
    .glass { border-radius: var(--esm-radius-lg); color: var(--esm-text-on-hero); background: color-mix(in srgb, var(--esm-text-on-hero) 7%, transparent);
      box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--esm-text-on-hero) 14%, transparent), var(--esm-shadow-overlay); backdrop-filter: blur(12px); }
    .preview { transform: perspective(1200px) rotateY(-4deg) rotateX(2deg); }
    .pill { display: inline-flex; align-items: center; gap: 6px; height: 22px; padding: 0 9px; border-radius: var(--esm-radius-full); font: var(--esm-text-caption); font-weight: 600; }
    .pill::before { content: ""; width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
    .pill-purple { color: var(--esm-text-on-hero); background: color-mix(in srgb, var(--esm-accent-purple) 45%, transparent); }
    .pill-red { color: var(--esm-text-on-hero); background: color-mix(in srgb, var(--esm-accent-red) 45%, transparent); }
    .added { padding: 6px var(--esm-space-2); margin: -6px calc(-1 * var(--esm-space-2)); border-radius: var(--esm-radius-sm); box-shadow: 0 0 0 2px color-mix(in srgb, var(--esm-text-on-hero) 35%, transparent); }
    .new { margin-left: 4px; padding: 0 6px; border-radius: var(--esm-radius-full); background: var(--esm-gradient-brand); color: var(--esm-text-on-gradient); font: var(--esm-text-caption); font-weight: 600; }
    @media (prefers-reduced-motion: reduce) { .preview { transform: none; } }
    .hero-icon { display: grid; place-items: center; flex: none; width: 32px; height: 32px; border-radius: var(--esm-radius-sm);
      background: color-mix(in srgb, var(--esm-text-on-hero) 10%, transparent); box-shadow: inset 0 0 0 1px color-mix(in srgb, var(--esm-text-on-hero) 16%, transparent); }
  `,
})
export class LoginPage {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  protected email = '';
  protected password = '';
  protected readonly error = signal('');
  protected readonly busy = signal(false);
  /** Shown only when the server seeded demo data (KI-011). */
  protected readonly demo = signal<{ email: string; password: string } | null>(null);

  constructor() {
    void this.auth.signInOptions().then((o) => this.demo.set(o.demoAccount ?? null)).catch(() => this.demo.set(null));
  }

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.email, this.password);
      const ws = this.auth.workspaces();
      if (ws.length === 1) {
        await this.auth.switchTo(ws[0].slug);
        let next = '/';
        try {
          next = sessionStorage.getItem('esm.afterLogin') ?? '/';
          sessionStorage.removeItem('esm.afterLogin');
        } catch {
          next = '/';
        }
        await this.router.navigateByUrl(next.startsWith('/connect?') ? next : '/');
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
