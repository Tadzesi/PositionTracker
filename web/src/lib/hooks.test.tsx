import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { usePoll } from './hooks';

const setVisibility = (state: 'visible' | 'hidden') => {
  Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
  document.dispatchEvent(new Event('visibilitychange'));
};

describe('usePoll', () => {
  beforeEach(() => { vi.useFakeTimers(); setVisibility('visible'); });
  afterEach(() => { vi.useRealTimers(); setVisibility('visible'); });

  it('polls on the interval, does not poll in a hidden tab and asks at once when it is visible again', async () => {
    const load = vi.fn().mockResolvedValue(1);
    renderHook(() => usePoll(load, 1000, []));
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(load).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(load).toHaveBeenCalledTimes(2);

    act(() => setVisibility('hidden'));
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(load).toHaveBeenCalledTimes(2); // the poll already waiting woke up, saw the hidden tab and stopped without asking
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(load).toHaveBeenCalledTimes(2);

    await act(async () => { setVisibility('visible'); await vi.advanceTimersByTimeAsync(0); });
    expect(load).toHaveBeenCalledTimes(3);
  });

  it('backs off while the server fails and goes back to the interval after a success', async () => {
    const load = vi.fn().mockRejectedValueOnce(new Error('x')).mockRejectedValueOnce(new Error('x')).mockResolvedValue(1);
    renderHook(() => usePoll(load, 1000, []));
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(load).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1999); });
    expect(load).toHaveBeenCalledTimes(1); // 1st failure: waits 2 s
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(load).toHaveBeenCalledTimes(2);
    await act(async () => { await vi.advanceTimersByTimeAsync(3999); });
    expect(load).toHaveBeenCalledTimes(2); // 2nd failure: waits 4 s
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(load).toHaveBeenCalledTimes(3);
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(load).toHaveBeenCalledTimes(4); // success: back to 1 s
  });

  it('stops polling once stopWhen says the data are final', async () => {
    const load = vi.fn().mockResolvedValue('course');
    renderHook(() => usePoll(load, 1000, [], { stopWhen: () => true }));
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(load).toHaveBeenCalledTimes(1);
  });
});
