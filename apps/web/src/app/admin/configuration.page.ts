import { DatePipe } from '@angular/common';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { Api, ApiError } from '../core/api';
import { ConfigChange, Diagnostic } from '../core/models';
import { Session } from '../core/session';
import { Toasts } from '../core/toast';
import { BuilderState } from '../builder/builder-state';

interface Revision {
  revision: number;
  contentHash: string;
  status: string;
  source: string;
  createdBy?: string;
  approvedBy?: string;
  createdAt: string;
  modules: { id: string; version: string }[];
  overlayOperations: number;
  canRollback: boolean;
  rollbackBlockedReason?: string;
}

interface ModuleRow {
  module: string;
  installed: string;
  latest: string;
  upgradeAvailable: boolean;
}

interface UpgradeReport {
  module: string;
  fromVersion: string;
  toVersion: string;
  compatible: boolean;
  overlay: { index: number; op: string; summary: string; status: string; diagnostics: Diagnostic[] }[];
  errors: Diagnostic[];
  affectedCustomizations: string[];
  baseChanges: ConfigChange[];
  migrations: { id: string; kind: string; description: string; affected: number }[];
}

interface UpgradeView {
  id: string;
  module: string;
  fromVersion: string;
  toVersion: string;
  status: string;
  report: UpgradeReport;
  runs: { migrationId: string; status: string; processed: number; converted: number; invalid: number; lastError?: string; issues: { number?: string; value?: string; reason: string }[] }[];
  activatedRevision?: number;
}

/** Published revisions with compatibility-checked rollback, and module upgrades with a preview (REQ-MOD-003, REQ-MIG-*). */
@Component({
  selector: 'esm-configuration-page',
  imports: [DatePipe],
  template: `
    <div class="page">
      <header class="top"><h1>Configuration</h1><span class="muted">Live revision {{ session.app()?.configRevision }}</span></header>

      <section>
        <h2>Modules</h2>
        <div class="table-wrap">
          <table>
            <thead><tr><th scope="col">Module</th><th scope="col">Installed</th><th scope="col">Latest</th><th scope="col"></th></tr></thead>
            <tbody>
              @for (m of modules(); track m.module) {
                <tr>
                  <td class="mono">{{ m.module }}</td>
                  <td>{{ m.installed }}</td>
                  <td>{{ m.latest }}</td>
                  <td class="right">
                    @if (m.upgradeAvailable && session.canManageModules()) {
                      <button class="btn btn-sm" (click)="plan(m)">Preview upgrade</button>
                    } @else if (!m.upgradeAvailable) {
                      <span class="muted">Up to date</span>
                    }
                  </td>
                </tr>
              } @empty { <tr><td colspan="4" class="muted">No modules.</td></tr> }
            </tbody>
          </table>
        </div>

        @if (report(); as r) {
          <div class="panel" aria-live="polite">
            <h3>Upgrade {{ r.module }} {{ r.fromVersion }} → {{ r.toVersion }}</h3>
            @if (r.compatible) {
              <p class="ok">✓ Your customizations carry over. {{ r.affectedCustomizations.length }} customized element{{ r.affectedCustomizations.length === 1 ? '' : 's' }} checked.</p>
            } @else {
              <p class="err">This upgrade conflicts with your customizations. Resolve these before upgrading:</p>
              <ul>@for (e of r.errors; track $index) { <li>{{ e.message }}@if (e.suggestion) { <span class="muted"> {{ e.suggestion }}</span> }</li> }</ul>
            }
            @if (r.baseChanges.length) {
              <h4>What the new version changes</h4>
              <ul class="changes">@for (c of r.baseChanges; track c.kind + c.id) { <li><span class="badge" [class]="c.change === 'added' ? 'tone-green' : c.change === 'removed' ? 'tone-red' : 'tone-blue'">{{ c.change }}</span> {{ c.kind }} {{ c.label }}</li> }</ul>
            }
            @if (r.migrations.length) {
              <h4>Data migrations</h4>
              <ul>@for (m of r.migrations; track m.id) { <li>{{ m.description }} <span class="muted">({{ m.affected }} record{{ m.affected === 1 ? '' : 's' }})</span></li> }</ul>
            }
            <div class="actions">
              <button class="btn" (click)="report.set(null)">Close</button>
              <button class="btn btn-primary" [disabled]="!r.compatible || busy()" (click)="start(r)">Upgrade</button>
            </div>
          </div>
        }

        @if (upgrade(); as u) {
          <div class="panel" aria-live="polite">
            <h3>Upgrade {{ u.module }} to {{ u.toVersion }}: <span [class]="u.status === 'completed' ? 'ok' : u.status === 'failed' ? 'err' : ''">{{ u.status }}</span></h3>
            @for (run of u.runs; track run.migrationId) {
              <p>{{ run.migrationId }}: {{ run.status }} · {{ run.processed }} processed, {{ run.converted }} converted@if (run.invalid) { , <span class="warn">{{ run.invalid }} need review</span> }</p>
              @if (run.lastError) { <p class="err small">{{ run.lastError }}</p> }
              @if (run.issues.length) {
                <ul class="small">@for (i of run.issues.slice(0, 10); track $index) { <li>{{ i.number }}: "{{ i.value }}" {{ i.reason }}</li> }</ul>
              }
            }
            @if (u.status === 'failed' || u.status === 'paused') {
              <button class="btn btn-primary" (click)="resume(u)" [disabled]="busy()">Resume</button>
            }
            @if (u.activatedRevision) { <p class="ok">Activated as revision {{ u.activatedRevision }}.</p> }
          </div>
        }
      </section>

      <section>
        <h2>Revisions</h2>
        <div class="table-wrap">
          <table>
            <thead><tr><th scope="col">Revision</th><th scope="col">Source</th><th scope="col">Modules</th><th scope="col">Customizations</th><th scope="col">When</th><th scope="col"></th></tr></thead>
            <tbody>
              @for (r of revisions(); track r.revision) {
                <tr>
                  <td><strong>{{ r.revision }}</strong> @if (r.status === 'active') { <span class="badge tone-green">live</span> }</td>
                  <td>{{ r.source }}</td>
                  <td class="small">@for (m of r.modules; track m.id) { <span class="mono">{{ m.id }}&#64;{{ m.version }}</span> }</td>
                  <td>{{ r.overlayOperations }}</td>
                  <td class="small">{{ r.createdAt | date: 'medium' }}</td>
                  <td class="right">
                    @if (r.status !== 'active' && session.canPublish()) {
                      <button class="btn btn-sm" [disabled]="!r.canRollback || busy()" [title]="r.rollbackBlockedReason ?? ''" (click)="rollback(r)">Roll back</button>
                      @if (!r.canRollback && r.rollbackBlockedReason) { <div class="muted small">{{ r.rollbackBlockedReason }}</div> }
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    </div>
  `,
  styles: `
    .page { padding: var(--esm-space-5) var(--esm-space-6); max-width: 1100px; }
    .top { display: flex; align-items: baseline; gap: var(--esm-space-3); margin-bottom: var(--esm-space-4); }
    h1 { margin: 0; font: var(--esm-text-title-lg); }
    h2 { font: var(--esm-text-title-md); margin: var(--esm-space-6) 0 var(--esm-space-2); }
    h3 { font: var(--esm-text-title-sm); margin: 0 0 var(--esm-space-2); }
    h4 { font: var(--esm-text-body-sm); font-weight: 600; margin: var(--esm-space-3) 0 var(--esm-space-1); }
    .table-wrap { overflow-x: auto; border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); }
    table { width: 100%; border-collapse: collapse; }
    th { text-align: left; font: var(--esm-text-body-sm); color: var(--esm-text-muted); padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); background: var(--esm-surface-sunken); }
    td { padding: var(--esm-space-2) var(--esm-space-3); border-bottom: 1px solid var(--esm-border-subtle); font: var(--esm-text-body-sm); vertical-align: top; }
    .right { text-align: right; }
    .small { font: var(--esm-text-body-sm); }
    .mono { margin-right: var(--esm-space-2); }
    .panel { margin-top: var(--esm-space-3); padding: var(--esm-space-4); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-md); background: var(--esm-surface-raised); }
    .panel ul { margin: 0; padding-left: var(--esm-space-4); font: var(--esm-text-body-sm); }
    .panel p { margin: var(--esm-space-1) 0; }
    .actions { display: flex; justify-content: flex-end; gap: var(--esm-space-2); margin-top: var(--esm-space-3); }
    .ok { color: var(--esm-accent-green); } .err { color: var(--esm-accent-red); } .warn { color: var(--esm-accent-amber); }
  `,
})
export class ConfigurationPage {
  protected readonly session = inject(Session);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly builder = inject(BuilderState);
  protected readonly revisions = signal<Revision[]>([]);
  protected readonly modules = signal<ModuleRow[]>([]);
  protected readonly report = signal<UpgradeReport | null>(null);
  protected readonly upgrade = signal<UpgradeView | null>(null);
  protected readonly busy = signal(false);
  private poll: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    void this.load();
    inject(DestroyRef).onDestroy(() => this.poll && clearTimeout(this.poll));
  }

  private async load(): Promise<void> {
    try {
      const [revisions, modules] = await Promise.all([this.api.get<Revision[]>('/api/config/revisions'), this.api.get<ModuleRow[]>('/api/modules')]);
      this.revisions.set(revisions);
      this.modules.set(modules);
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? e.message : 'Could not load configuration.', 'warning');
    }
  }

  protected async plan(m: ModuleRow): Promise<void> {
    this.upgrade.set(null);
    await this.guard(async () => this.report.set(await this.api.post<UpgradeReport>(`/api/modules/${m.module}/upgrade-plan`, { toVersion: m.latest })));
  }

  protected async start(r: UpgradeReport): Promise<void> {
    await this.guard(async () => {
      const u = await this.api.post<UpgradeView>(`/api/modules/${r.module}/upgrades`, { toVersion: r.toVersion });
      this.report.set(null);
      this.upgrade.set(u);
      this.watch(u.id);
    });
  }

  protected async resume(u: UpgradeView): Promise<void> {
    await this.guard(async () => {
      this.upgrade.set(await this.api.post<UpgradeView>(`/api/upgrades/${u.id}/resume`));
      this.watch(u.id);
    });
  }

  protected async rollback(r: Revision): Promise<void> {
    const live = this.session.app()?.configRevision ?? 0;
    if (!window.confirm(`Roll back to revision ${r.revision}? This publishes it as a new revision; records keep their data.`)) return;
    await this.guard(async () => {
      const res = await this.api.post<{ revision: number }>('/api/config/rollback', { toRevision: r.revision, expectedRevision: live });
      await this.session.configChanged();
      this.builder.revision.update((x) => x + 1);
      this.toasts.show(`Rolled back. Revision ${res.revision} is live.`, 'success');
      await this.load();
    });
  }

  private watch(id: string): void {
    if (this.poll) clearTimeout(this.poll);
    const tick = async () => {
      try {
        const u = await this.api.get<UpgradeView>(`/api/upgrades/${id}`);
        this.upgrade.set(u);
        if (u.status === 'completed') {
          await this.session.configChanged();
          this.builder.revision.update((x) => x + 1);
          this.toasts.show(`${u.module} upgraded to ${u.toVersion}.`, 'success');
          await this.load();
          return;
        }
        if (u.status === 'failed' || u.status === 'paused') return;
      } catch {
        // transient: keep polling
      }
      this.poll = setTimeout(tick, 1000);
    };
    this.poll = setTimeout(tick, 500);
  }

  private async guard(action: () => Promise<void>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
    } catch (e) {
      this.toasts.show(e instanceof ApiError ? (e.problem.suggestion ? `${e.message} ${e.problem.suggestion}` : e.message) : 'The action failed.', 'warning');
    } finally {
      this.busy.set(false);
    }
  }
}
