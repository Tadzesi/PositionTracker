import { useMemo, useState } from 'react';
import { download, get } from '../lib/api';
import { formatDateTime, formatDuration, formatGap, formatKm, formatSpeed, parseSpan } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { useI18n } from '../lib/i18n';
import type { ResultLine, ResultsView } from '../lib/types';
import { ErrorBanner, Loading, StatusBadge } from '../components/Ui';
import { useManage } from './EventLayout';

const STATUSES = ['', 'finished', 'dnf', 'dns'] as const;

/**
 * The results of the event for its organizer: the standing while the event runs, the stored results once it has finished. The
 * organizer decides how to publish them: a CSV (names only where the participants agreed, unless unticked) and a print view.
 */
export default function Results() {
  const { event } = useManage();
  const { t, lang } = useI18n();
  const results = useLoad(() => get<ResultsView>(`/api/events/${event.eventId}/results`), [event.eventId, event.state]);
  const [category, setCategory] = useState('');
  const [gender, setGender] = useState('');
  const [status, setStatus] = useState('');
  const [query, setQuery] = useState('');
  const [consentOnly, setConsentOnly] = useState(true);
  const [error, setError] = useState<unknown>(null);

  const view = results.data;
  const rows = useMemo(() => (view?.rows ?? []).filter((r) =>
    (!category || r.category === category) && (!gender || r.gender === gender) && (!status || r.status === status)
    && (!query || `${r.name} ${r.nickname ?? ''} ${r.bib}`.toLowerCase().includes(query.toLowerCase()))), [view, category, gender, status, query]);
  const categories = [...new Set((view?.rows ?? []).map((r) => r.category).filter((c): c is string => !!c))];
  const withoutConsent = (view?.rows ?? []).filter((r) => r.status === 'finished' && (!r.nameConsent || r.isMinor)).length;

  const statusLabel = (s: string) => ({ '': t('Všetci', 'All'), finished: t('Dokončili', 'Finished'), dnf: 'DNF', dns: 'DNS' } as Record<string, string>)[s] ?? s;
  const exportCsv = async () => {
    setError(null);
    try {
      const blob = await download(`/api/events/${event.eventId}/results.csv?namesOnlyWithConsent=${consentOnly}`);
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = `vysledky-${event.name.replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '').toLowerCase() || 'podujatie'}.csv`;
      document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    } catch (x) { setError(x); }
  };

  if (results.error) return <ErrorBanner error={results.error} />;
  if (!view) return <Loading />;
  if (event.state === 'draft' || event.state === 'ready') {
    return <div className="banner" role="status">{t('Výsledky sa zobrazia, keď podujatie beží naživo.', 'Results appear once the event is live.')}</div>;
  }
  return (
    <div className="stack results">
      <ErrorBanner error={error} />
      <div className="card">
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <h2>{t('Výsledky', 'Results')} <StatusBadge value={view.provisional ? 'ready' : 'finished'} label={view.provisional ? t('Predbežné', 'Provisional') : t('Konečné', 'Final')} /></h2>
          <div className="row noprint">
            <button className="btn small" onClick={exportCsv}><span>{t('Stiahnuť CSV', 'Download CSV')}</span></button>
            <button className="btn small" onClick={() => window.print()}><span>{t('Tlačiť', 'Print')}</span></button>
          </div>
        </div>
        <p className="mute">
          {view.name} · {view.location} · {formatDateTime(view.startTime, lang)}
          {view.endedAt && ` · ${t('skončilo', 'ended')} ${formatDateTime(view.endedAt, lang)}`}
        </p>
        <div className="stats">
          <div className="stat"><b>{view.finished}</b><span>{t('Dokončili', 'Finished')}</span></div>
          <div className="stat"><b>{view.dnf}</b><span>DNF</span></div>
          <div className="stat"><b>{view.dns}</b><span>DNS</span></div>
        </div>
        {view.provisional && <div className="banner" role="note">{t('Podujatie ešte beží: poradie sa mení. Konečné výsledky sa uložia po skončení podujatia.', 'The event is still running: the standing changes. The final results are stored when the event finishes.')}</div>}
        <div className="banner noprint" role="note">
          {t('Výsledky vidíte len vy. Ako ich zverejníte (web, e-mail, tlač), je na vás. Mená účastníkov bez súhlasu so zverejnením a maloletých sa v exporte skrývajú.',
            'Only you can see the results. How you publish them (web, email, print) is up to you. Names of participants who did not agree to publication, and of minors, are hidden in the export.')}
          {withoutConsent > 0 && ` ${t(`Bez súhlasu: ${withoutConsent}.`, `Without consent: ${withoutConsent}.`)}`}
        </div>
        <div className="row noprint" role="group" aria-label={t('Filter', 'Filter')}>
          {STATUSES.map((s) => <button key={s} className="chip" aria-pressed={status === s} onClick={() => setStatus(s)}>{statusLabel(s)}</button>)}
          <select aria-label={t('Kategória', 'Category')} value={category} onChange={(e) => setCategory(e.target.value)} style={{ maxWidth: 200 }}>
            <option value="">{t('Všetky kategórie', 'All categories')}</option>
            {categories.map((c) => <option key={c}>{c}</option>)}
          </select>
          <select aria-label={t('Pohlavie', 'Gender')} value={gender} onChange={(e) => setGender(e.target.value)} style={{ maxWidth: 160 }}>
            <option value="">{t('Obe pohlavia', 'Both genders')}</option>
            <option value="male">{t('Muži', 'Men')}</option><option value="female">{t('Ženy', 'Women')}</option>
          </select>
          <input aria-label={t('Hľadať', 'Search')} placeholder={t('Meno alebo číslo…', 'Name or bib…')} value={query} onChange={(e) => setQuery(e.target.value)} style={{ maxWidth: 220 }} />
        </div>
        <label className="check noprint"><input type="checkbox" checked={consentOnly} onChange={(e) => setConsentOnly(e.target.checked)} />
          <span>{t('V CSV len mená so súhlasom (ostatní číslom alebo prezývkou)', 'In the CSV only names with consent (the others by number or nickname)')}</span></label>
        <div className="scroll"><table>
          <thead><tr>
            <th>#</th><th>{t('Kat.', 'Cat.')}</th><th>{t('Číslo', 'Bib')}</th><th>{t('Meno', 'Name')}</th><th>{t('Kategória', 'Category')}</th>
            <th className="num">{t('Čistý čas', 'Net time')}</th><th className="num">{t('Strata', 'Gap')}</th><th className="num">{t('Rýchlosť', 'Speed')}</th>
            <th className="num">{t('Stúpanie', 'Ascent')}</th><th>{t('Úseky', 'Sectors')}</th><th>{t('Stav', 'Status')}</th>
          </tr></thead>
          <tbody>{rows.map((r) => <Line key={r.participantId} r={r} sport={event.sport} />)}</tbody>
        </table></div>
        {rows.length === 0 && <p className="mute">{t('Nič nezodpovedá filtru.', 'Nothing matches the filter.')}</p>}
      </div>
    </div>
  );
}

function Line({ r, sport }: { r: ResultLine; sport: 'running' | 'cycling' }) {
  const { t } = useI18n();
  const net = parseSpan(r.netTime);
  const pace = r.avgSpeedMps > 0.1 ? 1000 / r.avgSpeedMps : null;
  return (
    <tr>
      <td>{r.rank ?? '–'}</td><td>{r.categoryRank ?? '–'}</td><td>{r.bib}</td>
      <td>{r.name}{r.nickname && <div className="mute">„{r.nickname}“</div>}{r.isMinor && <div className="mute">{t('maloletý', 'minor')}</div>}</td>
      <td>{r.category ?? '–'}</td>
      <td className="num">{net === null ? '–' : formatDuration(net)}</td>
      <td className="num">{r.rank === 1 ? t('Víťaz', 'Winner') : r.status === 'finished' ? formatGap(r.gapToLeader) : '–'}</td>
      <td className="num">{r.distanceM > 0 ? formatSpeed(r.avgSpeedMps, sport, pace) : '–'}</td>
      <td className="num">{r.distanceM > 0 ? `${Math.round(r.ascentM)} m` : '–'}</td>
      <td>{r.splits.map((s, i) => <span key={i} className={`sector ${s.colour}`} title={formatDuration(parseSpan(s.time))} />)}</td>
      <td>{r.status === 'finished' ? <StatusBadge value="finished" label={t('Dokončil', 'Finished')} />
        : <StatusBadge value="dnf" label={r.status === 'dns' ? 'DNS' : `DNF · ${formatKm(r.distanceM)}`} />}</td>
    </tr>
  );
}
