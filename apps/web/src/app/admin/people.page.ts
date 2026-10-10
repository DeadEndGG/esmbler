import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from '../core/api';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';

interface Member {
  id: string;
  displayName: string;
  email?: string;
  roles: string[];
  groups: string[];
}

interface GroupRow {
  id: string;
  key: string;
  name: string;
  members: number;
}

interface Role {
  id: string;
  label: string;
  description?: string;
}

/** People, groups and role assignments (REQ-IAM-002). Changes write kernel tables then the relationship graph. */
@Component({
  selector: 'esm-people-page',
  imports: [FormsModule],
  template: `
    <div class="page">
      <header class="top"><h1>People and roles</h1></header>
      @if (!session.isAdmin()) {
        <div class="callout tone-amber" role="alert">Managing people requires the permission administration right.</div>
      } @else {
        <input class="input search" type="search" placeholder="Filter people" aria-label="Filter people" [ngModel]="query()" (ngModelChange)="query.set($event)" />
        <div class="table-wrap">
          <table>
            <thead><tr><th scope="col">Name</th><th scope="col">Roles</th><th scope="col">Groups</th></tr></thead>
            <tbody>
              @for (m of filtered(); track m.id) {
                <tr>
                  <td><strong>{{ m.displayName }}</strong><div class="muted small">{{ m.email }}</div></td>
                  <td>
                    <div class="chips">
                      @for (r of m.roles; track r) {
                        <span class="chip">{{ roleLabel(r) }} <button [attr.aria-label]="'Revoke ' + roleLabel(r) + ' from ' + m.displayName" (click)="revoke(m, r)">×</button></span>
                      }
                      <select class="add" [attr.aria-label]="'Assign a role to ' + m.displayName" [ngModel]="''" (ngModelChange)="assign(m, $event)">
                        <option value="">+ Role</option>
                        @for (r of roles(); track r.id) { @if (!m.roles.includes(r.id)) { <option [value]="r.id">{{ r.label }}</option> } }
                      </select>
                    </div>
                  </td>
                  <td>
                    <div class="chips">
                      @for (g of m.groups; track g) {
                        <span class="chip">{{ groupName(g) }} <button [attr.aria-label]="'Remove ' + m.displayName + ' from ' + groupName(g)" (click)="leave(m, g)">×</button></span>
                      }
                      <select class="add" [attr.aria-label]="'Add ' + m.displayName + ' to a group'" [ngModel]="''" (ngModelChange)="join(m, $event)">
                        <option value="">+ Group</option>
                        @for (g of groups(); track g.id) { @if (!inGroup(m, g)) { <option [value]="g.id">{{ g.name }}</option> } }
                      </select>
                    </div>
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="3" class="muted">{{ loading() ? 'Loading…' : 'No people match.' }}</td></tr>
              }
            </tbody>
          </table>
        </div>
        <h2>Groups</h2>
        <ul class="groups">
          @for (g of groups(); track g.id) { <li><strong>{{ g.name }}</strong> <span class="muted">{{ g.members }} member{{ g.members === 1 ? '' : 's' }} · {{ g.key }}</span></li> }
        </ul>
      }
    </div>
  `,
  styles: `
    .page { padding: var(--esm-space-5) var(--esm-space-6); max-width: 1100px; }
    h1 { margin: 0 0 var(--esm-space-4); font: var(--esm-text-title-lg); }
    h2 { font: var(--esm-text-title-md); margin: var(--esm-space-6) 0 var(--esm-space-2); }
    .search { max-width: 320px; margin-bottom: var(--esm-space-3); }
    .table-wrap { overflow-x: auto; border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); }
    table { width: 100%; border-collapse: collapse; }
    th { text-align: left; font: var(--esm-text-body-sm); color: var(--esm-text-muted); padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); background: var(--esm-surface-sunken); }
    td { padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); vertical-align: top; font: var(--esm-text-body-sm); }
    .small { font: var(--esm-text-body-sm); }
    .chips { display: flex; flex-wrap: wrap; gap: var(--esm-space-1); align-items: center; }
    .chip { display: inline-flex; align-items: center; gap: var(--esm-space-1); padding: 0 var(--esm-space-2); border-radius: var(--esm-radius-full); background: var(--esm-state-selected); }
    .chip button { border: 0; background: transparent; cursor: pointer; padding: 0; color: var(--esm-text-muted); }
    .add { border: 1px dashed var(--esm-border-subtle); border-radius: var(--esm-radius-full); background: transparent; padding: 0 var(--esm-space-2); font: var(--esm-text-body-sm); color: var(--esm-text-muted); cursor: pointer; }
    .groups { list-style: none; padding: 0; margin: 0; }
    .groups li { padding: var(--esm-space-2) 0; border-bottom: 1px solid var(--esm-border-subtle); }
    .callout { padding: var(--esm-space-3); border-radius: var(--esm-radius-md); background: color-mix(in srgb, var(--tone) 12%, transparent); }
  `,
})
export class PeoplePage {
  protected readonly session = inject(Session);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly members = signal<Member[]>([]);
  protected readonly groups = signal<GroupRow[]>([]);
  protected readonly roles = signal<Role[]>([]);
  protected readonly loading = signal(true);
  protected readonly query = signal('');
  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase();
    return this.members().filter((m) => !q || m.displayName.toLowerCase().includes(q) || (m.email ?? '').toLowerCase().includes(q));
  });

  constructor() {
    void this.load();
  }

  private async load(): Promise<void> {
    try {
      const [members, groups, roles] = await Promise.all([
        this.api.get<Member[]>('/api/admin/members'),
        this.api.get<GroupRow[]>('/api/admin/groups'),
        this.api.get<Role[]>('/api/admin/roles'),
      ]);
      this.members.set(members);
      this.groups.set(groups);
      this.roles.set(roles);
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not load people.', 'warning');
    } finally {
      this.loading.set(false);
    }
  }

  protected roleLabel(id: string): string {
    return this.roles().find((r) => r.id === id)?.label ?? id;
  }

  protected groupName(keyOrId: string): string {
    return this.groups().find((g) => g.key === keyOrId || g.id === keyOrId)?.name ?? keyOrId;
  }

  protected inGroup(m: Member, g: GroupRow): boolean {
    return m.groups.includes(g.key) || m.groups.includes(g.id) || m.groups.includes(g.name);
  }

  protected async assign(m: Member, role: string): Promise<void> {
    if (!role) return;
    await this.run(() => this.api.post('/api/admin/roles/assign', { role, principalId: m.id }), `${this.roleLabel(role)} assigned to ${m.displayName}.`);
  }

  protected async revoke(m: Member, role: string): Promise<void> {
    if (!window.confirm(`Revoke ${this.roleLabel(role)} from ${m.displayName}? It takes effect immediately.`)) return;
    await this.run(() => this.api.post('/api/admin/roles/revoke', { role, principalId: m.id }), `${this.roleLabel(role)} revoked from ${m.displayName}.`);
  }

  protected async join(m: Member, groupId: string): Promise<void> {
    if (!groupId) return;
    await this.run(() => this.api.post(`/api/admin/groups/${groupId}/members`, { principalId: m.id }), `${m.displayName} added to ${this.groupName(groupId)}.`);
  }

  protected async leave(m: Member, group: string): Promise<void> {
    const g = this.groups().find((x) => x.key === group || x.id === group || x.name === group);
    if (!g) return;
    await this.run(() => this.api.delete(`/api/admin/groups/${g.id}/members/${m.id}`), `${m.displayName} removed from ${g.name}.`);
  }

  private async run(action: () => Promise<unknown>, success: string): Promise<void> {
    try {
      await action();
      this.toasts.show(success, 'success');
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'The change failed.', 'warning');
    }
    await this.load();
  }
}
