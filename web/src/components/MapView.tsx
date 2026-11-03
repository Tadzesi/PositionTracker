import { useCallback, useEffect, useRef, useState } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';
import { useI18n } from '../lib/i18n';

export interface MapMarker { id: string; lat: number; lon: number; label: string; kind?: 'normal' | 'sos' | 'dim'; selected?: boolean }
export interface MapCheckpoint { name: string; lat: number; lon: number }

interface Props {
  points: [number, number][];
  markers: MapMarker[];
  checkpoints?: MapCheckpoint[];
  selectedId?: string | null;
  onSelect?: (id: string) => void;
  /** Marker ids of the followed riders: the map zooms to them and keeps them in view until the user moves the map. */
  followIds?: string[];
  className?: string;
  /** A click on the map (not on a marker): used to place checkpoints on the course. */
  onMapClick?: (lat: number, lon: number) => void;
}

const FOLLOW_ZOOM = 15;
const MAX_FOLLOW_ZOOM = 16;
const AHEAD_S = 4; // positions arrive every ~3 s: keep the point the rider is heading to in view too
const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

/** Leaflet map: the course as a line, riders as numbered markers. Colours follow the theme tokens. */
export default function MapView({ points, markers, checkpoints, selectedId, onSelect, followIds, className, onMapClick }: Props) {
  const { t } = useI18n();
  const follow = followIds ?? NONE;
  const host = useRef<HTMLDivElement>(null);
  const map = useRef<L.Map | null>(null);
  const course = useRef<L.Polyline | null>(null);
  const layer = useRef<L.LayerGroup | null>(null);
  const fitted = useRef(false);
  const select = useRef(onSelect);
  select.current = onSelect;
  const click = useRef(onMapClick);
  click.current = onMapClick;

  // ----- following: zoom to the followed riders and keep them in view until the user moves the map -----
  const [auto, setAutoState] = useState(follow.length > 0);
  const autoRef = useRef(auto);
  const pending = useRef(follow.length > 0); // a followed rider from a previous visit: zoom as soon as his marker arrives
  const followRef = useRef(follow);
  followRef.current = follow;
  const markersRef = useRef(markers);
  markersRef.current = markers;
  const motion = useRef(new Map<string, { lat: number; lon: number; t: number; vLat: number; vLon: number }>()); // per rider: speed from the last two polls
  const setAuto = useCallback((v: boolean) => { autoRef.current = v; setAutoState(v); if (!v) pending.current = false; }, []);
  const fitCourse = useCallback(() => {
    const m = map.current, c = course.current;
    if (m && c) m.fitBounds(c.getBounds(), { padding: [24, 24], animate: !reducedMotion() });
  }, []);
  // Keeps the followed riders in view with the best zoom: zooms out at once when they (or where they are heading) no longer fit in the middle of
  // the map, zooms in only when they would fit a whole zoom level closer, otherwise just pans.
  const followView = useCallback((force: boolean) => {
    const m = map.current;
    if (!m || !followRef.current.length) return;
    const now = Date.now();
    const pairs: L.LatLng[][] = [];
    for (const x of markersRef.current) {
      if (!followRef.current.includes(x.id)) continue;
      const prev = motion.current.get(x.id);
      let e = prev ?? { lat: x.lat, lon: x.lon, t: now, vLat: 0, vLon: 0 };
      if (prev && (prev.lat !== x.lat || prev.lon !== x.lon)) {
        const dt = (now - prev.t) / 1000;
        e = dt > 0.2 ? { lat: x.lat, lon: x.lon, t: now, vLat: (x.lat - prev.lat) / dt, vLon: (x.lon - prev.lon) / dt } : { ...prev, lat: x.lat, lon: x.lon };
      }
      motion.current.set(x.id, e);
      pairs.push([L.latLng(x.lat, x.lon), L.latLng(x.lat + e.vLat * AHEAD_S, x.lon + e.vLon * AHEAD_S)]);
    }
    if (!pairs.length) return;
    const all = pairs.flat(), bounds = L.latLngBounds(all), multi = pairs.length > 1;
    const size = m.getSize(), zoom = m.getZoom();
    const hidden = (ll: L.LatLng) => {
      const p = m.latLngToContainerPoint(ll);
      return p.x < size.x * 0.15 || p.x > size.x * 0.85 || p.y < size.y * 0.15 || p.y > size.y * 0.85;
    };
    const best = Math.min(MAX_FOLLOW_ZOOM, m.getBoundsZoom(bounds, false, L.point(size.x * 0.4, size.y * 0.4)));
    const zoomChange = best < zoom - 0.01 || (multi && best >= zoom + 1);
    if (!force && !zoomChange && !all.some(hidden)) return;
    const animate = !reducedMotion();
    if (force && multi) m.fitBounds(bounds, { padding: [40, 40], maxZoom: MAX_FOLLOW_ZOOM, animate });
    else if (zoomChange && multi) m.setView(bounds.getCenter(), best, { animate });
    else if (force) m.setView(bounds.getCenter(), Math.max(zoom, FOLLOW_ZOOM), { animate });
    else m.panTo(bounds.getCenter(), { animate });
  }, []);

  useEffect(() => {
    if (!host.current) return;
    const m = L.map(host.current, { zoomControl: true, attributionControl: true, zoomSnap: 0.25 }).setView([48.7, 19.5], 8);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(m);
    layer.current = L.layerGroup().addTo(m);
    map.current = m;
    m.on('click', (e: L.LeafletMouseEvent) => click.current?.(e.latlng.lat, e.latlng.lng));
    const stop = () => { if (autoRef.current) setAuto(false); };
    // only the user's own gestures stop the following (not 'zoomstart': our own zooms fire it too)
    const box = m.getContainer();
    m.on('dragstart', stop);
    m.on('dblclick', stop);
    box.addEventListener('wheel', stop, { passive: true });
    box.addEventListener('touchstart', (e) => { if (e.touches.length > 1) stop(); }, { passive: true }); // a pinch
    box.addEventListener('click', (e) => { if ((e.target as HTMLElement).closest?.('.leaflet-control-zoom')) stop(); }, true);
    box.addEventListener('keydown', (e) => { if (/^(\+|-|=|_|Arrow\w+)$/.test(e.key)) stop(); });
    return () => { m.remove(); map.current = null; course.current = null; layer.current = null; fitted.current = false; };
  }, [setAuto]);

  useEffect(() => {
    const m = map.current;
    if (!m || points.length < 2) return;
    course.current?.remove();
    course.current = L.polyline(points, { color: '#FF1E3C', weight: 4, opacity: 0.85 }).addTo(m);
    // the first fit is not animated: a remembered rider zooms right after it, and two animated moves in one tick cancel each other
    if (!fitted.current) { m.invalidateSize(); m.fitBounds(course.current.getBounds(), { padding: [24, 24], animate: false }); fitted.current = true; }
  }, [points]);

  useEffect(() => {
    const g = layer.current;
    if (!g) return;
    g.clearLayers();
    for (const c of checkpoints ?? []) {
      L.circleMarker([c.lat, c.lon], { radius: 5, color: '#FFD60A', weight: 2, fillOpacity: 0.9 }).bindTooltip(escapeHtml(c.name)).addTo(g);
    }
    for (const mk of markers) {
      const selected = mk.selected || mk.id === selectedId;
      const size = selected ? 34 : mk.kind === 'dim' ? 16 : 26;
      const icon = L.divIcon({
        className: '',
        iconSize: [size, size],
        html: `<div class="mk ${mk.kind ?? ''} ${selected ? 'sel' : ''}" style="width:${size}px;height:${size}px">${size > 20 ? escapeHtml(mk.label) : ''}</div>`,
      });
      const marker = L.marker([mk.lat, mk.lon], { icon, zIndexOffset: selected ? 1000 : 0, keyboard: false }).addTo(g);
      marker.on('click', () => select.current?.(mk.id));
    }
    // the polled positions keep the followed riders in view; the first arrival of a remembered rider zooms to him
    if (pending.current && markers.some((x) => followRef.current.includes(x.id))) { pending.current = false; followView(true); }
    else if (autoRef.current) followView(false);
  }, [markers, checkpoints, selectedId, followView]);

  // a rider was followed: zoom to the followed ones; the last one was dropped: the whole course again
  const was = useRef(follow);
  const followKey = follow.join(',');
  useEffect(() => {
    const before = was.current;
    was.current = follow;
    if (!follow.length && before.length) { setAuto(false); fitCourse(); }
    else if (follow.some((id) => !before.includes(id))) { setAuto(true); followView(true); }
    else if (follow.length < before.length && autoRef.current) followView(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [followKey]);

  const toggleAuto = () => {
    if (autoRef.current) { setAuto(false); fitCourse(); } else { setAuto(true); followView(true); }
  };

  return (
    <div className="mapwrap">
      <div ref={host} className={className ?? 'map'} role="application" aria-label="map" />
      {follow.length > 0 && (
        <button type="button" className="btn small mapbtn" onClick={toggleAuto}>
          <span>{auto ? t('Celá trať', 'Whole course') : t('Sledovať', 'Follow')}</span>
        </button>
      )}
    </div>
  );
}

const NONE: string[] = [];

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]!);
}

/** The position on the lap polyline at a distance (m) along it, for checkpoint markers. */
export function pointAt(points: [number, number][], lapLengthM: number, distanceM: number): [number, number] | null {
  if (points.length < 2 || lapLengthM <= 0) return null;
  const cum = [0];
  for (let i = 1; i < points.length; i++) cum.push(cum[i - 1] + haversine(points[i - 1], points[i]));
  const total = cum[cum.length - 1] || 1;
  const target = Math.min(Math.max(distanceM / lapLengthM, 0), 1) * total;
  for (let i = 1; i < points.length; i++) {
    if (cum[i] >= target) {
      const f = (target - cum[i - 1]) / (cum[i] - cum[i - 1] || 1);
      return [points[i - 1][0] + (points[i][0] - points[i - 1][0]) * f, points[i - 1][1] + (points[i][1] - points[i - 1][1]) * f];
    }
  }
  return points[points.length - 1];
}

function haversine(a: [number, number], b: [number, number]): number {
  const r = 6371000;
  const rad = (d: number) => (d * Math.PI) / 180;
  const dLat = rad(b[0] - a[0]);
  const dLon = rad(b[1] - a[1]);
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(rad(a[0])) * Math.cos(rad(b[0])) * Math.sin(dLon / 2) ** 2;
  return 2 * r * Math.asin(Math.sqrt(h));
}
