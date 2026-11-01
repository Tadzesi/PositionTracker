import { useEffect, useMemo, useRef, useState } from 'react';
import { useI18n } from '../lib/i18n';
import { assignRows, densityBins, groupRiders, windowAround, type RiderGroup } from '../lib/profileCluster';

export interface LiveProfileRider {
  id: string; bib: string; name: string;
  /** Distance within the current lap (m). */
  distM: number;
  rank: number | null; alarm: boolean; followed: boolean; gap?: string | null;
}

interface Props {
  /** [[distanceM, elevationM]…] of one lap; null for a course without elevation (a flat line is drawn). */
  profile: [number, number][] | null;
  lapLengthM: number;
  checkpoints: { name: string; distM: number }[];
  riders: LiveProfileRider[];
  selectedId: string | null;
  onSelect: (id: string | null) => void;
}

const H = 230;
const ROWS = 3;
const ROW_H = 24;
const PAD = { l: 44, r: 12, t: 14 + ROWS * ROW_H, b: 44 };
const MIN_GAP_PX = 24;
const BIG_FIELD = 30;
const ZOOM_AROUND_M = 3000;

function sliceProfile(profile: [number, number][], from: number, to: number): [number, number][] {
  const inside = profile.filter(([d]) => d > from && d < to);
  return [[from, eleAt(profile, from)], ...inside, [to, eleAt(profile, to)]];
}

function eleAt(profile: [number, number][], d: number): number {
  for (let i = 1; i < profile.length; i++) {
    if (profile[i][0] >= d) {
      const [d0, e0] = profile[i - 1];
      const [d1, e1] = profile[i];
      return e0 + (e1 - e0) * ((d - d0) / (d1 - d0 || 1));
    }
  }
  return profile[profile.length - 1]?.[1] ?? 0;
}

/**
 * Where the riders are on the elevation profile of the lap. With many riders the marks would pile up, so riders that
 * would overlap on screen are merged into a bubble with a count (it opens a list), while the selected, followed and
 * alarmed riders and the first three stay single. A big field also gets a density band; the profile can be zoomed to
 * the selected rider or to the front.
 */
export default function LiveProfile({ profile, lapLengthM, checkpoints, riders, selectedId, onSelect }: Props) {
  const { t } = useI18n();
  const box = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(640);
  const [win, setWin] = useState<[number, number]>([0, lapLengthM]);
  const [open, setOpen] = useState<string | null>(null);

  useEffect(() => { setWin([0, lapLengthM]); }, [lapLengthM]);
  useEffect(() => {
    const el = box.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setWidth(Math.max(280, Math.round(el.clientWidth))));
    ro.observe(el);
    setWidth(Math.max(280, Math.round(el.clientWidth) || 640));
    return () => ro.disconnect();
  }, []);

  const line = useMemo(() => profile && profile.length >= 2 ? profile : [[0, 0], [lapLengthM, 0]] as [number, number][], [profile, lapLengthM]);
  const [from, to] = win;
  const span = Math.max(to - from, 1);
  const iw = width - PAD.l - PAD.r;
  const ih = H - PAD.t - PAD.b;
  const x = (d: number) => PAD.l + ((d - from) / span) * iw;
  const eles = line.map((p) => p[1]);
  let min = Math.min(...eles);
  let max = Math.max(...eles);
  if (max - min < 4) { const mid = (max + min) / 2; min = mid - 2; max = mid + 2; }
  const y = (e: number) => PAD.t + (1 - (e - min) / (max - min)) * ih;

  const visible = riders.filter((r) => r.distM >= from && r.distM <= to);
  const outside = riders.length - visible.length;
  const leaders = new Set(riders.filter((r) => r.rank !== null && r.rank <= 3).map((r) => r.id));
  const groups: RiderGroup[] = useMemo(() => groupRiders(
    visible.map((r) => ({ id: r.id, distM: r.distM, pinned: r.id === selectedId || r.followed || r.alarm || leaders.has(r.id) })),
    x, MIN_GAP_PX,
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ), [riders, selectedId, from, to, width]);
  const byId = useMemo(() => new Map(riders.map((r) => [r.id, r])), [riders]);

  const labelW = (g: RiderGroup) => g.members.length > 1 ? 30 : Math.max(24, 8 * (byId.get(g.members[0].id)?.bib.length ?? 1) + 12);
  const rows = assignRows(groups.map((g) => ({ x: x(g.distM), w: labelW(g) })), ROWS);

  const path = sliceProfile(line, from, to);
  const d = path.map(([dist, e], i) => `${i ? 'L' : 'M'}${x(dist).toFixed(1)},${y(e).toFixed(1)}`).join('');
  const area = `${d}L${x(to).toFixed(1)},${PAD.t + ih}L${x(from).toFixed(1)},${PAD.t + ih}Z`;
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => from + f * span);

  const bins = riders.length >= BIG_FIELD ? densityBins(riders.map((r) => r.distM), from, to, Math.max(20, Math.floor(iw / 8))) : null;
  const binMax = bins ? Math.max(...bins, 1) : 1;

  const selected = selectedId ? byId.get(selectedId) ?? null : null;
  const leader = riders.find((r) => r.rank === 1) ?? null;
  const opened = open ? groups.find((g) => g.key === open) ?? null : null;
  const zoomed = span < lapLengthM - 1;
  const zoomTo = (center: number | undefined) => { if (center !== undefined) setWin(windowAround(center, ZOOM_AROUND_M, lapLengthM)); };
  const zoomBy = (factor: number) => setWin(windowAround((from + to) / 2, Math.min(lapLengthM, Math.max(500, span * factor)), lapLengthM));
  const pan = (dir: number) => setWin(windowAround((from + to) / 2 + dir * span / 2, span, lapLengthM));
  const press = (fn: () => void) => ({
    role: 'button' as const, tabIndex: 0, onClick: fn,
    onKeyDown: (e: React.KeyboardEvent) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fn(); } },
  });

  return (
    <figure className="live-profile">
      <div className="row live-profile-bar">
        <button type="button" className="chip" aria-pressed={!zoomed} onClick={() => setWin([0, lapLengthM])}>{t('Celá trať', 'Whole course')}</button>
        <button type="button" className="chip" disabled={!selected} onClick={() => zoomTo(selected?.distM)}>{t('Okolo sledovaného', 'Around selected')}</button>
        <button type="button" className="chip" disabled={!leader} onClick={() => zoomTo(leader?.distM)}>{t('Okolo čela', 'Around the front')}</button>
        <span className="spacer" />
        <button type="button" className="chip" aria-label={t('Priblížiť', 'Zoom in')} onClick={() => zoomBy(0.5)}>+</button>
        <button type="button" className="chip" aria-label={t('Oddialiť', 'Zoom out')} onClick={() => zoomBy(2)}>−</button>
        <button type="button" className="chip" aria-label={t('Posunúť späť', 'Pan back')} disabled={!zoomed || from <= 0} onClick={() => pan(-1)}>◀</button>
        <button type="button" className="chip" aria-label={t('Posunúť dopredu', 'Pan forward')} disabled={!zoomed || to >= lapLengthM} onClick={() => pan(1)}>▶</button>
      </div>
      <div ref={box}>
        <svg viewBox={`0 0 ${width} ${H}`} width="100%" role="img"
          aria-label={t('Poloha pretekárov na výškovom profile kola', 'Riders on the elevation profile of the lap')}>
          <path className="area" d={area} />
          <path className="line" d={d} fill="none" />
          <text className="axis" x={PAD.l - 6} y={PAD.t + 4} textAnchor="end">{profile ? `${Math.round(max)} m` : ''}</text>
          <text className="axis" x={PAD.l - 6} y={PAD.t + ih} textAnchor="end">{profile ? `${Math.round(min)} m` : ''}</text>
          {ticks.map((tick, i) => (
            <text key={i} className="axis" x={x(tick)} y={H - 6} textAnchor={i === 0 ? 'start' : i === ticks.length - 1 ? 'end' : 'middle'}>{(tick / 1000).toFixed(1)} km</text>
          ))}
          {checkpoints.filter((c) => c.distM > from && c.distM < to).map((c) => (
            <g key={`${c.name}-${c.distM}`}>
              <line className="cp" x1={x(c.distM)} x2={x(c.distM)} y1={PAD.t} y2={PAD.t + ih} />
              <circle className="cpdot" cx={x(c.distM)} cy={PAD.t + ih - 4} r={4}><title>{c.name}</title></circle>
            </g>
          ))}
          {bins && bins.map((n, i) => n > 0 && (
            <rect key={i} className="dens" x={PAD.l + (i / bins.length) * iw} y={H - 38} width={iw / bins.length + 0.5} height={8} style={{ opacity: 0.15 + 0.85 * (n / binMax) }}>
              <title>{n}</title>
            </rect>))}
          {groups.map((g, i) => {
            const gx = x(g.distM);
            const cy = 14 + rows[i] * ROW_H + ROW_H / 2 - 2;
            const single = g.members.length === 1;
            const r = single ? byId.get(g.members[0].id)! : null;
            const dotY = y(eleAt(line, g.distM));
            const cls = single
              ? `lp-mark${r!.id === selectedId ? ' sel' : ''}${r!.followed ? ' fol' : ''}${r!.alarm ? ' alarm' : ''}${r!.rank !== null && r!.rank <= 3 ? ' top' : ''}`
              : `lp-mark bubble${g.key === open ? ' sel' : ''}`;
            const w = labelW(g);
            return (
              <g key={g.key} className={cls}
                {...press(() => (single ? onSelect(r!.id === selectedId ? null : r!.id) : setOpen(open === g.key ? null : g.key)))}
                aria-label={single ? `#${r!.bib} ${r!.name}` : t(`Skupina: ${g.members.length} pretekárov`, `Group: ${g.members.length} riders`)}>
                <line className="stem" x1={gx} x2={gx} y1={cy + 9} y2={dotY} />
                <circle className="pin" cx={gx} cy={dotY} r={3.5} />
                <rect className="tag" x={gx - w / 2} y={cy - 10} width={w} height={20} rx={10} />
                <text className="tagtext" x={gx} y={cy + 4} textAnchor="middle">{single ? r!.bib : g.members.length}</text>
              </g>);
          })}
        </svg>
      </div>
      <figcaption className="mute">
        {selected
          ? `#${selected.bib} ${selected.name}${selected.rank ? ` · ${t('poradie', 'rank')} ${selected.rank}` : ''} · ${(selected.distM / 1000).toFixed(2)} km${selected.gap ? ` · ${selected.gap}` : ''}`
          : t(`${riders.length} pretekárov na trati`, `${riders.length} riders on the course`)}
        {outside > 0 && ` · ${t(`${outside} mimo zobrazeného úseku`, `${outside} outside the shown stretch`)}`}
      </figcaption>
      {opened && opened.members.length > 1 && (
        <div className="live-profile-group card" role="group" aria-label={t('Pretekári v skupine', 'Riders in the group')}>
          <div className="row" style={{ justifyContent: 'space-between' }}>
            <b>{t(`Skupina: ${opened.members.length} pretekárov okolo ${(opened.distM / 1000).toFixed(1)} km`, `Group: ${opened.members.length} riders around ${(opened.distM / 1000).toFixed(1)} km`)}</b>
            <button type="button" className="chip" onClick={() => setOpen(null)}>✕</button>
          </div>
          <div className="scroll" style={{ maxHeight: 180 }}><table><tbody>
            {opened.members.map((m) => byId.get(m.id)!).sort((a, b) => (a.rank ?? 1e9) - (b.rank ?? 1e9)).map((r) => (
              <tr key={r.id} className="click" onClick={() => { onSelect(r.id); setOpen(null); }}>
                <td>{r.rank ?? '–'}</td><td>#{r.bib}</td><td>{r.name}</td><td className="num">{r.gap ?? ''}</td>
              </tr>))}
          </tbody></table></div>
        </div>)}
    </figure>
  );
}
