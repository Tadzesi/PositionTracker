import { afterEach, expect, it, vi } from 'vitest';
import { get } from './api';

type Early = Record<string, { t: number; p: Promise<Response> }>;
const win = window as unknown as { __ptEarly?: Early };

afterEach(() => { delete win.__ptEarly; vi.restoreAllMocks(); });

it('takes over the response boot.js started early, once', async () => {
  const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(() => Promise.resolve(new Response('{"from":"network"}')));
  win.__ptEarly = { '/api/x': { t: Date.now(), p: Promise.resolve(new Response('{"from":"early"}')) } };
  expect(await get('/api/x')).toEqual({ from: 'early' });
  expect(fetchSpy).not.toHaveBeenCalled();
  expect(await get('/api/x')).toEqual({ from: 'network' }); // the second call (the next poll) is a normal request
});

it('falls back to a normal request when the early one failed or is stale', async () => {
  const fetchSpy = vi.spyOn(globalThis, 'fetch').mockImplementation(() => Promise.resolve(new Response('{"from":"network"}')));
  win.__ptEarly = { '/api/x': { t: Date.now(), p: Promise.reject(new TypeError('offline')) } };
  expect(await get('/api/x')).toEqual({ from: 'network' });
  win.__ptEarly = { '/api/y': { t: Date.now() - 60000, p: Promise.resolve(new Response('{"from":"early"}')) } };
  expect(await get('/api/y')).toEqual({ from: 'network' });
  expect(fetchSpy).toHaveBeenCalledTimes(2);
});
