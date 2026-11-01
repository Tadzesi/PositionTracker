import { render, screen, fireEvent, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { I18nProvider } from '../lib/i18n';
import LiveProfile, { type LiveProfileRider } from './LiveProfile';

const profile: [number, number][] = [[0, 300], [2500, 350], [5000, 320], [7500, 380], [10000, 300]];
const rider = (i: number, distM: number, extra: Partial<LiveProfileRider> = {}): LiveProfileRider =>
  ({ id: `r${i}`, bib: String(i), name: `Rider ${i}`, distM, rank: i + 1, alarm: false, followed: false, ...extra });

function show(riders: LiveProfileRider[], selectedId: string | null = null, onSelect = vi.fn()) {
  render(<I18nProvider><LiveProfile profile={profile} lapLengthM={10000} checkpoints={[]} riders={riders} selectedId={selectedId} onSelect={onSelect} /></I18nProvider>);
  return onSelect;
}

describe('LiveProfile', () => {
  it('merges a pack into a bubble with a count and lists its riders when opened', () => {
    // 40 riders within 200 m, plus the leader far ahead (a leader stays single)
    const pack = Array.from({ length: 40 }, (_, i) => rider(i + 5, 4000 + i * 5));
    const onSelect = show([rider(0, 9000), ...pack]);
    const bubble = screen.getByRole('button', { name: /40 riders/ });
    fireEvent.click(bubble);
    const list = screen.getByRole('group', { name: 'Riders in the group' });
    expect(within(list).getAllByRole('row')).toHaveLength(40);
    fireEvent.click(within(list).getAllByRole('row')[0]);
    expect(onSelect).toHaveBeenCalledWith('r5');
  });

  it('keeps the selected rider and an alarmed one single even inside a pack', () => {
    const pack = Array.from({ length: 10 }, (_, i) => rider(i + 5, 4000 + i * 5));
    pack[3] = { ...pack[3], alarm: true };
    show(pack, 'r5');
    expect(screen.getByRole('button', { name: '#5 Rider 5' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '#8 Rider 8' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /8 riders/ })).toBeInTheDocument();
  });

  it('zooms to the front and says how many riders are outside the shown stretch', () => {
    show([rider(0, 500), rider(1, 9500)]);
    fireEvent.click(screen.getByRole('button', { name: 'Around the front' }));
    expect(screen.getByText(/1 outside the shown stretch/)).toBeInTheDocument();
  });
});
