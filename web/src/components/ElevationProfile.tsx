import { useRef, useState } from 'react';
import { useI18n } from '../lib/i18n';
import { elevationAt, type Pt } from '../lib/gpx';

const W = 640;
const H = 170;
const PAD = { l: 46, r: 12, t: 12, b: 26 };

interface Props {
  points: Pt[];
  lapLengthM: number;
  checkpoints: { name: string; distM: number }[];
  /** Clicking the profile picks a distance (m from the start of the lap). */
  onPick?: (distM: number) => void;
}

/** Elevation profile of one lap: the line, the checkpoints as markers, a read-out under the pointer. */
export default function ElevationProfile({ points, lapLengthM, checkpoints, onPick }: Props) {
  const { t } = useI18n();
  const svg = useRef<SVGSVGElement>(null);
  const [hover, setHover] = useState<number | null>(null);
  const eles = points.map((p) => p[2]).filter((e): e is number => e !== null);
  if (points.length < 2 || eles.length < 2 || lapLengthM <= 0) return null;
  let min = Math.min(...eles);
  let max = Math.max(...eles);
  if (max - min < 4) { const mid = (max + min) / 2; min = mid - 2; max = mid + 2; }
  const iw = W - PAD.l - PAD.r;
  const ih = H - PAD.t - PAD.b;
  const x = (d: number) => PAD.l + (Math.min(Math.max(d, 0), lapLengthM) / lapLengthM) * iw;
  const y = (e: number) => PAD.t + (1 - (e - min) / (max - min)) * ih;
  const usable = points.filter((p) => p[2] !== null);
  const line = usable.map((p, i) => `${i ? 'L' : 'M'}${x(p[3]).toFixed(1)},${y(p[2] as number).toFixed(1)}`).join('');
  const area = `${line}L${x(usable[usable.length - 1][3]).toFixed(1)},${PAD.t + ih}L${x(usable[0][3]).toFixed(1)},${PAD.t + ih}Z`;
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => f * lapLengthM);

  const distanceAt = (clientX: number) => {
    const box = svg.current?.getBoundingClientRect();
    if (!box || box.width === 0) return null;
    const px = ((clientX - box.left) / box.width) * W;
    return Math.min(Math.max(((px - PAD.l) / iw) * lapLengthM, 0), lapLengthM);
  };
  const shown = hover === null ? null : { d: hover, e: elevationAt(points, hover) };

  return (
    <figure className="profile">
      <svg ref={svg} viewBox={`0 0 ${W} ${H}`} role="img" aria-label={t('Výškový profil jedného kola', 'Elevation profile of one lap')}
        style={{ cursor: onPick ? 'crosshair' : 'default' }}
        onPointerMove={(e) => setHover(distanceAt(e.clientX))} onPointerLeave={() => setHover(null)}
        onClick={(e) => { const d = distanceAt(e.clientX); if (d !== null) onPick?.(d); }}>
        <path className="area" d={area} />
        <path className="line" d={line} fill="none" />
        <text className="axis" x={PAD.l - 6} y={PAD.t + 4} textAnchor="end">{Math.round(max)} m</text>
        <text className="axis" x={PAD.l - 6} y={PAD.t + ih} textAnchor="end">{Math.round(min)} m</text>
        {ticks.map((d) => (
          <text key={d} className="axis" x={x(d)} y={H - 6} textAnchor={d === 0 ? 'start' : d === lapLengthM ? 'end' : 'middle'}>{(d / 1000).toFixed(d === 0 ? 0 : 1)} km</text>
        ))}
        {checkpoints.filter((c) => c.distM > 0 && c.distM < lapLengthM).map((c) => (
          <g key={`${c.name}-${c.distM}`}>
            <line className="cp" x1={x(c.distM)} x2={x(c.distM)} y1={PAD.t} y2={PAD.t + ih} />
            <circle className="cpdot" cx={x(c.distM)} cy={PAD.t + 4} r={4}><title>{c.name}</title></circle>
          </g>
        ))}
        {shown && <line className="cursor" x1={x(shown.d)} x2={x(shown.d)} y1={PAD.t} y2={PAD.t + ih} />}
      </svg>
      <figcaption className="mute">
        {shown
          ? `${(shown.d / 1000).toFixed(2)} km${shown.e !== null ? ` · ${Math.round(shown.e)} m` : ''}`
          : ' '}
      </figcaption>
    </figure>
  );
}
