import { drillParams, formatMetric } from './drill';
import { MetricResult } from './models';

const base: MetricResult = {
  id: 'm',
  label: 'Open tickets',
  status: 'ok',
  measure: 'count',
  count: 12,
  capped: false,
  buckets: [],
  moreGroups: false,
  asOf: '2026-10-10T00:00:00.000Z',
  configRevision: 1,
};

describe('drill links', () => {
  it('carry the exact filters and SLA conditions the figure counted', () => {
    expect(drillParams({ entity: 'core.tickets.ticket', filters: ['record.status == "new"'], sla: ['core.tickets.sla.ticket:breached'], label: 'Breached' })).toEqual({
      filter: ['record.status == "new"'],
      sla: ['core.tickets.sla.ticket:breached'],
      label: 'Breached',
    });
  });

  it('leave out empty lists so the URL stays short', () => {
    expect(drillParams({ entity: 'e', filters: [], sla: [], label: 'All' })).toEqual({ filter: null, sla: null, label: 'All' });
  });
});

describe('formatMetric', () => {
  it('shows counts, with a plus when capped', () => {
    expect(formatMetric(base)).toBe('12');
    expect(formatMetric({ ...base, count: 10000, capped: true })).toBe((10000).toLocaleString() + '+');
  });

  it('marks a bar capped only when that bar stopped counting', () => {
    const capped = { ...base, count: 10000, capped: true };
    expect(formatMetric(capped, 3, 3, false)).toBe('3');
    expect(formatMetric(capped, 10000, 10000, true)).toBe((10000).toLocaleString() + '+');
  });

  it('shows durations with their unit and a dash when nothing was measured', () => {
    const avg = { ...base, measure: 'duration', unit: 'hours', value: 4.25 };
    expect(formatMetric(avg)).toBe((4.3).toLocaleString() + ' hours');
    expect(formatMetric({ ...avg, value: 1 })).toBe('1 hour');
    expect(formatMetric({ ...avg, value: null })).toBe('—');
  });

  it('shows percentages', () => {
    expect(formatMetric({ ...base, measure: 'avg', format: 'percent', value: 50 })).toBe('50%');
  });
});
