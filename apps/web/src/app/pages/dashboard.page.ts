import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, ApiError } from '../core/api';
import { viewerTimeZone } from '../core/drill';
import { DashboardData, MetricResult } from '../core/models';
import { Session } from '../core/session';
import { NodeComponent } from '../renderer/node';
import { MetricsContext } from '../renderer/metrics';
import { RenderContext } from '../renderer/render-context';
import { Icon } from '../ui/icon';

const REFRESH_MS = 60_000;

/**
 * A dashboard view (REQ-WS-002, REQ-WS-004, ADR 0019): the layout comes from the view spec, the figures from the
 * server, evaluated for the signed-in person. Every figure links into the explorer on the records it counts.
 */
@Component({
  selector: 'esm-dashboard-page',
  imports: [NodeComponent, Icon, RouterLink],
  providers: [RenderContext, MetricsContext],
  template: `
    @let s = ctx.spec();
    <div class="page mx-auto max-w-[1440px] px-8 pt-7 pb-12 max-[700px]:px-4">
      <header class="mb-5 flex flex-wrap items-end gap-3">
        <div class="min-w-0 flex-1">
          <h1 class="text-title-lg">{{ s?.view?.layout?.props?.['title'] ?? s?.view?.label ?? 'Overview' }}</h1>
          <p class="mt-0.5 text-body-sm text-muted" aria-live="polite">
            @if (asOf(); as at) { Updated {{ at }} · figures count only records you can see } @else { Loading… }
          </p>
        </div>
        <button class="btn btn-sm" (click)="refresh()" [disabled]="metrics.loading()"><esm-icon name="reset" [size]="14" /> Refresh</button>
      </header>
      @if (error(); as err) {
        <div class="callout tone-red mb-4" role="alert">{{ err }}</div>
      }
      @if (empty()) {
        <section class="welcome mb-5 rounded-lg border border-subtle bg-raised p-5 shadow-xs" aria-labelledby="welcome-t">
          <h2 id="welcome-t" class="text-title-sm">No {{ plural() }} yet</h2>
          <p class="mt-1 text-body-sm text-muted">
            The figures below are a <strong>preview with sample data</strong>, so you can see what this dashboard will show. They are not your records and disappear when the first {{ singular() }} arrives.
          </p>
          <div class="mt-3 flex flex-wrap gap-2">
            @if (nav()?.canCreate) {
              @if (nav()?.requests) {
                <a class="btn btn-primary btn-sm" routerLink="/catalog"><esm-icon name="store" [size]="14" /> Make the first request</a>
              } @else {
                <a class="btn btn-primary btn-sm" [routerLink]="['/records', nav()!.entity, 'new']"><esm-icon name="plus" [size]="14" /> Create the first {{ singular() }}</a>
              }
            }
            @if (session.isAdmin()) {
              <a class="btn btn-sm" routerLink="/admin/people"><esm-icon name="users" [size]="14" /> Invite your team</a>
            }
            @if (session.canCustomize()) {
              <a class="btn btn-sm" routerLink="/admin/configuration"><esm-icon name="sliders" [size]="14" /> Review the configuration</a>
            }
          </div>
        </section>
      }
      @if (s) {
        <!-- The page header carries the title, so the layout.page node's own heading is not repeated. -->
        @for (c of s.view.layout.children; track c.id) { <esm-node [node]="c" [viewId]="s.view.id" /> }
      } @else if (!error()) {
        <div class="grid grid-cols-4 gap-4 max-[900px]:grid-cols-2" aria-busy="true">
          @for (i of [1, 2, 3, 4]; track i) { <span class="skeleton h-24"></span> }
        </div>
      }
    </div>
  `,
})
export class DashboardPage {
  readonly view = input.required<string>();
  private readonly api = inject(Api);
  protected readonly session = inject(Session);
  protected readonly ctx = inject(RenderContext);
  protected readonly metrics = inject(MetricsContext);
  protected readonly error = signal<string | null>(null);
  private readonly loadedAt = signal<Date | null>(null);
  private seq = 0;

  /** The viewer sees no records yet: figures are labeled previews (REQ-WS-006). */
  protected readonly empty = signal(false);
  protected readonly nav = computed(() => (this.ctx.spec() ? this.session.nav(this.ctx.spec()!.view.entity) : undefined));
  protected readonly plural = computed(() => this.nav()?.pluralLabel.toLowerCase() ?? 'records');
  protected readonly singular = computed(() => this.nav()?.label.toLowerCase() ?? 'record');

  protected readonly asOf = computed(() => this.loadedAt()?.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' }) ?? null);

  constructor() {
    effect(() => {
      const view = this.view();
      untracked(() => void this.load(view));
    });
    const timer = setInterval(() => {
      if (document.visibilityState === 'visible') void this.loadData(this.view());
    }, REFRESH_MS);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  protected refresh(): void {
    void this.loadData(this.view());
  }

  private async load(view: string): Promise<void> {
    this.ctx.spec.set(null);
    this.metrics.results.set(new Map());
    this.error.set(null);
    try {
      this.ctx.spec.set(await this.session.view(view));
    } catch (e) {
      this.error.set(e instanceof ApiError && e.status === 404 ? 'This dashboard does not exist or you cannot see it.' : 'Could not load this dashboard.');
      this.metrics.loading.set(false);
      return;
    }
    await this.loadData(view);
  }

  private async loadData(view: string): Promise<void> {
    const seq = ++this.seq;
    this.metrics.loading.set(true);
    try {
      const data = await this.api.get<DashboardData>(`/api/dashboards/${view}/data`, { tz: viewerTimeZone() });
      if (seq !== this.seq) return;
      this.metrics.results.set(new Map<string, MetricResult>(data.metrics.map((m) => [m.id, m])));
      this.empty.set(!!data.empty);
      this.loadedAt.set(new Date());
      this.error.set(null);
      // A publish since the spec was cached: reload the bootstrap and the layout so tiles match the figures.
      if (this.ctx.spec() && data.configRevision !== this.ctx.spec()!.configRevision) {
        await this.session.configChanged();
        if (seq === this.seq) this.ctx.spec.set(await this.session.view(view));
      }
    } catch (e) {
      if (seq === this.seq) this.error.set(e instanceof ApiError ? e.message : 'Could not load the figures.');
    } finally {
      if (seq === this.seq) this.metrics.loading.set(false);
    }
  }
}
