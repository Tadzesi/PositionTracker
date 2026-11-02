import { createContext, useContext, useState } from 'react';
import { Link, NavLink, Outlet, useNavigate, useParams } from 'react-router-dom';
import { BASE, del, get, post, put } from '../lib/api';
import { formatDateTime, localInputToUtc, utcToLocalInput } from '../lib/format';
import { useI18n } from '../lib/i18n';
import { useLoad, type Loaded } from '../lib/hooks';
import type { CategoryView, ManageView, WaveView } from '../lib/types';
import { EmailModeNotice, ErrorBanner, Field, Loading, StatusBadge, bannerError } from '../components/Ui';
import CourseSection from './CourseSection';
import DateTimeField from '../components/DateTimeField';
import { useSession } from '../components/Layout';

const ManageCtx = createContext<{ event: ManageView; reload: () => void; setEvent: (e: ManageView) => void } | null>(null);
export const useManage = () => {
  const v = useContext(ManageCtx);
  if (!v) throw new Error('event missing');
  return v;
};

/** Where the event pages live: the organizer's own route, or the administrator's one (which acts as the organizer). */
export function useEventBase(eventId: string): string {
  const { oid } = useParams();
  return oid ? `/admin/organizers/${oid}/events/${eventId}` : `/org/events/${eventId}`;
}

export default function EventLayout() {
  const { id } = useParams();
  const base = useEventBase(id ?? '');
  const { t, lang } = useI18n();
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
      <p className="mute" style={{ marginTop: -8 }}>
        {sportLabel(t, e.sport)} · {e.location} · {formatDateTime(e.startTime, lang)}
        {e.course ? ` · ${e.laps > 1 && !e.pointToPoint ? `${e.laps} × ` : ''}${(e.course.lapLengthM / 1000).toFixed(2)} km${e.pointToPoint ? ' · A → B' : ''}` : ''}
      </p>
      <EmailModeNotice mode={e.emailMode} />
      <nav className="tabs" aria-label="event">
        <NavLink to={base} end>{t('Nastavenie', 'Setup')}</NavLink>
        <NavLink to={`${base}/participants`}>{t('Účastníci', 'Participants')}</NavLink>
        <NavLink to={`${base}/control`}>{t('Riadenie pretekov', 'Race control')}</NavLink>
        {(e.state === 'live' || e.state === 'finished') && <NavLink to={`${base}/results`}>{t('Výsledky', 'Results')}</NavLink>}
      </nav>
      <Outlet />
    </ManageCtx.Provider>
  );
}

const sportLabel = (t: (a: string, b: string) => string, s: string) => (s === 'running' ? t('Beh', 'Running') : t('Cyklistika', 'Cycling'));

export function Setup() {
  const { event, setEvent } = useManage();
  const { t, lang } = useI18n();
  const { me } = useSession();
  const navigate = useNavigate();
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
  const advance = () => {
    if (!next) return;
    if (next === 'live' && !window.confirm(t('Spustiť podujatie naživo? Po spustení už nemožno meniť trasu.', 'Go live? The course cannot be changed afterwards.'))) return;
    if (next === 'finished' && !window.confirm(t('Ukončiť podujatie? Polohy sa už nebudú prijímať.', 'Finish the event? Positions are no longer accepted.'))) return;
    run(() => post<ManageView>(`/api/events/${event.eventId}/state`, { state: next }));
  };
  const p = event.participants;
  const total = p.invited + p.confirmed + p.declined + p.emailFailed;
  const base = useEventBase(event.eventId);
  const toCourse = () => document.getElementById('course')?.scrollIntoView({ behavior: 'smooth', block: 'start' });

  const steps = [
    { done: !!event.course, title: t('Nahrajte trasu', 'Upload the course'), hint: event.course
        ? event.pointToPoint
          ? t(`${(event.course.lapLengthM / 1000).toFixed(2)} km z bodu A do bodu B`, `${(event.course.lapLengthM / 1000).toFixed(2)} km from A to B`)
          : t(`${(event.course.lapLengthM / 1000).toFixed(2)} km na kolo`, `${(event.course.lapLengthM / 1000).toFixed(2)} km per lap`)
        : t('Súbor GPX s trasou jedného kola alebo z bodu A do bodu B.', 'A GPX file with the route of one lap or from A to B.'),
      action: <button type="button" className="btn small" onClick={toCourse}><span>{t('Prejsť na trasu', 'Go to the course')}</span></button> },
    { done: total > 0, title: t('Pozvite účastníkov', 'Invite participants'), hint: total > 0 ? t(`${total} pozvaných`, `${total} invited`) : t('Každý dostane e-mail s odkazom na potvrdenie.', 'Everybody gets an email with a link to confirm.'),
      action: <Link className="btn small" to={`${base}/participants`}><span>{t('Účastníci', 'Participants')}</span></Link> },
    { done: p.confirmed > 0, title: t('Počkajte na potvrdenia', 'Wait for confirmations'), hint: t(`${p.confirmed} z ${total} potvrdilo`, `${p.confirmed} of ${total} confirmed`) + (p.invited > 0 ? t(` · ${p.invited} čaká`, ` · ${p.invited} waiting`) : ''),
      action: p.invited > 0 ? <Link className="btn small" to={`${base}/participants?status=Invited`}><span>{t('Kto ešte nepotvrdil', 'Who has not confirmed')}</span></Link> : null },
    { done: event.state !== 'draft', title: t('Označte podujatie ako pripravené', 'Mark the event as ready'), hint: t('Po tomto kroku sa už dá spustiť naživo.', 'After this step you can go live.'),
      action: event.state === 'draft' ? <button type="button" className="btn small primary" disabled={busy || !confirmed || !event.course} onClick={advance}><span>{t('Označiť ako pripravené', 'Mark as ready')}</span></button> : null },
    { done: event.state === 'live' || event.state === 'finished', title: t('Spustite naživo v deň podujatia', 'Go live on the day'), hint: t('Účastníci začnú vysielať polohu, diváci uvidia mapu.', 'Participants start sending positions and spectators see the map.'),
      action: event.state === 'ready' ? <button type="button" className="btn small green" disabled={busy || !confirmed || p.confirmed === 0} onClick={advance}><span>{t('Spustiť naživo', 'Go live')}</span></button> : null },
  ];
  const current = steps.findIndex((s) => !s.done);

  return (
    <div className="stack">
      <ErrorBanner error={error} />
      {!confirmed && <div className="banner">{t('Potvrďte e-mail, aby ste mohli nahrať trasu a meniť podujatie.', 'Confirm your email to upload a course and change the event.')}</div>}

      {event.state !== 'finished' && event.state !== 'live' && (
        <div className="card">
          <h2>{t('Čo ďalej', 'What next')}</h2>
          <ol className="steps">
            {steps.map((s, i) => (
              <li key={i} className={s.done ? 'done' : i === current ? 'current' : ''} aria-current={i === current ? 'step' : undefined}>
                <span className="mark" aria-hidden="true">{s.done ? '✓' : i + 1}</span>
                <span className="txt"><b>{s.title}</b><small>{s.hint}</small></span>
                {i === current && s.action}
              </li>))}
          </ol>
        </div>
      )}

      <div className="card stack">
        <div className="row between">
          <h2>{t('Podujatie', 'Event')}</h2>
          {event.state !== 'finished' && <EditEvent confirmed={confirmed} />}
        </div>
        <div className="stats">
          <div className="stat"><b>{formatDateTime(event.startTime, lang)}</b><span>{t('Štart', 'Start')}</span></div>
          <div className="stat"><b>{event.location}</b><span>{t('Miesto', 'Location')}</span></div>
          <div className="stat"><b>{sportLabel(t, event.sport)}</b><span>{t('Šport', 'Sport')}</span></div>
          {event.pointToPoint
            ? <div className="stat"><b>{event.course ? `${(event.course.lapLengthM / 1000).toFixed(2)} km` : '–'}</b><span>{t('Z bodu A do bodu B', 'From A to B')}</span></div>
            : <div className="stat"><b>{event.course ? `${event.laps} × ${(event.course.lapLengthM / 1000).toFixed(2)} km` : '–'}</b><span>{t('Kolá × dĺžka', 'Laps × length')}</span></div>}
          <div className="stat"><b>{event.course ? `${Math.round(event.course.ascentM)} m` : '–'}</b><span>{event.pointToPoint ? t('Stúpanie', 'Ascent') : t('Stúpanie / kolo', 'Ascent / lap')}</span></div>
        </div>
        <h3>{t('Účastníci', 'Participants')}</h3>
        <div className="stats">
          <Link className="stat" to={`${base}/participants`}><b>{total}</b><span>{t('Pozvaných celkom', 'Invited in total')}</span></Link>
          <Link className="stat" to={`${base}/participants?status=Confirmed`}><b>{p.confirmed}</b><span>{t('Potvrdili', 'Confirmed')}</span></Link>
          <Link className="stat" to={`${base}/participants?status=Invited`}><b>{p.invited}</b><span>{t('Čakajú na odpoveď', 'Waiting')}</span></Link>
          <Link className="stat" to={`${base}/participants?status=Declined`}><b>{p.declined}</b><span>{t('Odmietli', 'Declined')}</span></Link>
          {p.emailFailed > 0 && <Link className="stat" to={`${base}/participants?status=EmailFailed`}><b>{p.emailFailed}</b><span>{t('E-mail zlyhal', 'Email failed')}</span></Link>}
        </div>
        {event.state === 'live' && (
          <div className="row">
            <button type="button" className="btn primary" onClick={() => navigate(`${base}/control`)}><span>{t('Riadiť preteky', 'Race control')}</span></button>
            <button type="button" className="btn danger" disabled={busy} onClick={advance}><span>{nextLabel}</span></button>
          </div>
        )}
        {event.state !== 'draft' && <p className="mute">{t('Verejný odkaz pre divákov:', 'Public link for spectators:')} <a href={`${BASE}/e/${event.eventId}`}>{`${location.origin}${BASE}/e/${event.eventId}`}</a></p>}
      </div>
      <CourseSection disabled={!confirmed || locked} />
      <Categories disabled={!confirmed || locked} />
      <Waves disabled={!confirmed || locked} />
      <Deadline disabled={!confirmed || event.state === 'finished'} />
      {event.state !== 'live' && <DeleteEvent listPath={base.startsWith('/admin') ? '/admin' : '/org'} />}
    </div>
  );
}

/**
 * Deleting the event with all its data. Only typing the event's name enables the button, so it is not one careless tap; a live
 * event cannot be deleted (finish it first).
 */
function DeleteEvent({ listPath }: { listPath: string }) {
  const { event } = useManage();
  const { t } = useI18n();
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [typed, setTyped] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const matches = typed.trim().toLowerCase() === event.name.trim().toLowerCase();
  const people = event.participants.invited + event.participants.confirmed + event.participants.declined + event.participants.emailFailed;
  const remove = async () => {
    setBusy(true); setError(null);
    try { await del(`/api/events/${event.eventId}`); navigate(listPath, { replace: true }); } catch (x) { setError(x); setBusy(false); }
  };
  return (
    <div className="card stack">
      <h2>{t('Odstrániť podujatie', 'Delete the event')}</h2>
      {!open ? (
        <div className="row"><button type="button" className="btn danger" onClick={() => setOpen(true)}><span>{t('Odstrániť podujatie…', 'Delete the event…')}</span></button></div>
      ) : (
        <>
          <p>{t('Podujatie sa odstráni natrvalo spolu so všetkými údajmi: trasou, kategóriami, vlnami, účastníkmi, ich polohami a výsledkami. Nedá sa to vrátiť.',
            'The event is deleted for good with all its data: course, categories, waves, participants, their positions and the results. This cannot be undone.')}</p>
          {people > 0 && <p className="mute">{t(`Účastníkov (${people}) nebudeme informovať; ich odkazy prestanú fungovať a neodoslané pozvánky sa zahodia.`,
            `The participants (${people}) are not told; their links stop working and invitations not yet sent are dropped.`)}</p>}
          <Field label={t(`Pre potvrdenie napíšte názov podujatia: ${event.name}`, `To confirm, type the event name: ${event.name}`)} name="confirmName">
            <input id="confirmName" autoComplete="off" value={typed} onChange={(e) => setTyped(e.target.value)} />
          </Field>
          <ErrorBanner error={error} />
          <div className="row">
            <button type="button" className="btn danger" disabled={!matches || busy} onClick={remove}><span>{t('Odstrániť natrvalo', 'Delete for good')}</span></button>
            <button type="button" className="btn ghost" onClick={() => { setOpen(false); setTyped(''); setError(null); }}><span>{t('Zrušiť', 'Cancel')}</span></button>
          </div>
        </>
      )}
    </div>
  );
}

/** Name, location, start and sport. A live event keeps its start and sport (the server refuses a change; the fields are locked here). */
function EditEvent({ confirmed }: { confirmed: boolean }) {
  const { event, setEvent } = useManage();
  const { t } = useI18n();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(event.name);
  const [loc, setLoc] = useState(event.location);
  const [start, setStart] = useState(utcToLocalInput(event.startTime));
  const [sport, setSport] = useState<string>(event.sport);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const live = event.state === 'live';
  const show = () => { setName(event.name); setLoc(event.location); setStart(utcToLocalInput(event.startTime)); setSport(event.sport); setError(null); setOpen(true); };
  if (!open) return <button type="button" className="btn small" disabled={!confirmed} onClick={show}><span>{t('Upraviť', 'Edit')}</span></button>;
  return (
    <form className="stack editevent" style={{ flexBasis: '100%' }} onSubmit={async (e) => {
      e.preventDefault();
      setBusy(true); setError(null);
      try {
        setEvent(await put<ManageView>(`/api/events/${event.eventId}`, { name, location: loc, startTime: localInputToUtc(start) ?? start, sport }));
        setOpen(false);
      } catch (x) { setError(x); }
      setBusy(false);
    }}>
      <ErrorBanner error={bannerError(error, ['name', 'location', 'startTime', 'sport'])} />
      <div className="grid two">
        <Field label={t('Názov', 'Name')} name="name" error={error}><input id="name" value={name} onChange={(e) => setName(e.target.value)} required /></Field>
        <Field label={t('Miesto', 'Location')} name="location" error={error}><input id="location" value={loc} onChange={(e) => setLoc(e.target.value)} required /></Field>
        <Field label={t('Dátum a čas štartu (vaše miestne)', 'Start date and time (your local time)')} name="startTime" error={error}>
          <DateTimeField id="startTime" value={start} disabled={live} onChange={setStart} required />
        </Field>
        <Field label={t('Šport', 'Sport')} name="sport" error={error}>
          <select id="sport" value={sport} disabled={live} onChange={(e) => setSport(e.target.value)}>
            <option value="cycling">{t('Cyklistika', 'Cycling')}</option>
            <option value="running">{t('Beh', 'Running')}</option>
          </select>
        </Field>
      </div>
      {live
        ? <p className="mute">{t('Podujatie už beží: štart a šport sa už nedajú zmeniť, názov a miesto áno.', 'The event is live: the start and sport can no longer be changed, the name and location can.')}</p>
        : <p className="mute">{t('Pri zmene štartu skontrolujte štartové vlny a termín potvrdenia – musia ostať po novom štarte. Účastníkom sa e-mail o zmene neposiela.', 'When you change the start, check the start waves and the confirmation deadline – they must stay after the new start. Participants are not emailed about the change.')}</p>}
      <div className="row">
        <button className="btn primary" disabled={busy}><span>{t('Uložiť', 'Save')}</span></button>
        <button type="button" className="btn ghost" onClick={() => setOpen(false)}><span>{t('Zrušiť', 'Cancel')}</span></button>
      </div>
    </form>
  );
}

/**
 * Saving one section of the setup page: the error and the confirmation are shown in the section itself (the page-level banner is
 * out of sight on a phone, so a refused save looked like a saved one).
 */
function useSectionSave() {
  const { setEvent } = useManage();
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const [busy, setBusy] = useState(false);
  const save = async (fn: () => Promise<ManageView>): Promise<ManageView | null> => {
    setBusy(true); setError(null); setSaved(false);
    try { const view = await fn(); setEvent(view); setSaved(true); return view; } catch (x) { setError(x); return null; } finally { setBusy(false); }
  };
  return { save, error, saved, busy, edited: () => setSaved(false) };
}

function SectionStatus({ error, saved }: { error: unknown; saved: boolean }) {
  const { t } = useI18n();
  return (
    <>
      <ErrorBanner error={error} />
      {saved && <div className="banner ok" role="status">{t('Uložené.', 'Saved.')}</div>}
    </>
  );
}

function Categories({ disabled }: { disabled: boolean }) {
  const { event } = useManage();
  const { t } = useI18n();
  const section = useSectionSave();
  const [rows, setRowsRaw] = useState<Pick<CategoryView, 'name' | 'gender' | 'minAge' | 'maxAge'>[]>(event.categories);
  const setRows = (next: typeof rows) => { setRowsRaw(next); section.edited(); };
  const num = (v: string) => (v === '' ? null : Number(v));
  // a row left completely empty (an "Add category" not filled in) is not a category: it is dropped instead of refusing the save
  const filled = rows.filter((r) => r.name.trim() !== '' || r.gender || r.minAge !== null || r.maxAge !== null);
  return (
    <form className="card stack" onSubmit={async (e) => {
      e.preventDefault();
      const view = await section.save(() => put<ManageView>(`/api/events/${event.eventId}/categories`, filled));
      if (view) setRowsRaw(view.categories);
    }}>
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
        <button className="btn" disabled={disabled || section.busy}><span>{t('Uložiť kategórie', 'Save categories')}</span></button>
      </div>
      <SectionStatus error={section.error} saved={section.saved} />
    </form>
  );
}

function Waves({ disabled }: { disabled: boolean }) {
  const { event } = useManage();
  const { t } = useI18n();
  const section = useSectionSave();
  const toRows = (waves: WaveView[]) => waves.map((w) => ({ name: w.name, start: utcToLocalInput(w.startTime) }));
  const [rows, setRowsRaw] = useState<{ name: string; start: string }[]>(toRows(event.waves));
  const setRows = (next: typeof rows) => { setRowsRaw(next); section.edited(); };
  return (
    <form className="card stack" onSubmit={async (e) => {
      e.preventDefault();
      // a wave without a name is an "Add wave" not filled in: dropped instead of refusing the save
      const view = await section.save(() => put<ManageView>(`/api/events/${event.eventId}/waves`,
        rows.filter((r) => r.name.trim() !== '').map((r) => ({ name: r.name, startTime: localInputToUtc(r.start) ?? r.start }))));
      if (view) setRowsRaw(toRows(view.waves));
    }}>
      <h2>{t('Štartové vlny', 'Start waves')}</h2>
      <p className="help">{t('Vlny použite, keď pretekári nevyštartujú naraz (napr. elita o 10:00, ostatní o 10:05). Každá vlna má názov a čas štartu, ktorý nesmie byť skôr ako štart podujatia ani viac ako 24 hodín po ňom. Pretekára do vlny zaradíte na stránke Účastníci. Vlny sú voliteľné; bez nich štartujú všetci v čas štartu podujatia. Čas, tempo, odhad príchodu aj poradie sa počítajú od štartu vlny pretekára, odstup na mape je v čistom čase.',
        'Use waves when riders do not start all at once (e.g. elite at 10:00, everybody else at 10:05). A wave has a name and a start time that is not before the event start and at most 24 hours after it. You put a rider in a wave on the Participants page. Waves are optional; without them everybody starts at the event start. Time, pace, finish estimate and ranking are counted from the start of the rider’s wave; gaps on the map are in net time.')}</p>
      {rows.map((r, i) => (
        <div className="row" key={i}>
          <input aria-label={t('Názov', 'Name')} placeholder={t('Názov', 'Name')} value={r.name} disabled={disabled} style={{ flex: 1 }} onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
          <div style={{ flex: 2, minWidth: 220 }}><DateTimeField id={`wave-start-${i}`} value={r.start} disabled={disabled} onChange={(v) => setRows(rows.map((x, j) => (j === i ? { ...x, start: v } : x)))} /></div>
          <button type="button" className="btn small danger" disabled={disabled} onClick={() => setRows(rows.filter((_, j) => j !== i))}><span>×</span></button>
        </div>))}
      <div className="row">
        <button type="button" className="btn small" disabled={disabled} onClick={() => setRows([...rows, { name: '', start: utcToLocalInput(event.startTime) }])}><span>{t('Pridať vlnu', 'Add wave')}</span></button>
        <button className="btn" disabled={disabled || section.busy}><span>{t('Uložiť vlny', 'Save waves')}</span></button>
      </div>
      <SectionStatus error={section.error} saved={section.saved} />
    </form>
  );
}

function Deadline({ disabled }: { disabled: boolean }) {
  const { event } = useManage();
  const { t } = useI18n();
  const section = useSectionSave();
  const [value, setValueRaw] = useState(event.confirmationDeadline ? utcToLocalInput(event.confirmationDeadline) : '');
  const setValue = (v: string) => { setValueRaw(v); section.edited(); };
  return (
    <form className="card stack" onSubmit={(e) => {
      e.preventDefault();
      void section.save(() => put<ManageView>(`/api/events/${event.eventId}/confirmation-deadline`, { confirmationDeadline: localInputToUtc(value) }));
    }}>
      <h2>{t('Termín potvrdenia účasti', 'Confirmation deadline')}</h2>
      <Field label={t('Do (prázdne = bez termínu)', 'Until (empty = none)')} name="confirmationDeadline">
        <DateTimeField id="confirmationDeadline" value={value} disabled={disabled} onChange={setValue} />
      </Field>
      <button className="btn" disabled={disabled || section.busy}><span>{t('Uložiť termín', 'Save deadline')}</span></button>
      <SectionStatus error={section.error} saved={section.saved} />
    </form>
  );
}
