/** Parses the server's TimeSpan text ("[d.]hh:mm:ss[.fff]", optional leading "-") into seconds. */
export function parseSpan(span: string | null | undefined): number | null {
  if (!span) return null;
  const m = /^(-)?(?:(\d+)\.)?(\d+):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(span);
  if (!m) return null;
  const seconds = Number(m[2] ?? 0) * 86400 + Number(m[3]) * 3600 + Number(m[4]) * 60 + Number(m[5]) + (m[6] ? Number(`0.${m[6]}`) : 0);
  return m[1] ? -seconds : seconds;
}

/** m:ss or h:mm:ss. */
export function formatDuration(seconds: number | null): string {
  if (seconds === null || !Number.isFinite(seconds)) return '–';
  const s = Math.round(Math.abs(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const ss = String(s % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${ss}` : `${m}:${ss}`;
}

export function formatGap(span: string | null | undefined): string {
  const s = parseSpan(span);
  return s === null ? '–' : `+${formatDuration(s)}`;
}

export function formatKm(meters: number): string {
  return `${(meters / 1000).toFixed(2)} km`;
}

export function formatSpeed(mps: number, sport: 'running' | 'cycling', pace: number | null): string {
  if (sport === 'running') return pace ? `${formatDuration(pace)} /km` : '–';
  return `${(mps * 3.6).toFixed(1)} km/h`;
}

export function formatDateTime(iso: string, lang: 'sk' | 'en'): string {
  return new Date(iso).toLocaleString(lang === 'sk' ? 'sk-SK' : 'en-GB', { dateStyle: 'medium', timeStyle: 'short' });
}

export function formatTime(iso: string, lang: 'sk' | 'en'): string {
  return new Date(iso).toLocaleTimeString(lang === 'sk' ? 'sk-SK' : 'en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

/** The value of a datetime-local input (local time) as a UTC ISO string with "Z"; null when empty or invalid. */
export function localInputToUtc(value: string): string | null {
  if (!value) return null;
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString().replace(/\.\d{3}Z$/, 'Z');
}

/** A UTC ISO string as the value of a datetime-local input (local time). */
export function utcToLocalInput(iso: string): string {
  const d = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
