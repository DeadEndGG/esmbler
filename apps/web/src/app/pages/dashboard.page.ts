import { Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
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
  imports: [NodeComponent, Icon],
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
  private readonly session = inject(Session);
  protected readonly ctx = inject(RenderContext);
  protected readonly metrics = inject(MetricsContext);
  protected readonly error = signal<string | null>(null);
  private readonly loadedAt = signal<Date | null>(null);
  private seq = 0;

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
