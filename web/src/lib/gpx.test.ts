import { describe, expect, it } from 'vitest';
import { nearestDistance, parseGpx, positionAt, type Pt } from './gpx';

const gpx = (pts: [number, number, number | null][]) =>
  `<?xml version="1.0"?><gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1"><trk><trkseg>${
    pts.map(([la, lo, e]) => `<trkpt lat="${la}" lon="${lo}">${e === null ? '' : `<ele>${e}</ele>`}</trkpt>`).join('')}</trkseg></trk></gpx>`;

describe('parseGpx', () => {
  it('reads the points, the length and the elevation range', () => {
    const p = parseGpx(gpx([[49, 18, 300], [49.01, 18, 320], [49.02, 18, 310]]))!;
    expect(p.count).toBe(3);
    expect(p.hasElevation).toBe(true);
    expect([p.minEle, p.maxEle]).toEqual([300, 320]);
    expect(p.lengthM).toBeGreaterThan(2200);
    expect(p.lengthM).toBeLessThan(2250);
    expect(p.points[0][3]).toBe(0);
  });

  it('says there is no elevation when a point lacks it', () => {
    const p = parseGpx(gpx([[49, 18, 300], [49.01, 18, null]]))!;
    expect(p.hasElevation).toBe(false);
    expect(p.minEle).toBeNull();
  });

  it('refuses what is not a GPX with two points', () => {
    expect(parseGpx('not xml <')).toBeNull();
    expect(parseGpx(gpx([[49, 18, 1]]))).toBeNull();
    expect(parseGpx(gpx([[95, 18, 1], [49, 18, 1]]))).toBeNull();
  });

  it('thins a long track to about 1200 points and keeps the last one', () => {
    const many = Array.from({ length: 2500 }, (_, i) => [49 + i * 0.0001, 18, 300] as [number, number, number]);
    const p = parseGpx(gpx(many))!;
    expect(p.count).toBe(2500);
    expect(p.points.length).toBeLessThanOrEqual(1300);
    expect(p.points[p.points.length - 1][3]).toBeCloseTo(p.lengthM, 0);
  }, 30000);
});

describe('line helpers', () => {
  const line: Pt[] = [[49, 18, 100, 0], [49.01, 18, 200, 1000], [49.02, 18, 100, 2000]];

  it('finds the position at a distance and clamps to the ends', () => {
    const mid = positionAt(line, 500)!;
    expect(mid[0]).toBeCloseTo(49.005, 6);
    expect(mid[1]).toBe(18);
    expect(positionAt(line, -5)).toEqual([49, 18]);
    expect(positionAt(line, 9999)).toEqual([49.02, 18]);
  });

  it('maps a clicked position to the distance of the nearest point', () => {
    expect(nearestDistance(line, 49.0099, 18.0001)).toBe(1000);
    expect(nearestDistance(line, 48.9, 18)).toBe(0);
  });
});
