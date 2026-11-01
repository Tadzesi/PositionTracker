import { useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { get } from '../lib/api';
import { formatDateTime } from '../lib/format';
import { useLoad } from '../lib/hooks';
import { useI18n } from '../lib/i18n';
import type { AdminEvent, AdminOrganizer, AuditRow } from '../lib/types';
import { ErrorBanner, Loading, StatusBadge } from '../components/Ui';
import { useSession } from '../components/Layout';

type Tab = 'events' | 'organizers' | 'audit';

/** Hours and minutes of working time, e.g. "2 h 15 min"; under a minute "< 1 min"; nothing measured "–". */
export function formatActive(seconds: number): string {
  if (seconds <= 0) return '–';
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return '< 1 min';
  const h = Math.floor(minutes / 60);
  return h > 0 ? `${h} h ${String(minutes % 60).padStart(2, '0')} min` : `${minutes} min`;
}

/** Administrator overview: every event of every organizer, the organizers with the time they spent here, and the audit trail. */
export default function Admin() {
  const { me } = useSession();
  const { t } = useI18n();
  const [tab, setTab] = useState<Tab>('events');
  if (!me?.isAdmin) return <p className="note" role="alert">{t('Táto stránka je len pre správcu.', 'This page is for the administrator only.')}</p>;
  return (
    <>
      <h1>{t('Správa aplikácie', 'Administration')}</h1>
      <nav className="tabs" aria-label="admin">
        {([['events', t('Podujatia', 'Events')], ['organizers', t('Organizátori', 'Organizers')], ['audit', t('Záznam zmien', 'Audit trail')]] as [Tab, string][]).map(([key, label]) => (
          <button key={key} type="button" className={tab === key ? 'active' : ''} aria-current={tab === key ? 'page' : undefined} onClick={() => setTab(key)}>{label}</button>
        ))}
      </nav>
      {tab === 'events' && <EventsTab />}
      {tab === 'organizers' && <OrganizersTab />}
      {tab === 'audit' && <AuditTab />}
    </>
  );
}

const STATES = ['draft', 'ready', 'live', 'finished'];

function EventsTab() {
  const { t, lang } = useI18n();
  const events = useLoad(() => get<AdminEvent[]>('/api/admin/events'), []);
  const [query, setQuery] = useState('');
  const [state, setState] = useState('');
  const stateLabel = (s: string) => ({ draft: t('Návrh', 'Draft'), ready: t('Pripravené', 'Ready'), live: t('Naživo', 'Live'), finished: t('Skončené', 'Finished') })[s] ?? s;
  const rows = useMemo(() => {
    const q = query.trim().toLowerCase();
    return (events.data ?? []).filter((e) => (!state || e.state === state)
      && (!q || `${e.name} ${e.location} ${e.organizerName} ${e.organizerEmail}`.toLowerCase().includes(q)));
  }, [events.data, query, state]);
  if (events.error) return <ErrorBanner error={events.error} />;
  if (!events.data) return <Loading />;
  return (
    <div className="card">
      <div className="row">
        <input aria-label={t('Hľadať', 'Search')} placeholder={t('Hľadať podujatie alebo organizátora', 'Search event or organizer')} value={query} onChange={(e) => setQuery(e.target.value)} style={{ flex: 2, minWidth: 200 }} />
        <select aria-label={t('Stav', 'State')} value={state} onChange={(e) => setState(e.target.value)} style={{ flex: 1, minWidth: 140 }}>
          <option value="">{t('Všetky stavy', 'All states')}</option>
          {STATES.map((s) => <option key={s} value={s}>{stateLabel(s)}</option>)}
        </select>
      </div>
      <p className="mute">{t(`${rows.length} z ${events.data.length} podujatí`, `${rows.length} of ${events.data.length} events`)}</p>
      <div className="scroll"><table>
        <thead><tr><th>{t('Podujatie', 'Event')}</th><th>{t('Organizátor', 'Organizer')}</th><th>{t('Začiatok', 'Start')}</th><th>{t('Účastníci', 'Participants')}</th><th>{t('Stav', 'State')}</th></tr></thead>
        <tbody>{rows.map((e) => (
          <tr key={e.eventId}>
            <td>
              <Link to={`/admin/organizers/${e.organizerId}/events/${e.eventId}`}>{e.name}</Link>
              <div className="mute">{e.location}</div>
            </td>
            <td>{e.organizerName}<div className="mute">{e.organizerEmail}</div></td>
            <td>{formatDateTime(e.startTime, lang)}</td>
            <td><b>{e.participants.confirmed}</b> / {e.participants.invited + e.participants.confirmed + e.participants.declined + e.participants.emailFailed}</td>
            <td><StatusBadge value={e.state} label={stateLabel(e.state)} /></td>
          </tr>))}</tbody>
      </table></div>
      {rows.length === 0 && <p className="mute">{t('Nič nezodpovedá filtru.', 'Nothing matches the filter.')}</p>}
    </div>
  );
}

function OrganizersTab() {
  const { t, lang } = useI18n();
  const organizers = useLoad(() => get<AdminOrganizer[]>('/api/admin/organizers'), []);
  if (organizers.error) return <ErrorBanner error={organizers.error} />;
  if (!organizers.data) return <Loading />;
  return (
    <div className="card">
      <p className="help">{t('Čas strávený na stránke sa meria len u prihlásených organizátorov: kým majú stránku otvorenú, viditeľnú a niečo na nej robia (približne, po 30 sekundách). Diváci a účastníci sa nemerajú.',
        'Time on the site is measured only for signed-in organizers: while they have the page open, visible and are using it (approximately, in 30-second steps). Spectators and participants are not measured.')}</p>
      <div className="scroll"><table>
        <thead><tr><th>{t('Organizátor', 'Organizer')}</th><th>{t('Podujatia', 'Events')}</th><th>{t('Čas na stránke', 'Time on site')}</th><th>{t('Posledných 30 dní', 'Last 30 days')}</th><th>{t('Naposledy', 'Last seen')}</th></tr></thead>
        <tbody>{organizers.data.map((o) => (
          <tr key={o.organizerId}>
            <td>{o.name}<div className="mute">{o.email}{o.emailConfirmed ? '' : ` · ${t('nepotvrdený', 'unconfirmed')}`}</div></td>
            <td>{o.events}</td>
            <td><b>{formatActive(o.activeSeconds)}</b></td>
            <td>{formatActive(o.activeSecondsLast30Days)}</td>
            <td>{o.lastSeenAt ? formatDateTime(o.lastSeenAt, lang) : '–'}</td>
          </tr>))}</tbody>
      </table></div>
    </div>
  );
}

function AuditTab() {
  const { t, lang } = useI18n();
  const audit = useLoad(() => get<AuditRow[]>('/api/admin/audit'), []);
  if (audit.error) return <ErrorBanner error={audit.error} />;
  if (!audit.data) return <Loading />;
  return (
    <div className="card">
      <p className="help">{t('Každá zmena, ktorú správca urobil za organizátora (posledných 200).', 'Every change an administrator made on behalf of an organizer (the last 200).')}</p>
      {audit.data.length === 0 && <p className="mute">{t('Zatiaľ žiadne zásahy.', 'No interventions yet.')}</p>}
      <div className="scroll"><table>
        <thead><tr><th>{t('Kedy', 'When')}</th><th>{t('Správca', 'Administrator')}</th><th>{t('Za organizátora', 'For organizer')}</th><th>{t('Zmena', 'Change')}</th></tr></thead>
        <tbody>{audit.data.map((r, i) => (
          <tr key={i}><td>{formatDateTime(r.at, lang)}</td><td>{r.adminEmail}</td><td>{r.organizerEmail}</td><td><code>{r.method} {r.path}</code></td></tr>))}</tbody>
      </table></div>
    </div>
  );
}
