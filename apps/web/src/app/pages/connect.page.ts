import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Icon } from '../ui/icon';

interface ConsentEnvironment {
  id: string;
  key: string;
  name: string;
  kind: string;
  allowed: boolean;
}

interface Consent {
  clientName: string;
  clientHost: string;
  redirectHost: string;
  scopes: string[];
  offeredScopes: string[];
  policy: 'off' | 'non_production' | 'all';
  environments: ConsentEnvironment[];
}

const SCOPES: Record<string, string> = {
  'config:read': 'Read this workspace\'s configuration (fields, rules, pages)',
  'config:edit': 'Change the draft configuration (you review and publish)',
  'config:publish:request': 'Ask a person to publish a draft',
  'access:explain': 'Explain who can see or do what',
  'records:read': 'Read records you can see (ticket text goes to the assistant\'s provider)',
};

/**
 * Connect an external assistant (ADR 0025): a signed-in person sees which assistant asks, from which host, chooses the
 * environment and what it may do, and approves. The server checks the workspace policy and the person's own rights; the
 * assistant never gets more than the person, never publishes, and the connection can be revoked at any time.
 */
@Component({
  selector: 'esm-connect-page',
  imports: [FormsModule, Icon],
  template: `
    <div class="admin-page max-w-[720px]">
      <header class="page-head">
        <span class="page-icon" aria-hidden="true"><esm-icon name="sparkles" [size]="18" /></span>
        <div class="flex-1"><h1 class="text-title-lg">Connect an assistant</h1></div>
      </header>
      @if (error()) { <div class="callout tone-red mb-3" role="alert">{{ error() }}</div> }
      @if (consent(); as c) {
        <section class="card-item grid gap-4" aria-labelledby="who">
          <h2 id="who" class="text-title-sm"><strong>{{ c.clientName }}</strong> ({{ c.clientHost }}) wants to work in {{ workspace() }}</h2>
          <div class="callout tone-amber" role="note">Only continue if you started this from {{ c.clientName }} just now. You will be sent back to {{ c.redirectHost }}.</div>
          @if (c.policy === 'off') {
            <p class="callout tone-red" role="alert">This workspace does not allow external assistants. An administrator can change this under AI assistant settings.</p>
          } @else {
            <fieldset class="grid gap-2">
              <legend class="text-body-sm font-medium">Environment</legend>
              @for (e of c.environments; track e.id) {
                <label class="flex items-center gap-2 text-body-sm">
                  <input type="radio" name="environment" [value]="e.id" [(ngModel)]="environment" [disabled]="!e.allowed" />
                  {{ e.name }} ({{ e.kind }}){{ e.allowed ? '' : ': not allowed by the workspace' }}
                </label>
              }
            </fieldset>
            <fieldset class="grid gap-2">
              <legend class="text-body-sm font-medium">The assistant may</legend>
              @for (s of c.offeredScopes; track s) {
                <label class="flex items-center gap-2 text-body-sm">
                  <input type="checkbox" [name]="s" [checked]="chosen().has(s)" [disabled]="s === 'config:read'" (change)="toggle(s)" />
                  {{ label(s) }}
                </label>
              }
            </fieldset>
            <p class="text-body-sm text-muted">What you can see in this configuration will be sent to {{ c.clientName }} under your own account's terms with its provider. It never publishes; you review its draft here. You can end the connection under Connected assistants.</p>
            <div class="flex justify-end gap-2">
              <button class="btn btn-ghost" type="button" (click)="cancel()">Cancel</button>
              <button class="btn btn-primary" type="button" (click)="approve()" [disabled]="!environment || busy()">Allow</button>
            </div>
          }
        </section>
      } @else if (!error()) {
        <div class="card-item" aria-busy="true"><span class="skeleton block h-4 w-1/2"></span></div>
      }
    </div>
  `,
  styles: `
    .card-item { padding: var(--esm-space-4); border-radius: var(--esm-radius-lg); border: 1px solid var(--esm-border-subtle); background: var(--esm-surface-raised); }
    fieldset { border: 0; padding: 0; margin: 0; }
  `,
})
export class ConnectPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly session = inject(Session);
  private readonly request = inject(ActivatedRoute).snapshot.queryParamMap.get('request') ?? '';

  protected readonly consent = signal<Consent | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly chosen = signal(new Set<string>());
  protected readonly workspace = computed(() => this.session.app()?.tenant?.name ?? 'this workspace');
  protected environment: string | null = null;

  constructor() {
    void this.load();
  }

  protected label(scope: string): string {
    return SCOPES[scope] ?? scope;
  }

  protected toggle(scope: string): void {
    this.chosen.update((set) => {
      const next = new Set(set);
      if (next.has(scope)) next.delete(scope);
      else next.add(scope);
      return next;
    });
  }

  private async load(): Promise<void> {
    try {
      const c = await this.api.get<Consent>('/api/oauth/consent', { request: this.request });
      this.consent.set(c);
      this.chosen.set(new Set(c.scopes.filter((s) => c.offeredScopes.includes(s) && s !== 'records:read').concat('config:read')));
      this.environment = c.environments.find((e) => e.allowed && e.kind !== 'production')?.id ?? c.environments.find((e) => e.allowed)?.id ?? null;
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'This connection request could not be read.');
    }
  }

  protected async approve(): Promise<void> {
    this.busy.set(true);
    try {
      const result = await this.api.post<{ redirect: string }>('/api/oauth/consent', { request: this.request, environmentId: this.environment, scopes: [...this.chosen()] });
      window.location.assign(result.redirect);
    } catch (e) {
      this.error.set(e instanceof ApiError ? e.message : 'The assistant was not connected.');
      this.busy.set(false);
    }
  }

  protected cancel(): void {
    void this.router.navigateByUrl('/');
  }
}
