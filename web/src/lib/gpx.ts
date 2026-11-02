/** A point of a course line: latitude, longitude, elevation (m, null without), distance from the start (m). */
export type Pt = [number, number, number | null, number];

export interface ParsedGpx {
  points: Pt[];
  /** Points in the file (the returned line is thinned). */
  count: number;
  lengthM: number;
  hasElevation: boolean;
  minEle: number | null;
  maxEle: number | null;
  /** The end lies at most 100 m from the start (as the server decides): only such a course can have more than one lap. */
  closed: boolean;
}

const MAX_POINTS = 1200;
/** Course.ClosedToleranceM on the server. */
const CLOSED_TOLERANCE_M = 100;
const rad = (d: number) => (d * Math.PI) / 180;

export function haversineM(aLat: number, aLon: number, bLat: number, bLon: number): number {
  const dLat = rad(bLat - aLat);
  const dLon = rad(bLon - aLon);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(aLat)) * Math.cos(rad(bLat)) * Math.sin(dLon / 2) ** 2;
  return 2 * 6371000 * Math.asin(Math.sqrt(h));
}

/** Reads the track (or route) points of a GPX document in the browser, to preview a file before it is uploaded; null when it is not a usable GPX. */
export function parseGpx(text: string): ParsedGpx | null {
  const doc = new DOMParser().parseFromString(text, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0) return null;
  let nodes = Array.from(doc.getElementsByTagNameNS('*', 'trkpt'));
  if (nodes.length === 0) nodes = Array.from(doc.getElementsByTagNameNS('*', 'rtept'));
  const raw: { lat: number; lon: number; ele: number | null }[] = [];
  for (const n of nodes) {
    const lat = Number(n.getAttribute('lat'));
    const lon = Number(n.getAttribute('lon'));
    if (!Number.isFinite(lat) || !Number.isFinite(lon) || Math.abs(lat) > 90 || Math.abs(lon) > 180) return null;
    const e = n.getElementsByTagNameNS('*', 'ele')[0]?.textContent;
    const ele = e != null && e.trim() !== '' && Number.isFinite(Number(e)) ? Number(e) : null;
    raw.push({ lat, lon, ele });
  }
  if (raw.length < 2) return null;
  const cum: number[] = [0];
  for (let i = 1; i < raw.length; i++) cum.push(cum[i - 1] + haversineM(raw[i - 1].lat, raw[i - 1].lon, raw[i].lat, raw[i].lon));
  const step = Math.max(1, Math.ceil(raw.length / MAX_POINTS));
  const points: Pt[] = [];
  for (let i = 0; i < raw.length; i += step) points.push([raw[i].lat, raw[i].lon, raw[i].ele, cum[i]]);
  if ((raw.length - 1) % step !== 0) points.push([raw[raw.length - 1].lat, raw[raw.length - 1].lon, raw[raw.length - 1].ele, cum[raw.length - 1]]);
  const hasElevation = raw.every((p) => p.ele !== null);
  const eles = hasElevation ? raw.map((p) => p.ele as number) : [];
  return {
    points, count: raw.length, lengthM: cum[cum.length - 1], hasElevation,
    minEle: eles.length ? Math.min(...eles) : null, maxEle: eles.length ? Math.max(...eles) : null,
    closed: haversineM(raw[0].lat, raw[0].lon, raw[raw.length - 1].lat, raw[raw.length - 1].lon) <= CLOSED_TOLERANCE_M,
  };
}

/** The position on the line at a distance from its start (clamped to the line). */
export function positionAt(points: Pt[], distM: number): [number, number] | null {
  if (points.length < 2) return null;
  if (distM <= points[0][3]) return [points[0][0], points[0][1]];
  for (let i = 1; i < points.length; i++) {
    if (points[i][3] >= distM) {
      const span = points[i][3] - points[i - 1][3] || 1;
      const f = (distM - points[i - 1][3]) / span;
      return [points[i - 1][0] + (points[i][0] - points[i - 1][0]) * f, points[i - 1][1] + (points[i][1] - points[i - 1][1]) * f];
    }
  }
  const last = points[points.length - 1];
  return [last[0], last[1]];
}

/** The distance from the start of the line point nearest to a clicked position. */
export function nearestDistance(points: Pt[], lat: number, lon: number): number {
  const k = Math.cos(rad(lat));
  let best = Infinity;
  let at = 0;
  for (const p of points) {
    const dy = p[0] - lat;
    const dx = (p[1] - lon) * k;
    const d = dx * dx + dy * dy;
    if (d < best) { best = d; at = p[3]; }
  }
  return at;
}

/** Elevation at a distance (linear between the points); null without elevation. */
export function elevationAt(points: Pt[], distM: number): number | null {
  for (let i = 1; i < points.length; i++) {
    if (points[i][3] >= distM) {
      const a = points[i - 1][2];
      const b = points[i][2];
      if (a === null || b === null) return null;
      const f = (distM - points[i - 1][3]) / (points[i][3] - points[i - 1][3] || 1);
      return a + (b - a) * f;
    }
  }
  return points[points.length - 1]?.[2] ?? null;
}

export const kmText = (m: number) => `${(m / 1000).toFixed(2)} km`;
