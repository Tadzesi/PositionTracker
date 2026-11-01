import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from './api';

export interface Loaded<T> { data: T | null; error: unknown; loading: boolean; reload: () => void; setData: (d: T) => void }

/** Loads once (and on reload()); the latest call wins. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): Loaded<T> {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [loading, setLoading] = useState(true);
  const [tick, setTick] = useState(0);
  const seq = useRef(0);
  useEffect(() => {
    const mine = ++seq.current;
    setLoading(true);
    load().then(
      (d) => { if (mine === seq.current) { setData(d); setError(null); setLoading(false); } },
      (e) => { if (mine === seq.current) { setError(e); setLoading(false); } },
    );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick]);
  const reload = useCallback(() => setTick((t) => t + 1), []);
  return { data, error, loading, reload, setData };
}

const MAX_BACKOFF_MS = 30_000;

/**
 * Polls every intervalMs while mounted; keeps the last good data when a poll fails. A hidden tab does not poll (it asks at once
 * when it becomes visible again), a failing poll backs off (doubling up to 30 s) instead of hammering a server that is refusing,
 * and with <c>stopWhen</c> the polling ends once the data are what is wanted (a static answer needs fetching only once).
 */
export function usePoll<T>(load: () => Promise<T>, intervalMs: number, deps: unknown[], options?: { stopWhen?: (data: T) => boolean }): { data: T | null; error: unknown } {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    let stopped = false;
    let paused = false;
    let failures = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const later = (ms: number) => { clearTimeout(timer); if (!stopped) timer = setTimeout(run, ms); };
    async function run() {
      if (document.visibilityState === 'hidden') { paused = true; return; }
      paused = false;
      try {
        const d = await load();
        if (stopped) return;
        setData(d); setError(null); failures = 0;
        if (options?.stopWhen?.(d)) return;
        later(intervalMs);
      } catch (e) {
        if (stopped) return;
        setError(e); failures++;
        later(Math.min(intervalMs * 2 ** failures, MAX_BACKOFF_MS));
      }
    }
    const onVisible = () => { if (document.visibilityState === 'visible' && paused && !stopped) { clearTimeout(timer); void run(); } };
    document.addEventListener('visibilitychange', onVisible);
    void run();
    return () => { stopped = true; clearTimeout(timer); document.removeEventListener('visibilitychange', onVisible); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return { data, error };
}

export const isStatus = (e: unknown, status: number) => e instanceof ApiError && e.status === status;
