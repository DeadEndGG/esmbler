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
  readonly canPublish = computed(() => this.app()?.actions.includes('config.publish') ?? false);
  readonly canManageModules = computed(() => this.app()?.actions.includes('modules.manage') ?? false);
  readonly isAdmin = computed(() => this.app()?.actions.includes('permissions.admin') ?? false);

  async load(): Promise<AppBootstrap> {
    const app = await this.api.get<AppBootstrap>('/api/app');
    if (this.app()?.configRevision !== app.configRevision) {
      this.views.clear();
      this.entities.clear();
    }
    this.app.set(app);
    return app;
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
