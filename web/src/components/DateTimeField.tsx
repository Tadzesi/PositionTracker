import { useEffect, useState } from 'react';
import { useI18n } from '../lib/i18n';

const pad = (n: number) => String(n).padStart(2, '0');

/** "24.3.2026", "24. 3. 2026", "24/3/2026" or "2026-03-24" (day first, as in the EU) → "2026-03-24"; null when it is not a real date. */
export function parseDateText(text: string): string | null {
  const s = text.trim();
  let y: number, m: number, d: number;
  let hit = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(s);
  if (hit) { y = +hit[1]; m = +hit[2]; d = +hit[3]; } else {
    hit = /^(\d{1,2})\s*[./-]\s*(\d{1,2})\s*[./-]\s*(\d{4})\.?$/.exec(s);
    if (!hit) return null;
    d = +hit[1]; m = +hit[2]; y = +hit[3];
  }
  const date = new Date(y, m - 1, d);
  if (date.getFullYear() !== y || date.getMonth() !== m - 1 || date.getDate() !== d) return null;
  return `${y}-${pad(m)}-${pad(d)}`;
}

/** "14:30", "14.30" or "9:05" (24 hours) → "14:30"; null when invalid. */
export function parseTimeText(text: string): string | null {
  const hit = /^(\d{1,2})\s*[:.]\s*(\d{2})$/.exec(text.trim());
  if (!hit) return null;
  const h = +hit[1];
  const m = +hit[2];
  return h > 23 || m > 59 ? null : `${pad(h)}:${pad(m)}`;
}

const shownDate = (iso: string, lang: 'sk' | 'en') => {
  const [y, m, d] = iso.split('-').map(Number);
  return lang === 'sk' ? `${d}. ${m}. ${y}` : `${pad(d)}/${pad(m)}/${y}`;
};

/**
 * A date and a 24-hour time as two text fields in the EU order (day first), instead of the browser's datetime-local, whose format
 * follows the browser language and cannot be set by the page. The value is "YYYY-MM-DDTHH:mm" (the datetime-local value), '' when
 * either part is empty or invalid. Under the fields the understood date is spelled out, so a typo is seen at once.
 */
export default function DateTimeField({ id, value, onChange, disabled, required }: {
  id: string; value: string; onChange: (value: string) => void; disabled?: boolean; required?: boolean;
}) {
  const { t, lang } = useI18n();
  const [d0, t0] = value ? value.split('T') : ['', ''];
  const [dateText, setDateText] = useState(d0 ? shownDate(d0, lang) : '');
  const [timeText, setTimeText] = useState(t0 ?? '');
  // a value that arrives from outside (another event, a reset) replaces the texts; the user's own typing does not
  useEffect(() => {
    const parsedNow = `${parseDateText(dateText) ?? ''}T${parseTimeText(timeText) ?? ''}`;
    if (value !== parsedNow && !(value === '' && parsedNow.length < 16)) {
      const [vd, vt] = value ? value.split('T') : ['', ''];
      setDateText(vd ? shownDate(vd, lang) : '');
      setTimeText(vt ?? '');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  const emit = (dText: string, tText: string) => {
    const d = parseDateText(dText);
    const tm = parseTimeText(tText);
    onChange(d && tm ? `${d}T${tm}` : '');
  };
  const dateIso = parseDateText(dateText);
  const timeOk = parseTimeText(timeText) !== null;
  const spelled = dateIso && timeOk
    ? new Date(`${dateIso}T${parseTimeText(timeText)}`).toLocaleString(lang === 'sk' ? 'sk-SK' : 'en-GB',
      { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit', hour12: false })
    : null;
  const bad = (dateText.trim() !== '' && !dateIso) || (timeText.trim() !== '' && !timeOk);

  return (
    <div className="dtf">
      <div className="row">
        <input id={id} inputMode="numeric" autoComplete="off" style={{ flex: 2, minWidth: 130 }} value={dateText} disabled={disabled} required={required}
          placeholder={t('d. m. rrrr (napr. 24. 3. 2026)', 'dd/mm/yyyy (e.g. 24/03/2026)')} aria-label={t('Dátum', 'Date')} aria-describedby={`${id}-echo`}
          onChange={(e) => { setDateText(e.target.value); emit(e.target.value, timeText); }} />
        <input inputMode="numeric" autoComplete="off" style={{ flex: 1, minWidth: 90 }} value={timeText} disabled={disabled} required={required}
          placeholder={t('hh:mm (24 h)', 'hh:mm (24 h)')} aria-label={t('Čas', 'Time')} aria-describedby={`${id}-echo`}
          onChange={(e) => { setTimeText(e.target.value); emit(dateText, e.target.value); }} />
      </div>
      <div id={`${id}-echo`} className={bad ? 'err' : 'mute'} role={bad ? 'alert' : undefined} style={{ fontSize: '.9rem', marginTop: 4 }}>
        {bad ? t('Dátum zadajte ako 24. 3. 2026 a čas ako 14:30.', 'Enter the date as 24/03/2026 and the time as 14:30.') : spelled ?? ' '}
      </div>
    </div>
  );
}
