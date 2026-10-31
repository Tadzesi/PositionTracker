import { useEffect, useRef } from 'react';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';

export interface MapMarker { id: string; lat: number; lon: number; label: string; kind?: 'normal' | 'sos' | 'dim'; selected?: boolean }
export interface MapCheckpoint { name: string; lat: number; lon: number }

interface Props {
  points: [number, number][];
  markers: MapMarker[];
  checkpoints?: MapCheckpoint[];
  selectedId?: string | null;
  onSelect?: (id: string) => void;
  className?: string;
}

/** Leaflet map: the course as a line, riders as numbered markers. Colours follow the theme tokens. */
export default function MapView({ points, markers, checkpoints, selectedId, onSelect, className }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const map = useRef<L.Map | null>(null);
  const course = useRef<L.Polyline | null>(null);
  const layer = useRef<L.LayerGroup | null>(null);
  const fitted = useRef(false);
  const select = useRef(onSelect);
  select.current = onSelect;

  useEffect(() => {
    if (!host.current) return;
    const m = L.map(host.current, { zoomControl: true, attributionControl: true }).setView([48.7, 19.5], 8);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(m);
    layer.current = L.layerGroup().addTo(m);
    map.current = m;
    return () => { m.remove(); map.current = null; course.current = null; layer.current = null; fitted.current = false; };
  }, []);

  useEffect(() => {
    const m = map.current;
    if (!m || points.length < 2) return;
    course.current?.remove();
    course.current = L.polyline(points, { color: '#FF1E3C', weight: 4, opacity: 0.85 }).addTo(m);
    if (!fitted.current) { m.fitBounds(course.current.getBounds(), { padding: [20, 20] }); fitted.current = true; }
  }, [points]);

  useEffect(() => {
    const g = layer.current;
    if (!g) return;
    g.clearLayers();
    for (const c of checkpoints ?? []) {
      L.circleMarker([c.lat, c.lon], { radius: 5, color: '#FFD60A', weight: 2, fillOpacity: 0.9 }).bindTooltip(c.name).addTo(g);
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
  }, [markers, checkpoints, selectedId]);

  return <div ref={host} className={className ?? 'map'} role="application" aria-label="map" />;
}

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
