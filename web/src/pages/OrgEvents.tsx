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

export default function OrgEvents() {
  const { t, lang } = useI18n();
  const { me } = useSession();
  const list = useLoad(() => get<{ emailMode: string; events: EventSummary[] }>('/api/events'), []);
  return (
    <>
      <h1>{t('Moje podujatia', 'My events')}</h1>
      {me && !me.emailConfirmed && (
        <div className="banner">
          {t('E-mail ešte nie je potvrdený. Pokiaľ ho nepotvrdíte (odkaz v e-maile), nemožno nahrať trasu ani pozývať účastníkov.',
            'Your email is not confirmed yet. Until you confirm it (link in the email) you cannot upload a course or invite participants.')}
        </div>
      )}
      <EmailModeNotice mode={list.data?.emailMode} />
      <ErrorBanner error={list.error} />
      <div className="grid two">
        <div className="card">
          <h2>{t('Podujatia', 'Events')}</h2>
          {list.loading && !list.data ? <Loading /> : list.data?.events.length === 0
            ? <p className="mute">{t('Zatiaľ nemáte žiadne podujatie.', 'You have no events yet.')}</p>
            : <div className="scroll"><table>
                <thead><tr><th>{t('Názov', 'Name')}</th><th>{t('Začiatok', 'Start')}</th><th>{t('Stav', 'State')}</th></tr></thead>
                <tbody>{list.data?.events.map((e) => (
                  <tr key={e.eventId}>
                    <td><Link to={`/org/events/${e.eventId}`}>{e.name}</Link><div className="mute">{e.location}</div></td>
                    <td>{formatDateTime(e.startTime, lang)}</td>
                    <td><StatusBadge value={e.state} label={stateLabel(t, e.state)} /></td>
                  </tr>))}</tbody>
              </table></div>}
        </div>
        <CreateEvent />
      </div>
    </>
  );
}

function CreateEvent() {
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
    <form className="card stack" onSubmit={submit}>
      <h2>{t('Nové podujatie', 'New event')}</h2>
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
      <button className="btn primary" disabled={busy}><span>{t('Vytvoriť podujatie', 'Create event')}</span></button>
    </form>
  );
}
