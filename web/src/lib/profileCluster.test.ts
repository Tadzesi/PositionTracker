import { describe, expect, it } from 'vitest';
import { assignRows, densityBins, groupRiders, lapDistance, windowAround } from './profileCluster';

const x = (d: number) => d / 10; // 10 m per pixel

describe('groupRiders', () => {
  it('keeps distant riders single and merges close ones into one group at their mean', () => {
    const groups = groupRiders([
      { id: 'a', distM: 100, pinned: false }, { id: 'b', distM: 140, pinned: false }, { id: 'c', distM: 1000, pinned: false },
    ], x, 20);
    expect(groups.map((g) => g.members.map((m) => m.id))).toEqual([['a', 'b'], ['c']]);
    expect(groups[0].distM).toBe(120);
  });

  it('chains riders: each one only needs to be near the previous', () => {
    const riders = [0, 150, 300, 450].map((d, i) => ({ id: String(i), distM: d, pinned: false }));
    expect(groupRiders(riders, x, 20)).toHaveLength(1);
  });

  it('never merges pinned riders (selected, leaders, alarms), even in the middle of a pack', () => {
    const groups = groupRiders([
      { id: 'a', distM: 100, pinned: false }, { id: 'sos', distM: 110, pinned: true }, { id: 'b', distM: 120, pinned: false },
    ], x, 20);
    expect(groups).toHaveLength(2);
    expect(groups.find((g) => g.key === 'sos')?.members).toHaveLength(1);
  });

  it('breaks a group apart when zoomed in (more pixels per metre)', () => {
    const riders = [{ id: 'a', distM: 100, pinned: false }, { id: 'b', distM: 140, pinned: false }];
    expect(groupRiders(riders, x, 20)).toHaveLength(1);
    expect(groupRiders(riders, (d) => d, 20)).toHaveLength(2);
  });
});

describe('densityBins', () => {
  it('counts riders per bin inside the window only', () => {
    expect(densityBins([5, 15, 16, 95, 500], 0, 100, 10)).toEqual([1, 2, 0, 0, 0, 0, 0, 0, 0, 1]);
  });
});

describe('assignRows', () => {
  it('puts overlapping labels into different rows and reuses a row once there is room', () => {
    const rows = assignRows([{ x: 10, w: 20 }, { x: 20, w: 20 }, { x: 100, w: 20 }], 3);
    expect(rows[0]).not.toBe(rows[1]);
    expect(rows[2]).toBe(0);
  });
});

describe('windowAround and lapDistance', () => {
  it('keeps the window inside the lap', () => {
    expect(windowAround(100, 3000, 10000)).toEqual([0, 3000]);
    expect(windowAround(9900, 3000, 10000)).toEqual([7000, 10000]);
    expect(windowAround(500, 20000, 10000)).toEqual([0, 10000]);
  });

  it('maps the total distance to the current lap; a finished rider is at the end', () => {
    expect(lapDistance(12500, 2, 10000, false)).toBe(2500);
    expect(lapDistance(30000, 3, 10000, true)).toBe(10000);
    expect(lapDistance(-5, 1, 10000, false)).toBe(0);
  });
});
