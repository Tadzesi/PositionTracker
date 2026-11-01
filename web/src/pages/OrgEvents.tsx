import { useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate } from 'react-router-dom';
import { get, post } from '../lib/api';
import { formatDateTime, localInputToUtc } from '../lib/format';
import { useI18n } from '../lib/i18n';
import { useLoad } from '../lib/hooks';
import type { EventSummary, Sport } from '../lib/types';
import { EmailModeNotice, ErrorBanner, Field, Loading, StatusBadge, bannerError } from '../components/Ui';
import { useSession } from '../components/Layout';

export function RequireSession({ children }: { children: React.ReactNode }) {
  const { me, ready } = useSession();
  const { t } = useI18n();
  if (!ready) return <p className="mute">{t('Načítavam…', 'Loading…')}</p>;
  if (!me) return <Navigate to="/account/signin" replace />;
  return <>{children}</>;
}

const stateLabel = (t: (a: string, b: string) => string, s: string) =>
  ({ draft: t('Návrh', 'Draft'), ready: t('Pripravené', 'Ready'), live: t('Naživo', 'Live'), finished: t('Skončené', 'Finished') })[s] ?? s;

/** Events the organizer is planning or running come first (soonest start first); finished ones follow, newest first. */
const EVENT_GROUPS = [
  { key: 'live', sk: 'Naživo', en: 'Live', states: ['live'], finished: false },
  { key: 'planned', sk: 'V príprave', en: 'Planned', states: ['draft', 'ready'], finished: false },
  { key: 'finished', sk: 'Ukončené', en: 'Finished', states: ['finished'], finished: true },
];

export default function OrgEvents() {
  const { t, lang } = useI18n();
  const { me } = useSession();
  const navigate = useNavigate();
  const [creating, setCreating] = useState(false);
  const list = useLoad(() => get<{ emailMode: string; events: EventSummary[] }>('/api/events'), []);
  const events = list.data?.events;
  const empty = events?.length === 0;
  return (
    <>
      <div className="row between">
        <h1>{t('Moje podujatia', 'My events')}</h1>
        {events && !empty && !creating && (
          <button type="button" className="btn primary" onClick={() => setCreating(true)}><span>+ {t('Nové podujatie', 'New event')}</span></button>
        )}
      </div>
      {me && !me.emailConfirmed && (
        <div className="banner">
          {t('E-mail ešte nie je potvrdený. Pokiaľ ho nepotvrdíte (odkaz v e-maile), nemožno nahrať trasu ani pozývať účastníkov.',
            'Your email is not confirmed yet. Until you confirm it (link in the email) you cannot upload a course or invite participants.')}
        </div>
      )}
      <EmailModeNotice mode={list.data?.emailMode} />
      <ErrorBanner error={list.error} />
      {list.loading && !events && <div className="card"><Loading /></div>}
      {empty && (
        <CreateEvent intro={t('Zatiaľ nemáte žiadne podujatie. Vytvorte prvé – trasu a účastníkov pridáte v ďalšom kroku.',
          'You have no events yet. Create your first – you add the course and participants in the next step.')} />
      )}
      {creating && <CreateEvent onCancel={() => setCreating(false)} />}
      <div className="stack">
        {EVENT_GROUPS.map((g) => {
          const rows = (events ?? []).filter((e) => g.states.includes(e.state))
            .sort((x, y) => g.finished ? y.startTime.localeCompare(x.startTime) : x.startTime.localeCompare(y.startTime));
          if (rows.length === 0) return null;
          return (
            <div className="card" key={g.key}>
              <h2>{t(g.sk, g.en)} <span className="mute">({rows.length})</span></h2>
              <div className="scroll"><table>
                <thead><tr><th>{t('Názov', 'Name')}</th><th>{t('Začiatok', 'Start')}</th><th>{t('Účastníci', 'Participants')}</th><th>{t('Stav', 'State')}</th></tr></thead>
                <tbody>{rows.map((e) => (
                  <tr key={e.eventId} className="click" onClick={() => navigate(`/org/events/${e.eventId}`)}>
                    <td>
                      <Link to={`/org/events/${e.eventId}`} onClick={(x) => x.stopPropagation()}>{e.name}</Link>
                      <div className="mute">{e.location}</div>
                      <span className="sub">{sportLabel(t, e.sport)} · {e.lapLengthM ? `${e.laps > 1 ? `${e.laps} × ` : ''}${(e.lapLengthM / 1000).toFixed(1)} km` : t('bez trasy', 'no course')}</span>
                    </td>
                    <td>{formatDateTime(e.startTime, lang)}</td>
                    <td><Participants e={e} /></td>
                    <td>
                      <div className="row">
                        <StatusBadge value={e.state} label={stateLabel(t, e.state)} />
                        {e.state === 'live' && (
                          <Link className="btn small primary" to={`/org/events/${e.eventId}/control`} onClick={(x) => x.stopPropagation()}><span>{t('Riadiť preteky', 'Race control')}</span></Link>
                        )}
                      </div>
                    </td>
                  </tr>))}</tbody>
              </table></div>
            </div>);
        })}
      </div>
    </>
  );
}

const sportLabel = (t: (a: string, b: string) => string, s: string) => (s === 'running' ? t('Beh', 'Running') : t('Cyklistika', 'Cycling'));

/** Confirmed out of everybody invited, with a bar and the number still waiting. */
function Participants({ e }: { e: EventSummary }) {
  const { t } = useI18n();
  const p = e.participants;
  const total = p.invited + p.confirmed + p.declined + p.emailFailed;
  if (total === 0) return <span className="mute">{t('Nikto nepozvaný', 'Nobody invited')}</span>;
  return (
    <div title={t(`${p.confirmed} potvrdilo, ${p.invited} čaká, ${p.declined} odmietlo, ${p.emailFailed} e-mail zlyhal`, `${p.confirmed} confirmed, ${p.invited} waiting, ${p.declined} declined, ${p.emailFailed} email failed`)}>
      <b>{p.confirmed}</b> / {total} {t('potvrdených', 'confirmed')}
      <div className="bar" aria-hidden="true"><span style={{ width: `${(p.confirmed / total) * 100}%` }} /></div>
      {p.invited > 0 && <span className="sub">{t(`${p.invited} čaká`, `${p.invited} waiting`)}</span>}
    </div>
  );
}

function CreateEvent({ intro, onCancel }: { intro?: string; onCancel?: () => void }) {
  const { t } = useI18n();
  const navigate = useNavigate();
  const [name, setName] = useState('');
  const [location, setLocation] = useState('');
  const [start, setStart] = useState('');
  const [sport, setSport] = useState<Sport>('cycling');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try {
      const created = await post<{ eventId: string }>('/api/events', { name, location, startTime: localInputToUtc(start) ?? start, sport });
      navigate(`/org/events/${created.eventId}`);
    } catch (x) { setError(x); setBusy(false); }
  };
  return (
    <form className="card stack formcard" onSubmit={submit}>
      <h2>{intro ? t('Vytvorte prvé podujatie', 'Create your first event') : t('Nové podujatie', 'New event')}</h2>
      {intro && <p className="mute">{intro}</p>}
      <ErrorBanner error={bannerError(error, ['name', 'location', 'startTime', 'sport'])} />
      <Field label={t('Názov', 'Name')} name="name" error={error}><input id="name" value={name} onChange={(e) => setName(e.target.value)} required /></Field>
      <Field label={t('Miesto', 'Location')} name="location" error={error}><input id="location" value={location} onChange={(e) => setLocation(e.target.value)} required /></Field>
      <Field label={t('Dátum a čas štartu (vaše miestne)', 'Start date and time (your local time)')} name="startTime" error={error}>
        <input id="startTime" type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} required />
      </Field>
      <Field label={t('Šport', 'Sport')} name="sport" error={error}>
        <select id="sport" value={sport} onChange={(e) => setSport(e.target.value as Sport)}>
          <option value="cycling">{t('Cyklistika', 'Cycling')}</option>
          <option value="running">{t('Beh', 'Running')}</option>
        </select>
      </Field>
      <div className="row">
        <button className="btn primary" disabled={busy}><span>{t('Vytvoriť podujatie', 'Create event')}</span></button>
        {onCancel && <button type="button" className="btn ghost" onClick={onCancel}><span>{t('Zrušiť', 'Cancel')}</span></button>}
      </div>
    </form>
  );
}
