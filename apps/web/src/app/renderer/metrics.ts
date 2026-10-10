import { Component, Injectable, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { drillCommands, drillParams, formatMetric } from '../core/drill';
import { MetricBucket, MetricResult, NodeSpec } from '../core/models';
import { Icon } from '../ui/icon';

/**
 * Evaluated figures for the page (ADR 0019), provided by the dashboard page. Tiles rendered anywhere else (a draft
 * preview, a record page) find no context and show a placeholder rather than computing figures themselves.
 */
@Injectable()
export class MetricsContext {
  readonly results = signal<ReadonlyMap<string, MetricResult>>(new Map());
  readonly loading = signal(true);

  result(id: string | undefined): MetricResult | undefined {
    return id ? this.results().get(id) : undefined;
  }
}

const TONES: Record<string, string> = { info: 'blue', success: 'green', warning: 'amber', critical: 'red' };

/** display.kpi_tile: one ungrouped figure that opens the explorer on exactly the records it counts. */
@Component({
  selector: 'esm-kpi-tile',
  imports: [RouterLink, Icon],
  template: `
    @let m = metric();
    @let label = node().props['label'] ?? m?.label ?? 'Metric';
    @if (m?.status === 'ok' && m?.drill && node().props['drill'] !== false) {
      <a class="tile" [class]="'tone-' + tone()" [routerLink]="link(m!).commands" [queryParams]="link(m!).params" [attr.aria-label]="label + ': ' + value() + '. Open the list'">
        <span class="label">{{ label }}</span>
        <span class="value">{{ value() }}</span>
        <span class="hint"><esm-icon name="arrow-up-right" [size]="13" /> View records</span>
      </a>
    } @else {
      <div class="tile" [class]="'tone-' + tone()" [attr.aria-busy]="!m && ctx?.loading()">
        <span class="label">{{ label }}</span>
        @if (!m) {
          @if (ctx?.loading()) { <span class="skeleton value-skeleton"></span> } @else { <span class="value muted">—</span> }
        } @else if (m.status === 'timeout') {
          <span class="value muted">—</span><span class="hint">Took too long to count. Refresh to try again.</span>
        } @else if (m.status === 'restricted') {
          <span class="value muted">—</span><span class="hint">Not available to you.</span>
        } @else {
          <span class="value">{{ value() }}</span>
        }
      </div>
    }
  `,
  styles: `
    :host { display: block; height: 100%; }
    .tile { display: flex; flex-direction: column; gap: var(--esm-space-1); height: 100%; padding: var(--esm-space-4) var(--esm-space-5); border-radius: var(--esm-radius-lg);
      background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); box-shadow: var(--esm-shadow-card); color: var(--esm-text-default); text-decoration: none;
      border-top: 3px solid var(--tone, var(--esm-border-subtle)); transition: border-color var(--esm-motion-fast) var(--esm-ease-out), box-shadow var(--esm-motion-fast) var(--esm-ease-out); }
    .tile.tone-gray { --tone: var(--esm-border-subtle); }
    a.tile:hover { border-color: var(--esm-border-strong); border-top-color: var(--tone, var(--esm-border-strong)); text-decoration: none; }
    a.tile:hover .hint { color: var(--esm-accent-brand); }
    .label { font: var(--esm-text-body-sm); font-weight: 500; color: var(--esm-text-muted); }
    .value { font: var(--esm-text-title-xl); letter-spacing: -0.02em; font-variant-numeric: tabular-nums; }
    .value-skeleton { display: block; height: 28px; width: 40%; margin: 4px 0; }
    .hint { display: inline-flex; align-items: center; gap: 4px; font: var(--esm-text-caption); color: var(--esm-text-muted); }
  `,
})
export class KpiTile {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(MetricsContext, { optional: true });
  protected readonly metric = computed(() => this.ctx?.result(this.node().props['metric']));
  protected readonly value = computed(() => (this.metric() ? formatMetric(this.metric()!) : '—'));
  protected readonly tone = computed(() => TONES[this.node().props['tone'] ?? ''] ?? 'gray');

  protected link(m: MetricResult) {
    return { commands: drillCommands(m.drill!), params: drillParams(m.drill!) };
  }
}

/** A grouped metric's numbers as a table; every row opens the explorer on its records. */
@Component({
  selector: 'esm-metric-buckets',
  imports: [RouterLink],
  template: `
    @let m = metric();
    <table class="w-full border-collapse">
      <caption class="sr-only">{{ m.label }}</caption>
      <thead><tr><th scope="col">Group</th>@if (m.measure !== 'count') { <th scope="col" class="num">Value</th> }<th scope="col" class="num">Records</th></tr></thead>
      <tbody>
        @for (b of m.buckets; track b.key) {
          <tr>
            <th scope="row"><a [routerLink]="['/records', b.drill.entity]" [queryParams]="params(b)">{{ b.label }}</a></th>
            @if (m.measure !== 'count') { <td class="num">{{ value(b) }}</td> }
            <td class="num">{{ b.count.toLocaleString() }}</td>
          </tr>
        }
      </tbody>
    </table>
  `,
  styles: `
    :host { display: block; margin-top: var(--esm-space-3); }
    th, td { padding: 6px var(--esm-space-2); border-bottom: 1px solid var(--esm-border-subtle); text-align: left; font: var(--esm-text-body-sm); }
    thead th { font: var(--esm-text-caption); text-transform: uppercase; letter-spacing: 0.04em; color: var(--esm-text-muted); }
    tbody th { font-weight: 500; }
    tbody tr:last-child > * { border-bottom: 0; }
    .num { text-align: right; font-variant-numeric: tabular-nums; }
  `,
})
export class MetricBucketTable {
  readonly metric = input.required<MetricResult>();

  protected params(b: MetricBucket) {
    return drillParams(b.drill);
  }

  protected value(b: MetricBucket): string {
    return formatMetric(this.metric(), b.value, b.count);
  }
}

/**
 * display.bar_chart: a grouped metric as bars. Bars are links into the explorer, so each one is reachable by keyboard
 * and announced with its label and value; a table with the same numbers can be shown.
 */
@Component({
  selector: 'esm-bar-chart',
  imports: [RouterLink, MetricBucketTable],
  template: `
    @let m = metric();
    <section class="chart-card" [attr.aria-labelledby]="node().id + '-t'">
      <header class="flex items-center gap-2">
        <h3 [id]="node().id + '-t'" class="flex-1">{{ node().props['title'] ?? m?.label ?? 'Chart' }}</h3>
        @if (m?.status === 'ok' && m!.buckets.length) {
          <button class="btn btn-ghost btn-sm" [attr.aria-pressed]="table()" (click)="showTable.set(!table())">{{ table() ? 'Hide table' : 'Show table' }}</button>
        }
      </header>
      @if (!m) {
        <div class="state">@if (ctx?.loading()) { <span class="skeleton"></span><span class="skeleton"></span><span class="skeleton short"></span> } @else { <p class="muted">No data.</p> }</div>
      } @else if (m.status !== 'ok') {
        <p class="muted state">{{ m.status === 'timeout' ? 'Took too long to count. Refresh to try again.' : 'Not available to you.' }}</p>
      } @else if (!m.buckets.length || max() === 0) {
        <p class="muted state">Nothing to show yet.</p>
      } @else if (vertical()) {
        <ul class="columns" role="list">
          @for (b of m.buckets; track b.key) {
            <li>
              <a class="col-bar" [routerLink]="commands(b)" [queryParams]="params(b)" [attr.aria-label]="b.label + ': ' + display(m, b)">
                <span class="col-value">{{ display(m, b) }}</span>
                <span class="col-fill" [style.height.%]="pct(b)"></span>
              </a>
              <span class="col-label" aria-hidden="true">{{ b.label.replace('Week of ', '') }}</span>
            </li>
          }
        </ul>
      } @else {
        <ul class="bars" role="list">
          @for (b of m.buckets; track b.key) {
            <li>
              <a class="bar" [routerLink]="commands(b)" [queryParams]="params(b)" [attr.aria-label]="b.label + ': ' + display(m, b)">
                <span class="bar-label">{{ b.label }}</span>
                <span class="bar-track"><span class="bar-fill" [style.width.%]="pct(b)"></span></span>
                <span class="bar-value">{{ display(m, b) }}</span>
              </a>
            </li>
          }
        </ul>
        @if (m.moreGroups) { <p class="muted more">Showing the {{ m.buckets.length }} largest groups.</p> }
      }
      @if (m?.status === 'ok' && table()) { <esm-metric-buckets [metric]="m!" /> }
    </section>
  `,
  styles: `
    :host { display: block; height: 100%; }
    .chart-card { height: 100%; background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); box-shadow: var(--esm-shadow-card); padding: var(--esm-space-4) var(--esm-space-5) var(--esm-space-5); }
    header { margin-bottom: var(--esm-space-3); }
    h3 { margin: 0; font: var(--esm-text-title-sm); }
    .state { display: grid; gap: var(--esm-space-2); margin: 0; padding: var(--esm-space-4) 0; font: var(--esm-text-body-sm); }
    .state .skeleton { display: block; height: 12px; }
    .state .skeleton.short { width: 60%; }
    ul { list-style: none; margin: 0; padding: 0; }
    .bars { display: grid; gap: 2px; }
    .bar { display: grid; grid-template-columns: minmax(80px, 34%) 1fr auto; align-items: center; gap: var(--esm-space-3); padding: 6px var(--esm-space-2); border-radius: var(--esm-radius-sm);
      color: var(--esm-text-default); text-decoration: none; font: var(--esm-text-body-sm); }
    .bar:hover { background: var(--esm-state-hover); text-decoration: none; }
    .bar-label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .bar-track { height: 10px; border-radius: var(--esm-radius-full); background: var(--esm-surface-sunken); overflow: hidden; }
    .bar-fill { display: block; height: 100%; min-width: 2px; border-radius: inherit; background: var(--esm-accent-brand); }
    .bar-value { font-variant-numeric: tabular-nums; font-weight: 600; min-width: 2ch; text-align: right; }
    .columns { display: flex; align-items: stretch; gap: var(--esm-space-2); height: 180px; }
    .columns li { flex: 1; display: flex; flex-direction: column; min-width: 0; }
    .col-bar { flex: 1; display: flex; flex-direction: column; justify-content: flex-end; align-items: stretch; gap: 4px; padding: 0 2px; border-radius: var(--esm-radius-sm); text-decoration: none; color: var(--esm-text-default); }
    .col-bar:hover { background: var(--esm-state-hover); text-decoration: none; }
    .col-value { text-align: center; font: var(--esm-text-caption); font-weight: 600; font-variant-numeric: tabular-nums; }
    .col-fill { display: block; min-height: 2px; border-radius: 4px 4px 0 0; background: var(--esm-accent-brand); }
    .col-label { margin-top: 6px; text-align: center; font: var(--esm-text-caption); color: var(--esm-text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .more { margin: var(--esm-space-2) 0 0; font: var(--esm-text-caption); }
  `,
})
export class BarChart {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(MetricsContext, { optional: true });
  protected readonly metric = computed(() => this.ctx?.result(this.node().props['metric']));
  protected readonly vertical = computed(() => this.node().props['orientation'] === 'vertical');
  protected readonly showTable = signal<boolean | null>(null);
  protected readonly table = computed(() => this.showTable() ?? !!this.node().props['showTable']);
  protected readonly max = computed(() => Math.max(0, ...(this.metric()?.buckets ?? []).map((b) => figure(this.metric()!, b))));

  protected pct(b: MetricBucket): number {
    const max = this.max();
    return max > 0 ? Math.max(0, (figure(this.metric()!, b) / max) * 100) : 0;
  }

  protected display(m: MetricResult, b: MetricBucket): string {
    return formatMetric(m, b.value, b.count);
  }

  protected commands(b: MetricBucket) {
    return drillCommands(b.drill);
  }

  protected params(b: MetricBucket) {
    return drillParams(b.drill);
  }
}

/** The number a bar shows: the count, or the measured value for sums, averages and durations. */
function figure(m: MetricResult, b: MetricBucket): number {
  return m.measure === 'count' ? b.count : (b.value ?? 0);
}

/** display.metric_table: a grouped metric as a table card. */
@Component({
  selector: 'esm-metric-table',
  imports: [MetricBucketTable],
  template: `
    @let m = metric();
    <section class="chart-card" [attr.aria-labelledby]="node().id + '-t'">
      <h3 [id]="node().id + '-t'">{{ node().props['title'] ?? m?.label ?? 'Table' }}</h3>
      @if (!m) {
        <p class="muted state">{{ ctx?.loading() ? 'Loading…' : 'No data.' }}</p>
      } @else if (m.status !== 'ok') {
        <p class="muted state">{{ m.status === 'timeout' ? 'Took too long to count. Refresh to try again.' : 'Not available to you.' }}</p>
      } @else if (!m.buckets.length) {
        <p class="muted state">Nothing to show yet.</p>
      } @else {
        <esm-metric-buckets [metric]="m" />
        @if (m.moreGroups) { <p class="muted state">Showing the {{ m.buckets.length }} largest groups.</p> }
      }
    </section>
  `,
  styles: `
    :host { display: block; height: 100%; }
    .chart-card { height: 100%; background: var(--esm-surface-raised); border: 1px solid var(--esm-border-subtle); border-radius: var(--esm-radius-lg); box-shadow: var(--esm-shadow-card); padding: var(--esm-space-4) var(--esm-space-5) var(--esm-space-5); }
    h3 { margin: 0; font: var(--esm-text-title-sm); }
    .state { margin: var(--esm-space-3) 0 0; font: var(--esm-text-body-sm); }
  `,
})
export class MetricTable {
  readonly node = input.required<NodeSpec>();
  protected readonly ctx = inject(MetricsContext, { optional: true });
  protected readonly metric = computed(() => this.ctx?.result(this.node().props['metric']));
}
