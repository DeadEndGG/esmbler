import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Workspace } from './models';

interface TokenResponse {
  accessToken: string;
  expiresAt: string;
  workspaces?: Workspace[];
  user?: { id: string; name: string };
}

const WORKSPACE_KEY = 'esm.workspace';

/**
 * Session handling (ADR 0011): the short-lived access token lives in memory only; the rotating refresh token is an
 * HttpOnly cookie the script never sees. The chosen workspace is remembered so a reload returns to it.
 */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);
  private refreshing: Promise<boolean> | null = null;

  readonly token = signal<string | null>(null);
  readonly workspaces = signal<Workspace[]>([]);
  readonly userName = signal<string>('');
  readonly workspace = signal<{ tenant: string; environment: string } | null>(null);
  readonly signedIn = computed(() => this.token() !== null);
  readonly inWorkspace = computed(() => this.signedIn() && this.workspace() !== null && this.tenantScoped());
  private readonly tenantScoped = signal(false);

  async login(email: string, password: string): Promise<void> {
    const res = await firstValueFrom(this.http.post<TokenResponse>('/api/auth/login', { email, password }));
    this.token.set(res.accessToken);
    this.tenantScoped.set(false);
    this.workspaces.set(res.workspaces ?? []);
    this.userName.set(res.user?.name ?? '');
  }

  async switchTo(tenant: string, environment?: string): Promise<void> {
    const res = await firstValueFrom(this.http.post<TokenResponse & { tenant: any; environment: any }>('/api/auth/switch', { tenant, environment }));
    this.token.set(res.accessToken);
    this.tenantScoped.set(true);
    const ws = { tenant, environment: res.environment?.key ?? environment ?? 'prod' };
    this.workspace.set(ws);
    try {
      localStorage.setItem(WORKSPACE_KEY, JSON.stringify(ws));
    } catch {
      /* storage unavailable: the user picks again after reload */
    }
  }

  /** Restores a session from the refresh cookie (page reload). */
  async restore(): Promise<boolean> {
    const ok = await this.refresh();
    if (!ok) return false;
    let saved: { tenant: string; environment: string } | null = null;
    try {
      saved = JSON.parse(localStorage.getItem(WORKSPACE_KEY) ?? 'null');
    } catch {
      saved = null;
    }
    if (saved && this.workspaces().some((w) => w.slug === saved!.tenant)) {
      if (!this.tenantScoped()) await this.switchTo(saved.tenant, saved.environment);
      else this.workspace.set(saved);
      return true;
    }
    return true;
  }

  refresh(): Promise<boolean> {
    this.refreshing ??= (async () => {
      try {
        const res = await firstValueFrom(
          this.http.post<TokenResponse>('/api/auth/refresh', {}, { headers: new HttpHeaders({ 'X-Esmbler-Csrf': '1' }) }),
        );
        this.token.set(res.accessToken);
        this.workspaces.set(res.workspaces ?? this.workspaces());
        this.tenantScoped.set(this.decodeTenant(res.accessToken));
        this.userName.set(this.decodeName(res.accessToken));
        return true;
      } catch {
        this.token.set(null);
        return false;
      } finally {
        this.refreshing = null;
      }
    })();
    return this.refreshing;
  }

  async logout(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/api/auth/logout', {}, { headers: new HttpHeaders({ 'X-Esmbler-Csrf': '1' }) }));
    } finally {
      this.token.set(null);
      this.workspace.set(null);
      this.tenantScoped.set(false);
    }
  }

  private claims(token: string): Record<string, unknown> {
    try {
      return JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
    } catch {
      return {};
    }
  }

  private decodeTenant(token: string): boolean {
    return typeof this.claims(token)['tid'] === 'string';
  }

  private decodeName(token: string): string {
    return (this.claims(token)['name'] as string) ?? '';
  }
}
