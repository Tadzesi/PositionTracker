import { createContext, useContext, useState } from 'react';
import { NavLink, Outlet, useParams } from 'react-router-dom';
import { BASE, get, post, put } from '../lib/api';
import { formatDateTime, localInputToUtc, utcToLocalInput } from '../lib/format';
import { useI18n } from '../lib/i18n';
import { useLoad, type Loaded } from '../lib/hooks';
import type { CategoryView, Checkpoint, ManageView, WaveView } from '../lib/types';
import { EmailModeNotice, ErrorBanner, Field, Loading, StatusBadge } from '../components/Ui';
import { useSession } from '../components/Layout';

const ManageCtx = createContext<{ event: ManageView; reload: () => void; setEvent: (e: ManageView) => void } | null>(null);
export const useManage = () => {
  const v = useContext(ManageCtx);
  if (!v) throw new Error('event missing');
  return v;
};

export default function EventLayout() {
  const { id } = useParams();
  const { t } = useI18n();
  const manage: Loaded<ManageView> = useLoad(() => get<ManageView>(`/api/events/${id}/manage`), [id]);
  if (manage.error) return <ErrorBanner error={manage.error} />;
  if (!manage.data) return <Loading />;
  const e = manage.data;
  return (
    <ManageCtx.Provider value={{ event: e, reload: manage.reload, setEvent: manage.setData }}>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h1>{e.name}</h1>
        <StatusBadge value={e.state} />
      </div>
      <EmailModeNotice mode={e.emailMode} />
      <nav className="tabs" aria-label="event">
        <NavLink to={`/org/events/${e.eventId}`} end>{t('Nastavenie', 'Setup')}</NavLink>
        <NavLink to={`/org/events/${e.eventId}/participants`}>{t('Účastníci', 'Participants')}</NavLink>
        <NavLink to={`/org/events/${e.eventId}/control`}>{t('Riadenie pretekov', 'Race control')}</NavLink>
      </nav>
      <Outlet />
    </ManageCtx.Provider>
  );
}

const toBase64 = (buffer: ArrayBuffer) => {
  let s = '';
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
};

export function Setup() {
  const { event, setEvent } = useManage();
  const { t, lang } = useI18n();
  const { me } = useSession();
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const run = async (fn: () => Promise<ManageView>) => {
    setBusy(true); setError(null);
    try { setEvent(await fn()); } catch (x) { setError(x); }
    setBusy(false);
  };
  const confirmed = me?.emailConfirmed ?? false;
  const locked = event.state === 'live' || event.state === 'finished';
  const next = ({ draft: 'ready', ready: 'live', live: 'finished' } as Record<string, string>)[event.state];
  const nextLabel = ({ ready: t('Označiť ako pripravené', 'Mark as ready'), live: t('Spustiť naživo', 'Go live'), finished: t('Ukončiť podujatie', 'Finish event') } as Record<string, string>)[next ?? ''];

  return (
    <div className="stack">
      <ErrorBanner error={error} />
      {!confirmed && <div className="banner">{t('Potvrďte e-mail, aby ste mohli nahrať trasu a meniť podujatie.', 'Confirm your email to upload a course and change the event.')}</div>}
      <div className="card">
        <h2>{t('Podujatie', 'Event')}</h2>
        <div className="stats">
          <div className="stat"><b>{formatDateTime(event.startTime, lang)}</b><span>{t('Štart', 'Start')}</span></div>
          <div className="stat"><b>{event.location}</b><span>{t('Miesto', 'Location')}</span></div>
          <div className="stat"><b>{event.participants.confirmed}/{event.participants.invited + event.participants.confirmed + event.participants.declined + event.participants.emailFailed}</b><span>{t('Potvrdení', 'Confirmed')}</span></div>
        </div>
        {next && (
          <div className="row" style={{ marginTop: 14 }}>
            <button className={`btn ${next === 'live' ? 'green' : 'primary'}`} disabled={busy || !confirmed}
              onClick={() => {
                if (next === 'live' && !window.confirm(t('Spustiť podujatie naživo? Po spustení už nemožno meniť trasu.', 'Go live? The course cannot be changed afterwards.'))) return;
                if (next === 'finished' && !window.confirm(t('Ukončiť podujatie? Polohy sa už nebudú prijímať.', 'Finish the event? Positions are no longer accepted.'))) return;
                run(() => post<ManageView>(`/api/events/${event.eventId}/state`, { state: next }));
              }}><span>{nextLabel}</span></button>
            <span className="mute">{event.state === 'draft' ? t('Najprv nahrajte trasu.', 'Upload the course first.') : ''}</span>
          </div>
        )}
        {event.state === 'live' && <p className="mute">{t('Verejný odkaz pre divákov:', 'Public link for spectators:')} <a href={`${BASE}/e/${event.eventId}`}>{`${location.origin}${BASE}/e/${event.eventId}`}</a></p>}
      </div>
      <CourseForm disabled={!confirmed || locked} run={run} />
      <Categories disabled={!confirmed || locked} run={run} />
      <Waves disabled={!confirmed || locked} run={run} />
      <Deadline disabled={!confirmed || event.state === 'finished'} run={run} />
    </div>
  );
}

type Run = (fn: () => Promise<ManageView>) => Promise<void>;

function CourseForm({ disabled, run }: { disabled: boolean; run: Run }) {
  const { event } = useManage();
  const { t } = useI18n();
  const [file, setFile] = useState<File | null>(null);
  const [laps, setLaps] = useState(event.laps);
  const [delay, setDelay] = useState(event.spectatorDelaySeconds);
  const [cps, setCps] = useState<Checkpoint[]>(event.course?.checkpoints ?? []);
  const c = event.course;
  return (
    <form className="card stack" onSubmit={async (e) => {
      e.preventDefault();
      if (!file) return;
      const gpxBase64 = toBase64(await file.arrayBuffer());
      await run(() => put<ManageView>(`/api/events/${event.eventId}/course`,
        { gpxBase64, laps, spectatorDelaySeconds: delay, checkpoints: cps.filter((x) => x.name.trim()) }));
    }}>
      <h2>{t('Trasa', 'Course')}</h2>
      {c && <p className="mute">{t(`Nahraná trasa: ${(c.lapLengthM / 1000).toFixed(2)} km na kolo, stúpanie ${Math.round(c.ascentM)} m, ${c.closed ? 'uzavretá' : 'otvorená'}.`,
        `Uploaded course: ${(c.lapLengthM / 1000).toFixed(2)} km per lap, ascent ${Math.round(c.ascentM)} m, ${c.closed ? 'closed' : 'open'}.`)}</p>}
      <Field label={t('Súbor GPX', 'GPX file')} name="gpxBase64"><input id="gpxBase64" type="file" accept=".gpx,application/gpx+xml,text/xml" disabled={disabled} onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></Field>
      <div className="grid two">
        <Field label={t('Počet kôl (1–10)', 'Laps (1–10)')} name="laps"><input id="laps" type="number" min={1} max={10} value={laps} disabled={disabled} onChange={(e) => setLaps(Number(e.target.value))} /></Field>
        <Field label={t('Oneskorenie pre divákov (5–300 s)', 'Spectator delay (5–300 s)')} name="spectatorDelaySeconds"><input id="spectatorDelaySeconds" type="number" min={5} max={300} value={delay} disabled={disabled} onChange={(e) => setDelay(Number(e.target.value))} /></Field>
      </div>
      <h3>{t('Kontrolné body', 'Checkpoints')}</h3>
      {cps.map((cp, i) => (
        <div className="row" key={i}>
          <input aria-label={t('Názov', 'Name')} placeholder={t('Názov', 'Name')} value={cp.name} disabled={disabled} style={{ flex: 2 }}
            onChange={(e) => setCps(cps.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
          <input aria-label={t('Vzdialenosť v kole (m)', 'Distance in lap (m)')} type="number" min={1} value={cp.lapDistanceM} disabled={disabled} style={{ flex: 1 }}
            onChange={(e) => setCps(cps.map((x, j) => (j === i ? { ...x, lapDistanceM: Number(e.target.value) } : x)))} />
          <button type="button" className="btn small danger" disabled={disabled} onClick={() => setCps(cps.filter((_, j) => j !== i))}><span>×</span></button>
        </div>))}
      <div className="row">
        <button type="button" className="btn small" disabled={disabled} onClick={() => setCps([...cps, { name: '', lapDistanceM: 1000 }])}><span>{t('Pridať kontrolný bod', 'Add checkpoint')}</span></button>
        <button className="btn primary" disabled={disabled || !file}><span>{c ? t('Nahradiť trasu', 'Replace course') : t('Nahrať trasu', 'Upload course')}</span></button>
      </div>
      <p className="mute">{t('Pri zmene nahrajte GPX znova.', 'Upload the GPX again when you change anything here.')}</p>
    </form>
  );
}

function Categories({ disabled, run }: { disabled: boolean; run: Run }) {
  const { event } = useManage();
  const { t } = useI18n();
  const [rows, setRows] = useState<Pick<CategoryView, 'name' | 'gender' | 'minAge' | 'maxAge'>[]>(event.categories);
  const num = (v: string) => (v === '' ? null : Number(v));
  return (
    <form className="card stack" onSubmit={(e) => { e.preventDefault(); run(() => put<ManageView>(`/api/events/${event.eventId}/categories`, rows)); }}>
      <h2>{t('Kategórie', 'Categories')}</h2>
      {rows.map((r, i) => (
        <div className="row" key={i}>
          <input aria-label={t('Názov', 'Name')} placeholder={t('Názov', 'Name')} value={r.name} disabled={disabled} style={{ flex: 2 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
          <select aria-label={t('Pohlavie', 'Gender')} value={r.gender ?? ''} disabled={disabled} style={{ flex: 1 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, gender: e.target.value || null } : x)))}>
            <option value="">{t('Všetci', 'Any')}</option><option value="female">{t('Ženy', 'Female')}</option><option value="male">{t('Muži', 'Male')}</option>
          </select>
          <input aria-label={t('Vek od', 'Age from')} type="number" placeholder={t('od', 'from')} value={r.minAge ?? ''} disabled={disabled} style={{ width: 80 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, minAge: num(e.target.value) } : x)))} />
          <input aria-label={t('Vek do', 'Age to')} type="number" placeholder={t('do', 'to')} value={r.maxAge ?? ''} disabled={disabled} style={{ width: 80 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, maxAge: num(e.target.value) } : x)))} />
          <button type="button" className="btn small danger" disabled={disabled} onClick={() => setRows(rows.filter((_, j) => j !== i))}><span>×</span></button>
        </div>))}
      <div className="row">
        <button type="button" className="btn small" disabled={disabled} onClick={() => setRows([...rows, { name: '', gender: null, minAge: null, maxAge: null }])}><span>{t('Pridať kategóriu', 'Add category')}</span></button>
        <button className="btn" disabled={disabled}><span>{t('Uložiť kategórie', 'Save categories')}</span></button>
      </div>
    </form>
  );
}

function Waves({ disabled, run }: { disabled: boolean; run: Run }) {
  const { event } = useManage();
  const { t } = useI18n();
  const [rows, setRows] = useState<{ name: string; start: string }[]>(event.waves.map((w: WaveView) => ({ name: w.name, start: utcToLocalInput(w.startTime) })));
  return (
    <form className="card stack" onSubmit={(e) => {
      e.preventDefault();
      run(() => put<ManageView>(`/api/events/${event.eventId}/waves`, rows.map((r) => ({ name: r.name, startTime: localInputToUtc(r.start) ?? r.start }))));
    }}>
      <h2>{t('Štartové vlny', 'Start waves')}</h2>
      {rows.map((r, i) => (
        <div className="row" key={i}>
          <input aria-label={t('Názov', 'Name')} placeholder={t('Názov', 'Name')} value={r.name} disabled={disabled} style={{ flex: 1 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
          <input aria-label={t('Štart vlny', 'Wave start')} type="datetime-local" value={r.start} disabled={disabled} style={{ flex: 1 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, start: e.target.value } : x)))} />
          <button type="button" className="btn small danger" disabled={disabled} onClick={() => setRows(rows.filter((_, j) => j !== i))}><span>×</span></button>
        </div>))}
      <div className="row">
        <button type="button" className="btn small" disabled={disabled} onClick={() => setRows([...rows, { name: '', start: utcToLocalInput(event.startTime) }])}><span>{t('Pridať vlnu', 'Add wave')}</span></button>
        <button className="btn" disabled={disabled}><span>{t('Uložiť vlny', 'Save waves')}</span></button>
      </div>
    </form>
  );
}

function Deadline({ disabled, run }: { disabled: boolean; run: Run }) {
  const { event } = useManage();
  const { t } = useI18n();
  const [value, setValue] = useState(event.confirmationDeadline ? utcToLocalInput(event.confirmationDeadline) : '');
  return (
    <form className="card stack" onSubmit={(e) => {
      e.preventDefault();
      run(() => put<ManageView>(`/api/events/${event.eventId}/confirmation-deadline`, { confirmationDeadline: localInputToUtc(value) }));
    }}>
      <h2>{t('Termín potvrdenia účasti', 'Confirmation deadline')}</h2>
      <Field label={t('Do (prázdne = bez termínu)', 'Until (empty = none)')} name="confirmationDeadline">
        <input id="confirmationDeadline" type="datetime-local" value={value} disabled={disabled} onChange={(e) => setValue(e.target.value)} />
      </Field>
      <button className="btn" disabled={disabled}><span>{t('Uložiť termín', 'Save deadline')}</span></button>
    </form>
  );
}
