import { Params } from '@angular/router';
import { DrillQuery, MetricResult } from './models';

/**
 * Explorer links for figures (REQ-WS-003, ADR 0019). A drill carries the exact filters and SLA conditions the server
 * counted, so the explorer lists the same records the figure covers; the server re-applies the viewer's permissions.
 */
export function drillCommands(d: DrillQuery): string[] {
  return ['/records', d.entity];
}

export function drillParams(d: DrillQuery): Params {
  return {
    filter: d.filters.length ? d.filters : null,
    sla: d.sla.length ? d.sla : null,
    label: d.label,
  };
}

/** A figure for display: counts get a "+" when capped; averages and durations get their unit. */
export function formatMetric(m: MetricResult, value: number | null | undefined = m.value, count = m.count): string {
  if (m.measure === 'count') return count.toLocaleString() + (m.capped ? '+' : '');
  if (value === null || value === undefined) return '—';
  const n = value.toLocaleString(undefined, { maximumFractionDigits: Math.abs(value) < 10 ? 1 : 0 });
  if (m.format === 'percent') return n + '%';
  if (m.measure === 'duration') return `${n} ${unitLabel(m.unit, value)}`;
  return n;
}

function unitLabel(unit: string | null | undefined, value: number): string {
  const one = Math.abs(value) === 1;
  switch (unit) {
    case 'minutes':
      return one ? 'minute' : 'minutes';
    case 'days':
      return one ? 'day' : 'days';
    default:
      return one ? 'hour' : 'hours';
  }
}

/** The browser's IANA time zone, so date buckets follow the viewer's days. */
export function viewerTimeZone(): string | undefined {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
  } catch {
    return undefined;
  }
}
