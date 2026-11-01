import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import { I18nProvider } from '../lib/i18n';
import { formatDateTime } from '../lib/format';
import DateTimeField, { parseDateText, parseTimeText } from './DateTimeField';

describe('date and time text', () => {
  it('reads the day-first forms used in the EU', () => {
    for (const s of ['24.3.2026', '24. 3. 2026', '24/03/2026', '24-3-2026', '2026-03-24', ' 24 . 03 . 2026 ']) expect(parseDateText(s)).toBe('2026-03-24');
  });
  it('refuses impossible dates and other text', () => {
    for (const s of ['31.2.2026', '2026-13-01', '24.3.26', 'zajtra', '']) expect(parseDateText(s)).toBeNull();
  });
  it('reads 24-hour times', () => {
    expect(parseTimeText('9:05')).toBe('09:05');
    expect(parseTimeText('14.30')).toBe('14:30');
    for (const s of ['24:00', '12:60', '1430', '2pm']) expect(parseTimeText(s)).toBeNull();
  });
});

describe('formatDateTime', () => {
  it('writes the day first with 24 hours in both languages', () => {
    const iso = '2026-03-24T13:05:00Z';
    expect(formatDateTime(iso, 'sk')).toMatch(/^\d{1,2}\. \d{1,2}\. 2026,? \d{2}:\d{2}$/);
    expect(formatDateTime(iso, 'en')).toMatch(/^\d{2}\/\d{2}\/2026,? \d{2}:\d{2}$/);
    expect(formatDateTime(iso, 'en')).not.toMatch(/AM|PM/i);
  });
});

function Harness({ initial = '' }: { initial?: string }) {
  const [v, setV] = useState(initial);
  return <I18nProvider initial="sk"><DateTimeField id="dt" value={v} onChange={setV} /><output data-testid="v">{v}</output></I18nProvider>;
}

describe('DateTimeField', () => {
  it('gives the datetime-local value once both parts are valid and spells the date out', async () => {
    render(<Harness />);
    await userEvent.type(screen.getByLabelText('Dátum'), '24.3.2026');
    expect(screen.getByTestId('v')).toHaveTextContent('');
    await userEvent.type(screen.getByLabelText('Čas'), '14:30');
    expect(screen.getByTestId('v')).toHaveTextContent('2026-03-24T14:30');
    expect(screen.getByText(/utorok 24\. marca 2026/)).toBeInTheDocument();
  });

  it('shows an existing value in the Slovak form and flags a typo', async () => {
    render(<Harness initial="2026-11-08T10:00" />);
    expect(screen.getByLabelText('Dátum')).toHaveValue('8. 11. 2026');
    expect(screen.getByLabelText('Čas')).toHaveValue('10:00');
    await userEvent.clear(screen.getByLabelText('Dátum'));
    await userEvent.type(screen.getByLabelText('Dátum'), '31.2.2026');
    expect(await screen.findByRole('alert')).toHaveTextContent('24. 3. 2026');
    expect(screen.getByTestId('v')).toHaveTextContent('');
  });
});
