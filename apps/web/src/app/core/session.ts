import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api';
import { AppBootstrap, EntityClientSpec, ViewClientSpec } from './models';

/** Workspace bootstrap and cached, revision-keyed client configuration. */
@Injectable({ providedIn: 'root' })
export class Session {
  private readonly api = inject(Api);
  private readonly views = new Map<string, Promise<ViewClientSpec>>();
  private readonly entities = new Map<string, Promise<EntityClientSpec>>();

  readonly app = signal<AppBootstrap | null>(null);
  readonly canCustomize = computed(() => this.app()?.actions.includes('config.edit') ?? false);
  /** Exporting lists and figures is its own right (records.export); the server checks it again. */
  readonly canExport = computed(() => this.app()?.actions.includes('records.export') ?? false);
  readonly canPublish = computed(() => this.app()?.actions.includes('config.publish') ?? false);
  readonly canManageModules = computed(() => this.app()?.actions.includes('modules.manage') ?? false);
  readonly isAdmin = computed(() => this.app()?.actions.includes('permissions.admin') ?? false);
  /** Approval steps waiting for the signed-in person (navigation badge, ADR 0016). */
  readonly pendingApprovals = signal(0);
  /** Unread notifications of the signed-in person (navigation badge, ADR 0021). */
  readonly unreadNotifications = signal(0);
  readonly openAlerts = signal(0);
  readonly canReadAudit = computed(() => this.app()?.actions.some((a) => a === 'audit.read' || a === 'permissions.admin') ?? false);
  readonly canManageNotifications = computed(() => this.app()?.actions.some((a) => a === 'notifications.manage' || a === 'permissions.admin') ?? false);

  async load(): Promise<AppBootstrap> {
    const app = await this.api.get<AppBootstrap>('/api/app');
    if (this.app()?.configRevision !== app.configRevision) {
      this.views.clear();
      this.entities.clear();
    }
    this.app.set(app);
    this.refreshApprovals();
    this.refreshNotifications();
    this.refreshAlerts();
    return app;
  }

  /** Re-reads the approvals badge; failures leave the last value. */
  refreshApprovals(): void {
    if (!this.app()?.approvals) {
      this.pendingApprovals.set(0);
      return;
    }
    this.api.get<{ pending: number }>('/api/approvals/count').then((c) => this.pendingApprovals.set(c.pending), () => undefined);
  }

  /** Re-reads the open alerts badge (alerts that are the user's to answer); failures leave the last value. */
  refreshAlerts(): void {
    if (!this.app()?.alerts) {
      this.openAlerts.set(0);
      return;
    }
    this.api.get<{ open: number }>('/api/alerts/count').then((c) => this.openAlerts.set(c.open), () => undefined);
  }

  /** Re-reads the unread notifications badge; failures leave the last value. */
  refreshNotifications(): void {
    this.api.get<{ unread: number }>('/api/me/notifications/unread-count').then((c) => this.unreadNotifications.set(c.unread), () => undefined);
  }

  /** Call after a publish/rollback/upgrade: drop cached specs and reload the bootstrap. */
  async configChanged(): Promise<void> {
    this.views.clear();
    this.entities.clear();
    await this.load();
  }

  view(id: string): Promise<ViewClientSpec> {
    let p = this.views.get(id);
    if (!p) {
      p = this.api.get<ViewClientSpec>(`/api/config/views/${id}`);
      p.catch(() => this.views.delete(id));
      this.views.set(id, p);
    }
    return p;
  }

  entity(id: string): Promise<EntityClientSpec> {
    let p = this.entities.get(id);
    if (!p) {
      p = this.api.get<EntityClientSpec>(`/api/config/entities/${id}`);
      p.catch(() => this.entities.delete(id));
      this.entities.set(id, p);
    }
    return p;
  }

  nav(entity: string) {
    return this.app()?.navigation.find((n) => n.entity === entity);
  }
}
