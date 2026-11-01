/** A rider on the profile: distance from the start of the lap (m); pinned riders are never merged into a group. */
export interface ProfileRider { id: string; distM: number; pinned: boolean }

/** One mark on the profile: a single rider, or a group (a bubble with a count) of riders that would overlap. */
export interface RiderGroup { key: string; distM: number; members: ProfileRider[] }

/**
 * Groups the riders that are closer than <paramref minGapPx> on screen (chained: a rider joins the group when it is
 * within the gap of the previous one). Pinned riders (selected, followed, leaders, alarms) stay single. The group's
 * distance is the mean of its members. The result is ordered by distance.
 */
export function groupRiders(riders: ProfileRider[], toX: (distM: number) => number, minGapPx: number): RiderGroup[] {
  const groups: RiderGroup[] = [];
  const free = riders.filter((r) => !r.pinned).sort((a, b) => a.distM - b.distM);
  let current: ProfileRider[] = [];
  const flush = () => {
    if (current.length === 0) return;
    groups.push({ key: current.map((m) => m.id).join('|'), distM: current.reduce((s, m) => s + m.distM, 0) / current.length, members: current });
    current = [];
  };
  for (const r of free) {
    if (current.length > 0 && toX(r.distM) - toX(current[current.length - 1].distM) >= minGapPx) flush();
    current.push(r);
  }
  flush();
  for (const r of riders.filter((x) => x.pinned)) groups.push({ key: r.id, distM: r.distM, members: [r] });
  return groups.sort((a, b) => a.distM - b.distM);
}

/** Riders per bin along [from, to] (for the density band of a big field). */
export function densityBins(distances: number[], from: number, to: number, bins: number): number[] {
  const out = new Array<number>(bins).fill(0);
  if (!(to > from) || bins <= 0) return out;
  for (const d of distances) {
    if (d < from || d > to) continue;
    out[Math.min(bins - 1, Math.floor(((d - from) / (to - from)) * bins))]++;
  }
  return out;
}

/** Assigns each label (centre x, width) to the first of <paramref rows> rows where it does not touch the previous label of that row. */
export function assignRows(labels: { x: number; w: number }[], rows: number, gap = 2): number[] {
  const order = labels.map((_, i) => i).sort((a, b) => labels[a].x - labels[b].x);
  const right = new Array<number>(rows).fill(-Infinity);
  const result = new Array<number>(labels.length).fill(0);
  for (const i of order) {
    const left = labels[i].x - labels[i].w / 2;
    let row = right.findIndex((r) => left >= r + gap);
    if (row < 0) row = right.indexOf(Math.min(...right)); // all rows taken here: use the one that frees up first (a small overlap)
    result[i] = row;
    right[row] = labels[i].x + labels[i].w / 2;
  }
  return result;
}

/** The window of the profile to show: the whole lap, or a stretch of <paramref widthM> around a distance, kept inside the lap. */
export function windowAround(centerM: number, widthM: number, lapLengthM: number): [number, number] {
  if (widthM >= lapLengthM) return [0, lapLengthM];
  const from = Math.min(Math.max(centerM - widthM / 2, 0), lapLengthM - widthM);
  return [from, from + widthM];
}

/** A rider's distance within the current lap (m), from the total distance and the lap number; a finished rider is at the end. */
export function lapDistance(totalDistanceM: number, lap: number, lapLengthM: number, finished: boolean): number {
  if (finished) return lapLengthM;
  return Math.min(Math.max(totalDistanceM - (Math.max(lap, 1) - 1) * lapLengthM, 0), lapLengthM);
}
