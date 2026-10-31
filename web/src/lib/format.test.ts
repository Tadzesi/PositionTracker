import { formatDuration, formatGap, localInputToUtc, parseSpan } from './format';

describe('parseSpan', () => {
  it('parses server TimeSpan text', () => {
    expect(parseSpan('00:01:23')).toBe(83);
    expect(parseSpan('01:00:00')).toBe(3600);
    expect(parseSpan('1.02:00:00')).toBe(93600);
    expect(parseSpan('-00:00:05')).toBe(-5);
    expect(parseSpan('00:00:01.5')).toBe(1.5);
  });
  it('returns null for nothing or garbage', () => {
    expect(parseSpan(null)).toBeNull();
    expect(parseSpan('abc')).toBeNull();
  });
});

describe('format', () => {
  it('formats durations', () => {
    expect(formatDuration(83)).toBe('1:23');
    expect(formatDuration(3723)).toBe('1:02:03');
    expect(formatDuration(null)).toBe('–');
  });
  it('formats gaps', () => {
    expect(formatGap('00:00:12')).toBe('+0:12');
    expect(formatGap(null)).toBe('–');
  });
  it('converts local input to UTC with Z', () => {
    expect(localInputToUtc('2026-11-08T10:00')).toMatch(/^2026-11-0[78]T\d\d:\d\d:00Z$/);
    expect(localInputToUtc('')).toBeNull();
  });
});
