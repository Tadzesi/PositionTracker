import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it } from 'vitest';
import { I18nProvider } from '../lib/i18n';
import MapView from './MapView';

const points: [number, number][] = [[48.7, 18.8], [48.71, 18.81], [48.72, 18.8]];
const markers = [{ id: 'a', lat: 48.7, lon: 18.8, label: '1' }, { id: 'b', lat: 48.71, lon: 18.81, label: '2' }];
const ui = (follow: string[]) => <I18nProvider><MapView points={points} markers={markers} followIds={follow} /></I18nProvider>;

it('offers the whole-course / follow button only while somebody is followed', async () => {
  const { rerender } = render(ui([]));
  expect(screen.queryByRole('button', { name: /Whole course|Celá trať|Follow|Sledovať/ })).toBeNull();
  rerender(ui(['a']));
  const btn = await screen.findByRole('button', { name: /Whole course|Celá trať/ });
  await userEvent.click(btn); // back to the whole course: the next click would follow again
  expect(await screen.findByRole('button', { name: /^Follow$|^Sledovať$/ })).toBeInTheDocument();
  rerender(ui([]));
  expect(screen.queryByRole('button', { name: /Whole course|Celá trať|^Follow$|^Sledovať$/ })).toBeNull();
});
