import { Injectable, inject, signal } from '@angular/core';
import { Api } from './api';

export interface Person {
  id: string;
  displayName: string;
  email?: string;
}

export interface Group {
  id: string;
  key: string;
  name: string;
}

/** People and groups of the current workspace, for user/group pickers. Loaded once per session. */
@Injectable({ providedIn: 'root' })
export class Directory {
  private readonly api = inject(Api);
  readonly people = signal<Person[]>([]);
  readonly groups = signal<Group[]>([]);
  private loaded: Promise<void> | null = null;

  load(): Promise<void> {
    this.loaded ??= Promise.all([
      this.api.get<Person[]>('/api/admin/members').then((p) => this.people.set(p)).catch(() => undefined),
      this.api.get<Group[]>('/api/admin/groups').then((g) => this.groups.set(g)).catch(() => undefined),
    ]).then(() => undefined);
    return this.loaded;
  }

  reset(): void {
    this.loaded = null;
  }
}
