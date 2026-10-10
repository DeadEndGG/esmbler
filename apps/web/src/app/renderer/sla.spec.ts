import { formatDuration } from './sla';

describe('formatDuration (SLA panel, ADR 0018)', () => {
  it('shows minutes, hours and days the way people read them', () => {
    expect(formatDuration(0)).toBe('0 min');
    expect(formatDuration(30)).toBe('1 min');
    expect(formatDuration(12 * 60)).toBe('12 min');
    expect(formatDuration(2 * 3600 + 5 * 60)).toBe('2 h 05 min');
    expect(formatDuration(3 * 86400 + 4 * 3600)).toBe('3 d 4 h');
    expect(formatDuration(2 * 86400)).toBe('2 d');
  });

  it('formats overruns as positive durations', () => {
    expect(formatDuration(-90 * 60)).toBe('1 h 30 min');
  });
});
