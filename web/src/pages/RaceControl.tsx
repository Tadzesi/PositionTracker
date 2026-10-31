import { useMemo, useState } from 'react';
import { get, post } from '../lib/api';
import { formatDuration, formatGap, formatKm, formatSpeed, formatTime, parseSpan } from '../lib/format';
import { useI18n } from '../lib/i18n';
import { useLoad, usePoll } from '../lib/hooks';
import type { LiveParticipant, LiveView, PublicCourse } from '../lib/types';
import MapView, { pointAt, type MapMarker } from '../components/MapView';
import { ErrorBanner, StatusBadge } from '../components/Ui';
import { useManage } from './EventLayout';

export const ALARM_ORDER = ['sos', 'offCourse', 'stopped', 'offline', 'lowBattery', 'lapsInferred'];

export function useStateLabel() {
  const { t } = useI18n();
  return (s: string) => ({
    notStarted: t('Čaká', 'Not started'), live: t('Naživo', 'Live'), delayed: t('Oneskorený', 'Delayed'), offline: t('Offline', 'Offline'),
    stopped: t('Stojí', 'Stopped'), offCourse: t('Mimo trasy', 'Off course'), sos: 'SOS', finished: t('V cieli', 'Finished'), dnf: 'DNF',
  } as Record<string, string>)[s] ?? s;
}

export function useAlarmLabel() {
  const { t } = useI18n();
  return (k: string) => ({
    sos: 'SOS', stopped: t('Stojí', 'Stopped'), offCourse: t('Mimo trasy', 'Off course'), offline: t('Bez signálu', 'No signal'),
    lowBattery: t('Slabá batéria', 'Low battery'), lapsInferred: t('Kolá odhadnuté – skontrolujte', 'Laps estimated – check'),
  } as Record<string, string>)[k] ?? k;
}

export default function RaceControl() {
  const { event } = useManage();
  const { t, lang } = useI18n();
  const stateLabel = useStateLabel();
  const alarmLabel = useAlarmLabel();
  const [selected, setSelected] = useState<string | null>(null);
  const [category, setCategory] = useState('');
  const [onlyAlarms, setOnlyAlarms] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const running = event.state === 'live' || event.state === 'finished';
  const live = usePoll(() => get<LiveView>(`/api/events/${event.eventId}/live`), 2000, [event.eventId, running]);
  const course = useLoad(() => get<PublicCourse>(`/api/events/${event.eventId}/course`).catch(() => null), [event.eventId, event.state]);

  const view = live.data;
  const rows = useMemo(() => (view?.participants ?? []).filter((p) =>
    (!category || p.category === category) && (!onlyAlarms || p.alarms.some((a) => !a.acknowledged))), [view, category, onlyAlarms]);
  const sel = view?.participants.find((p) => p.participantId === selected) ?? null;

  const markers: MapMarker[] = (view?.participants ?? []).filter((p) => p.lat !== null && p.lon !== null).map((p) => ({
    id: p.participantId, lat: p.lat!, lon: p.lon!, label: p.bib,
    kind: p.state === 'sos' ? 'sos' : p.state === 'offline' || p.state === 'dnf' ? 'dim' : 'normal',
  }));
  const cps = course.data?.checkpoints.map((c) => {
    const at = pointAt(course.data!.points, course.data!.lapLengthM, c.lapDistanceM);
    return at ? { name: c.name, lat: at[0], lon: at[1] } : null;
  }).filter((x): x is { name: string; lat: number; lon: number } => x !== null);

  const flags = (view?.participants ?? []).flatMap((p) => p.alarms.map((a) => ({ p, a })))
    .sort((x, y) => Number(x.a.acknowledged) - Number(y.a.acknowledged) || ALARM_ORDER.indexOf(x.a.kind) - ALARM_ORDER.indexOf(y.a.kind));

  const act = async (fn: () => Promise<void>) => { setError(null); try { await fn(); } catch (x) { setError(x); } };
  const ack = (p: LiveParticipant, kind: string) => act(() => post(`/api/events/${event.eventId}/alarms/ack`, { participantId: p.participantId, kind }));

  if (!running) return <div className="banner">{t('Riadenie pretekov je dostupné po spustení podujatia naživo.', 'Race control is available once the event is live.')}</div>;

  return (
    <div className="stack">
      <ErrorBanner error={error ?? live.error} />
      <div className="stats">
        <div className="stat"><b>{view?.countLive ?? 0}</b><span>{t('Vysiela', 'Broadcasting')}</span></div>
        <div className="stat"><b>{view?.countDelayed ?? 0}</b><span>{t('Oneskorení', 'Delayed')}</span></div>
        <div className="stat"><b>{view?.countOffline ?? 0}</b><span>Offline</span></div>
        <div className="stat"><b>{view?.countDnf ?? 0}</b><span>DNF</span></div>
        <div className="stat"><b>{view ? formatTime(view.at, lang) : '–'}</b><span>{t('Čas údajov', 'Data time')}</span></div>
      </div>
      <div className="race">
        <div className="stack">
          <MapView points={course.data?.points ?? []} markers={markers} checkpoints={cps ?? []} selectedId={selected} onSelect={setSelected} className="map tall" />
          <div className="card">
            <h3>{t('Hlásenia', 'Race control messages')}</h3>
            {flags.length === 0 && <p className="mute">{t('Bez alarmov.', 'No alarms.')}</p>}
            {flags.map(({ p, a }) => (
              <div key={p.participantId + a.kind} className={`flag ${a.kind} ${a.acknowledged ? 'ack' : ''}`}>
                <b>{alarmLabel(a.kind)}</b> <span>#{p.bib} {p.displayName}</span> <span className="mute">{formatTime(a.since, lang)}</span>
                <span className="spacer" />
                <button className="btn small ghost" onClick={() => setSelected(p.participantId)}><span>{t('Na mape', 'Show on map')}</span></button>
                {!a.acknowledged && <button className="btn small" onClick={() => ack(p, a.kind)}><span>{t('Potvrdiť', 'Acknowledge')}</span></button>}
              </div>))}
          </div>
        </div>
        <div className="stack">
          <div className="card">
            <h3>{t('Poradie', 'Timing tower')}</h3>
            <div className="row">
              <select aria-label={t('Kategória', 'Category')} value={category} onChange={(e) => setCategory(e.target.value)} style={{ maxWidth: 200 }}>
                <option value="">{t('Všetky kategórie', 'All categories')}</option>
                {[...new Set((view?.participants ?? []).map((p) => p.category).filter(Boolean))].map((c) => <option key={c}>{c}</option>)}
              </select>
              <button className="chip" aria-pressed={onlyAlarms} onClick={() => setOnlyAlarms(!onlyAlarms)}>{t('Len alarmy', 'Alarms only')}</button>
            </div>
            <div className="scroll"><table>
              <thead><tr><th>P</th><th>#</th><th>{t('Meno', 'Name')}</th><th>{t('Sektory', 'Sectors')}</th><th className="num">{t('Odstup', 'Gap')}</th><th /></tr></thead>
              <tbody>{rows.map((p) => (
                <tr key={p.participantId} className={`click ${p.participantId === selected ? 'sel' : ''}`} onClick={() => setSelected(p.participantId)}>
                  <td>{p.rank ?? '–'}</td><td>{p.bib}</td><td>{p.displayName}</td>
                  <td>{p.segments.slice(-6).map((s, i) => <span key={i} className={`sector ${s.colour}`} />)}</td>
                  <td className="num">{p.rank === 1 ? t('Líder', 'Leader') : formatGap(p.gapToLeader)}</td>
                  <td><span className={`dot ${p.state}`} title={stateLabel(p.state)} /></td>
                </tr>))}</tbody>
            </table></div>
          </div>
          {sel && <Telemetry p={sel} sport={event.sport} eventId={event.eventId} act={act} />}
        </div>
      </div>
    </div>
  );
}

function Telemetry({ p, sport, eventId, act }: { p: LiveParticipant; sport: 'running' | 'cycling'; eventId: string; act: (fn: () => Promise<void>) => Promise<void> }) {
  const { t, lang } = useI18n();
  const stateLabel = useStateLabel();
  const signal = parseSpan(p.lastSignalAgo);
  return (
    <div className="card">
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h3>#{p.bib} {p.displayName}</h3>
        <StatusBadge value={p.state} label={stateLabel(p.state)} />
      </div>
      <div className="stats">
        <div className="stat"><b>{formatSpeed(p.speedMps, sport, p.paceSecPerKm)}</b><span>{t('Rýchlosť', 'Speed')}</span></div>
        <div className="stat"><b>{formatKm(p.totalDistanceM)}</b><span>{t('Prejdené', 'Distance')}</span></div>
        <div className="stat"><b>{Math.round(p.ascentM)} m</b><span>{t('Stúpanie', 'Ascent')}</span></div>
        <div className="stat"><b>{p.gradePercent.toFixed(1)} %</b><span>{t('Sklon', 'Grade')}</span></div>
        <div className="stat"><b>{p.batteryPct === null ? '–' : `${Math.round(p.batteryPct)} %`}</b><span>{t('Batéria', 'Battery')}</span></div>
        <div className="stat"><b>{p.gpsAccuracyM === null ? '–' : `${Math.round(p.gpsAccuracyM)} m`}</b><span>GPS</span></div>
        <div className="stat"><b>{signal === null ? '–' : formatDuration(signal)}</b><span>{t('Posledný signál', 'Last signal')}</span></div>
        <div className="stat"><b>{p.eta ? formatTime(p.eta.expected, lang) : '–'}</b><span>ETA{p.eta?.approximate ? ' ≈' : ''}</span></div>
      </div>
      <p>{p.segments.map((s, i) => <span key={i} className={`sector ${s.colour}`} title={formatDuration(parseSpan(s.time))} />)}</p>
      <div className="row">
        <button className="btn small danger" disabled={p.state === 'dnf' || p.state === 'finished'}
          onClick={() => { if (window.confirm(t(`Označiť #${p.bib} ako DNF?`, `Mark #${p.bib} as DNF?`))) act(() => post(`/api/events/${eventId}/participants/${p.participantId}/dnf`)); }}><span>{t('Označiť DNF', 'Mark DNF')}</span></button>
      </div>
    </div>
  );
}
