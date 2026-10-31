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

/** Polls every intervalMs while mounted; keeps the last good data when a poll fails. */
export function usePoll<T>(load: () => Promise<T>, intervalMs: number, deps: unknown[]): { data: T | null; error: unknown } {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    let stopped = false;
    let timer: ReturnType<typeof setTimeout>;
    const run = async () => {
      try {
        const d = await load();
        if (!stopped) { setData(d); setError(null); }
      } catch (e) {
        if (!stopped) setError(e);
      }
      if (!stopped) timer = setTimeout(run, intervalMs);
    };
    run();
    return () => { stopped = true; clearTimeout(timer); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return { data, error };
}

export const isStatus = (e: unknown, status: number) => e instanceof ApiError && e.status === status;
