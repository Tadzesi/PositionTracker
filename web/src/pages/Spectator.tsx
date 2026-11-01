import { useEffect, useMemo, useRef, useState } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { get } from '../lib/api';
import { formatDuration, formatGap, formatKm, formatSpeed, formatTime, parseSpan } from '../lib/format';
import { useI18n } from '../lib/i18n';
import { usePoll } from '../lib/hooks';
import type { PublicCourse, SpectatorParticipant, SpectatorView } from '../lib/types';
import MapView, { pointAt, type MapMarker } from '../components/MapView';
import { ErrorBanner, Loading, StatusBadge, useStateLabel } from '../components/Ui';

const loadFollowed = (eventId: string): string[] => {
  try { return JSON.parse(localStorage.getItem(`pt-follow-${eventId}`) ?? '[]'); } catch { return []; }
};
const saveFollowed = (eventId: string, ids: string[]) => {
  try { localStorage.setItem(`pt-follow-${eventId}`, JSON.stringify(ids)); } catch { /* storage blocked */ }
};

export default function Spectator() {
  const { id = '' } = useParams();
  const [params] = useSearchParams();
  const { t, lang } = useI18n();
  const stateLabel = useStateLabel();
  const course = usePoll(() => get<PublicCourse>(`/api/events/${id}/course`), 15000, [id]);
  const view = usePoll(() => get<SpectatorView>(`/api/events/${id}/public`), 3000, [id]);
  const [query, setQuery] = useState('');
  const [category, setCategory] = useState('');
  const [followed, setFollowed] = useState<string[]>(() => {
    const preset = params.get('follow');
    const stored = loadFollowed(id);
    return preset && !stored.includes(preset) ? [...stored, preset] : stored;
  });
  const [focus, setFocus] = useState<string | null>(followed[0] ?? null);
  const [alertFor, setAlertFor] = useState<SpectatorParticipant | null>(null);
  const notified = useRef<Set<string>>(new Set());
  const [notify, setNotify] = useState<Set<string>>(new Set());

  const participants = view.data?.participants ?? [];
  const toggle = (pid: string) => {
    const next = followed.includes(pid) ? followed.filter((x) => x !== pid) : [...followed, pid];
    setFollowed(next); saveFollowed(id, next);
    if (!followed.includes(pid)) setFocus(pid);
  };

  // In-page alert when a rider the visitor asked about reaches the finish.
  useEffect(() => {
    for (const p of participants) {
      if (p.state === 'finished' && notify.has(p.participantId) && !notified.current.has(p.participantId)) {
        notified.current.add(p.participantId);
        setAlertFor(p);
        try { if ('Notification' in window && Notification.permission === 'granted') new Notification(t('V cieli!', 'Finished!'), { body: p.displayName }); } catch { /* not allowed */ }
      }
    }
  }, [participants, notify, t]);

  const shown = useMemo(() => participants.filter((p) =>
    (!category || p.category === category)
    && (!query || p.displayName.toLowerCase().includes(query.toLowerCase()) || p.bib.toLowerCase() === query.toLowerCase().replace('#', ''))),
  [participants, category, query]);
  const sel = participants.find((p) => p.participantId === focus) ?? null;

  const markers: MapMarker[] = participants.filter((p) => p.lat !== null && p.lon !== null).map((p) => ({
    id: p.participantId, lat: p.lat!, lon: p.lon!, label: p.bib,
    kind: followed.includes(p.participantId) || p.participantId === focus ? 'normal' : 'dim',
    selected: p.participantId === focus,
  }));
  const cps = course.data?.checkpoints.map((c) => {
    const at = pointAt(course.data!.points, course.data!.lapLengthM, c.lapDistanceM);
    return at ? { name: c.name, lat: at[0], lon: at[1] } : null;
  }).filter((x): x is { name: string; lat: number; lon: number } => x !== null);

  if (course.error && !course.data) {
    return (
      <div className="card narrow stack">
        <h2>{t('Podujatie ešte nebeží', 'The event is not running yet')}</h2>
        <p className="mute">{t('Sledovanie sa sprístupní, keď organizátor spustí podujatie naživo. Skúste to neskôr.', 'Tracking opens when the organizer takes the event live. Please try again later.')}</p>
      </div>
    );
  }
  if (!course.data) return <Loading />;
  const sport = course.data.event.sport;
  const delay = course.data.event.spectatorDelaySeconds;

  return (
    <div className="stack">
      <h1>{course.data.event.name}</h1>
      <div className="banner" role="note">
        {t(`Polohy sú oneskorené o ${delay} s a poradie je orientačné. Oficiálne výsledky vydáva organizátor.`,
          `Positions are delayed by ${delay} s and the ranking is indicative. The organizer issues the official results.`)}
      </div>
      <ErrorBanner error={view.error} />
      {alertFor && (
        <div className="banner ok" role="alert">
          🏁 {t(`${alertFor.displayName} je v cieli!`, `${alertFor.displayName} has finished!`)}{' '}
          <button className="btn small" onClick={() => setAlertFor(null)}><span>OK</span></button>
        </div>)}
      <div className="race">
        <div className="stack">
          <MapView points={course.data.points} markers={markers} checkpoints={cps ?? []} selectedId={focus} onSelect={setFocus} className="map tall" />
          {sel && <Followed p={sel} sport={sport} followed={followed.includes(sel.participantId)} onToggle={() => toggle(sel.participantId)}
            notify={notify.has(sel.participantId)} onNotify={async () => {
              const next = new Set(notify);
              if (next.has(sel.participantId)) next.delete(sel.participantId); else {
                next.add(sel.participantId);
                try { if ('Notification' in window && Notification.permission === 'default') await Notification.requestPermission(); } catch { /* optional */ }
              }
              setNotify(next);
            }} />}
        </div>
        <div className="card">
          <h3>{t('Pretekári', 'Riders')}</h3>
          <div className="row">
            <input aria-label={t('Hľadať', 'Search')} placeholder={t('Meno alebo číslo…', 'Name or bib…')} value={query} onChange={(e) => setQuery(e.target.value)} style={{ maxWidth: 220 }} />
            <select aria-label={t('Kategória', 'Category')} value={category} onChange={(e) => setCategory(e.target.value)} style={{ maxWidth: 200 }}>
              <option value="">{t('Všetky kategórie', 'All categories')}</option>
              {[...new Set(participants.map((p) => p.category).filter(Boolean))].map((c) => <option key={c}>{c}</option>)}
            </select>
          </div>
          {!view.data && <Loading />}
          <div className="scroll"><table>
            <thead><tr><th>P</th><th>#</th><th>{t('Meno', 'Name')}</th><th className="num">{t('Odstup', 'Gap')}</th><th /></tr></thead>
            <tbody>{shown.map((p) => (
              <tr key={p.participantId} className={`click ${p.participantId === focus ? 'sel' : ''}`} onClick={() => setFocus(p.participantId)}>
                <td>{p.rank ?? '–'}</td><td>{p.bib}</td>
                <td>{p.displayName} <span className={`dot ${p.state}`} title={stateLabel(p.state)} /></td>
                <td className="num">{p.rank === 1 ? t('Líder', 'Leader') : formatGap(p.gapToLeader)}</td>
                <td><button className="chip" aria-pressed={followed.includes(p.participantId)} aria-label={t('Sledovať', 'Follow')}
                  onClick={(e) => { e.stopPropagation(); toggle(p.participantId); }}>{followed.includes(p.participantId) ? '★' : '☆'}</button></td>
              </tr>))}</tbody>
          </table></div>
          {view.data && <p className="dim">{t('Údaje z', 'Data from')} {formatTime(view.data.at, lang)}</p>}
        </div>
      </div>
    </div>
  );
}

function Followed({ p, sport, followed, onToggle, notify, onNotify }: {
  p: SpectatorParticipant; sport: 'running' | 'cycling'; followed: boolean; onToggle: () => void; notify: boolean; onNotify: () => void;
}) {
  const { t, lang } = useI18n();
  const stateLabel = useStateLabel();
  const [copied, setCopied] = useState(false);
  const link = `${window.location.origin}${window.location.pathname}?follow=${p.participantId}`;
  const share = async () => {
    try {
      if (navigator.share) await navigator.share({ title: p.displayName, url: link });
      else { await navigator.clipboard.writeText(link); setCopied(true); }
    } catch { /* cancelled */ }
  };
  return (
    <div className="card stack">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3>#{p.bib} {p.displayName}</h3>
        <StatusBadge value={p.state} label={stateLabel(p.state)} />
      </div>
      <div>
        <span className="mute">{p.state === 'finished' ? t('V cieli', 'Finished') : t('Predpokladaný príchod do cieľa', 'Expected at the finish')}</span>
        <div className="big">{p.state === 'finished' ? '🏁' : p.eta ? formatTime(p.eta.expected, lang) : '–'}</div>
        {p.eta && p.state !== 'finished' && <span className="mute">{formatTime(p.eta.earliest, lang)} – {formatTime(p.eta.latest, lang)}{p.eta.approximate ? ' ≈' : ''}</span>}
      </div>
      <div className="stats">
        <div className="stat"><b>{formatKm(p.totalDistanceM)}</b><span>{t('Prejdené', 'Distance')}</span></div>
        <div className="stat"><b>{formatSpeed(p.speedMps, sport, p.paceSecPerKm)}</b><span>{t('Rýchlosť', 'Speed')}</span></div>
        <div className="stat"><b>{p.rank ?? '–'}</b><span>{t('Poradie', 'Rank')}</span></div>
        <div className="stat"><b>{formatGap(p.gapToAhead)}</b><span>{t('Za vedúcim', 'Behind ahead')}</span></div>
      </div>
      {p.segments.length > 0 && (
        <div>
          <span className="mute">{t('Úseky', 'Sectors')}</span>
          <div>{p.segments.map((s, i) => <span key={i} className={`sector ${s.colour}`} title={formatDuration(parseSpan(s.time))} />)}</div>
        </div>)}
      <div className="row">
        <button className="btn small" aria-pressed={followed} onClick={onToggle}><span>{followed ? '★ ' : '☆ '}{followed ? t('Sledujem', 'Following') : t('Sledovať', 'Follow')}</span></button>
        <button className="btn small" aria-pressed={notify} onClick={onNotify}><span>{notify ? '🔔 ' : '🔕 '}{t('Upozorniť v cieli', 'Notify me at the finish')}</span></button>
        <button className="btn small ghost" onClick={share}><span>{copied ? t('Odkaz skopírovaný', 'Link copied') : t('Zdieľať', 'Share')}</span></button>
      </div>
      {notify && <p className="mute">{t('Upozornenie sa zobrazí na tejto stránke, kým je otvorená.', 'The alert is shown on this page while it stays open.')}</p>}
    </div>
  );
}
