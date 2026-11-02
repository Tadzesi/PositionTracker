import { useEffect, useMemo, useRef, useState } from 'react';
import { useParams, useSearchParams } from 'react-router-dom';
import { get } from '../lib/api';
import { formatDateTime, formatDuration, formatGap, formatKm, formatSpeed, formatTime, parseSpan } from '../lib/format';
import { useI18n } from '../lib/i18n';
import { useLoad, usePoll } from '../lib/hooks';
import type { PublicCourse, PublicEventInfo, PublicResults, SpectatorParticipant, SpectatorView } from '../lib/types';
import MapView, { pointAt, type MapMarker } from '../components/MapView';
import LiveProfile, { type LiveProfileRider } from '../components/LiveProfile';
import { lapDistance } from '../lib/profileCluster';
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
  // the course does not change while the event runs: it is fetched until it arrives (the event may not be live yet), then no more
  const course = usePoll(() => get<PublicCourse>(`/api/events/${id}/course`), 15000, [id], { stopWhen: () => true });
  // the public header: what the page is about before the go-live (the course answers 404 then) and whether the event has finished;
  // asked every minute until it has finished, then no more
  const info = usePoll(() => get<PublicEventInfo>(`/api/events/${id}/info`), 60000, [id], { stopWhen: (i) => i.state === 'finished' });
  const finished = info.data?.state === 'finished';
  const results = useLoad(() => (finished ? get<PublicResults>(`/api/events/${id}/public-results`).catch(() => null) : Promise.resolve(null)), [id, finished]);
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

  const profileRiders: LiveProfileRider[] = course.data ? participants
    .filter((p) => p.lat !== null && p.state !== 'dnf' && p.state !== 'notStarted')
    .map((p) => ({
      id: p.participantId, bib: p.bib, name: p.displayName, rank: p.rank, alarm: false, followed: followed.includes(p.participantId),
      distM: lapDistance(p.totalDistanceM, p.lap, course.data!.lapLengthM, p.state === 'finished'),
      gap: p.rank === 1 ? t('Líder', 'Leader') : p.gapToLeader ? formatGap(p.gapToLeader) : null,
    })) : [];

  if (course.error && !course.data) {
    if (info.data && info.data.state === 'ready') return <BeforeStart info={info.data} />;
    if (info.data && finished) return <AfterFinish info={info.data} results={results.data} />;
    if (!info.data && !info.error) return <Loading />;
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
      {finished ? <FinishedNote endedAt={info.data?.endedAt ?? null} /> : (
        <div className="banner" role="note">
          {t(`Polohy sú oneskorené o ${delay} s a poradie je orientačné. Oficiálne výsledky vydáva organizátor.`,
            `Positions are delayed by ${delay} s and the ranking is indicative. The organizer issues the official results.`)}
        </div>)}
      {finished && results.data && <ResultsCard results={results.data} />}
      <ErrorBanner error={view.error} />
      {alertFor && (
        <div className="banner ok" role="alert">
          🏁 {t(`${alertFor.displayName} je v cieli!`, `${alertFor.displayName} has finished!`)}{' '}
          <button className="btn small" onClick={() => setAlertFor(null)}><span>OK</span></button>
        </div>)}
      <div className="race">
        <div className="stack">
          <MapView points={course.data.points} markers={markers} checkpoints={cps ?? []} selectedId={focus} onSelect={setFocus} followIds={followed} className="map tall" />
          <div className="card">
            <h3>{t('Pretekári na profile trate', 'Riders on the course profile')}</h3>
            <LiveProfile profile={course.data.profile ?? null} lapLengthM={course.data.lapLengthM}
              checkpoints={course.data.checkpoints.map((c) => ({ name: c.name, distM: c.lapDistanceM }))}
              riders={profileRiders} selectedId={focus} onSelect={setFocus} />
          </div>
          {sel && <Followed p={sel} sport={sport} eventFinished={finished} followed={followed.includes(sel.participantId)} onToggle={() => toggle(sel.participantId)}
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

function BeforeStart({ info }: { info: PublicEventInfo }) {
  const { t, lang } = useI18n();
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => { const timer = setInterval(() => setNow(Date.now()), 60000); return () => clearInterval(timer); }, []);
  const minutes = Math.max(0, Math.floor((new Date(info.startTime).getTime() - now) / 60000));
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  const countdown = minutes === 0 ? null
    : days > 0 ? t(`Štart za ${days} d ${hours} h`, `Starts in ${days} d ${hours} h`)
    : hours > 0 ? t(`Štart za ${hours} h ${minutes % 60} min`, `Starts in ${hours} h ${minutes % 60} min`)
    : t(`Štart za ${minutes} min`, `Starts in ${minutes} min`);
  return (
    <div className="card narrow stack">
      <h1>{info.name}</h1>
      <p className="mute">{info.location} · {formatDateTime(info.startTime, lang)}</p>
      {countdown && <div className="big">{countdown}</div>}
      <p className="mute">{t('Mapa sa zobrazí automaticky po štarte.', 'The map appears automatically after the start.')}</p>
    </div>
  );
}

function FinishedNote({ endedAt }: { endedAt: string | null }) {
  const { t, lang } = useI18n();
  return (
    <div className="banner ok" role="status">
      <b>{t('Podujatie skončilo', 'The event has finished')}</b>{endedAt && <> · {formatDateTime(endedAt, lang)}</>}.{' '}
      {t('Mapa ukazuje posledné polohy. Oficiálne výsledky vydáva organizátor.', 'The map shows the last positions. The organizer issues the official results.')}
    </div>
  );
}

/** After the runtime has left the memory (some days after the finish): the header, the finish time and the results. */
function AfterFinish({ info, results }: { info: PublicEventInfo; results: PublicResults | null }) {
  const { lang } = useI18n();
  return (
    <div className="stack">
      <div className="card stack">
        <h1>{info.name}</h1>
        <p className="mute">{info.location} · {formatDateTime(info.startTime, lang)}</p>
      </div>
      <FinishedNote endedAt={info.endedAt} />
      {results ? <ResultsCard results={results} /> : <Loading />}
    </div>
  );
}

/** The public results: a name only where the participant agreed (the server decides), DNF after the finishers. */
function ResultsCard({ results }: { results: PublicResults }) {
  const { t } = useI18n();
  return (
    <div className="card stack">
      <h2>{t('Výsledky', 'Results')}</h2>
      <div className="stats">
        <div className="stat"><b>{results.finished}</b><span>{t('Dokončili', 'Finished')}</span></div>
        <div className="stat"><b>{results.dnf}</b><span>DNF</span></div>
      </div>
      {results.rows.length === 0 ? <p className="mute">{t('Bez výsledkov.', 'No results.')}</p> : (
        <div className="scroll"><table>
          <thead><tr><th>P</th><th>#</th><th>{t('Meno', 'Name')}</th><th>{t('Kategória', 'Category')}</th>
            <th className="num">{t('Čas', 'Time')}</th><th className="num">{t('Strata', 'Gap')}</th></tr></thead>
          <tbody>{results.rows.map((r) => (
            <tr key={r.bib}>
              <td>{r.rank ?? '–'}</td><td>{r.bib}</td><td>{r.name}</td><td>{r.category ?? ''}</td>
              <td className="num">{r.status === 'finished' ? formatDuration(parseSpan(r.netTime)) : `DNF · ${formatKm(r.distanceM)}`}</td>
              <td className="num">{r.rank === 1 ? t('Víťaz', 'Winner') : r.status === 'finished' ? formatGap(r.gapToLeader) : '–'}</td>
            </tr>))}</tbody>
        </table></div>)}
      <p className="dim">{t('Mená zobrazujeme len so súhlasom pretekára.', 'Names are shown only with the rider’s consent.')}</p>
    </div>
  );
}

function Followed({ p, sport, eventFinished, followed, onToggle, notify, onNotify }: {
  p: SpectatorParticipant; sport: 'running' | 'cycling'; eventFinished: boolean; followed: boolean; onToggle: () => void; notify: boolean; onNotify: () => void;
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
        <span className="mute">{p.state === 'finished' ? t('V cieli', 'Finished') : eventFinished ? t('Do cieľa nedorazil', 'Did not reach the finish') : t('Predpokladaný príchod do cieľa', 'Expected at the finish')}</span>
        <div className="big">{p.state === 'finished' ? '🏁' : eventFinished ? 'DNF' : p.eta ? formatTime(p.eta.expected, lang) : '–'}</div>
        {p.eta && p.state !== 'finished' && !eventFinished && <span className="mute">{formatTime(p.eta.earliest, lang)} – {formatTime(p.eta.latest, lang)}{p.eta.approximate ? ' ≈' : ''}</span>}
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
        {!eventFinished && <button className="btn small" aria-pressed={notify} onClick={onNotify}><span>{notify ? '🔔 ' : '🔕 '}{t('Upozorniť v cieli', 'Notify me at the finish')}</span></button>}
        <button className="btn small ghost" onClick={share}><span>{copied ? t('Odkaz skopírovaný', 'Link copied') : t('Zdieľať', 'Share')}</span></button>
      </div>
      {notify && <p className="mute">{t('Upozornenie sa zobrazí na tejto stránke, kým je otvorená.', 'The alert is shown on this page while it stays open.')}</p>}
    </div>
  );
}
