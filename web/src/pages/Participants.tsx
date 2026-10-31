import { useState, type FormEvent } from 'react';
import { del, get, post, put } from '../lib/api';
import { useI18n } from '../lib/i18n';
import { useLoad } from '../lib/hooks';
import type { ParticipantList, ParticipantRow } from '../lib/types';
import { EmailModeNotice, ErrorBanner, Field, Loading, StatusBadge, bannerError } from '../components/Ui';
import { useManage } from './EventLayout';
import { useSession } from '../components/Layout';

const FILTERS = ['', 'Invited', 'Confirmed', 'Declined', 'EmailFailed'] as const;

export default function Participants() {
  const { event } = useManage();
  const { t } = useI18n();
  const { me } = useSession();
  const [status, setStatus] = useState<string>('');
  const [q, setQ] = useState('');
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const base = `/api/events/${event.eventId}/participants`;
  const list = useLoad(() => get<ParticipantList>(`${base}?status=${status}&q=${encodeURIComponent(q)}`), [event.eventId, status, q]);
  const confirmed = me?.emailConfirmed ?? false;
  const finished = event.state === 'finished';

  const act = async (fn: () => Promise<string | void>) => {
    setError(null); setNotice(null);
    try { const n = await fn(); if (n) setNotice(n); list.reload(); } catch (x) { setError(x); }
  };

  const label = (s: string) => ({ '': t('Všetci', 'All'), Invited: t('Pozvaní', 'Invited'), Confirmed: t('Potvrdení', 'Confirmed'), Declined: t('Odmietli', 'Declined'), EmailFailed: t('Chyba e-mailu', 'Email failed') })[s] ?? s;
  const statusLabel = (s: string) => label(s);
  const count = (s: string) => list.data ? ({ '': list.data.counts.all, Invited: list.data.counts.invited, Confirmed: list.data.counts.confirmed, Declined: list.data.counts.declined, EmailFailed: list.data.counts.emailFailed } as Record<string, number>)[s] : 0;

  return (
    <div className="stack">
      <EmailModeNotice mode={list.data?.emailMode} />
      <ErrorBanner error={error ?? list.error} />
      {notice && <div className="banner ok" role="status">{notice}</div>}
      <div className="card">
        <div className="row" style={{ justifyContent: 'space-between' }}>
          <h2>{t('Účastníci', 'Participants')}</h2>
          <button className="btn small" disabled={!confirmed || finished} onClick={() => act(async () => {
            const r = await post<{ sent: number; limited: number }>(`${base}/remind`);
            return t(`Pripomienka odoslaná: ${r.sent}, odložené kvôli limitu: ${r.limited}.`, `Reminders sent: ${r.sent}, held back by the limit: ${r.limited}.`);
          })}><span>{t('Poslať pripomienku nepotvrdeným', 'Remind those not confirmed')}</span></button>
        </div>
        <div className="row" role="group" aria-label={t('Filter stavu', 'Status filter')}>
          {FILTERS.map((f) => <button key={f} className="chip" aria-pressed={status === f} onClick={() => setStatus(f)}>{label(f)} {list.data ? count(f) : ''}</button>)}
          <input aria-label={t('Hľadať', 'Search')} placeholder={t('Hľadať meno, e-mail, číslo…', 'Search name, email, bib…')} value={q} onChange={(e) => setQ(e.target.value)} style={{ maxWidth: 280 }} />
        </div>
        {list.loading && !list.data ? <Loading /> : (
          <div className="scroll"><table>
            <thead><tr><th>#</th><th>{t('Meno', 'Name')}</th><th>E-mail</th><th>{t('Kategória', 'Category')}</th><th>{t('Stav', 'Status')}</th><th /></tr></thead>
            <tbody>{list.data?.participants.map((p) => (
              <Row key={p.participantId} p={p} eventState={event.state} act={act} base={base} eventId={event.eventId} statusLabel={statusLabel} />))}</tbody>
          </table></div>)}
        {list.data?.participants.length === 0 && <p className="mute">{t('Žiadni účastníci.', 'No participants.')}</p>}
      </div>
      <AddParticipant base={base} disabled={!confirmed || finished} onAdded={() => list.reload()} categories={event.categories.map((c) => c.name)} waves={event.waves.map((w) => w.name)} />
    </div>
  );
}

function Row({ p, eventState, act, base, eventId, statusLabel }: {
  p: ParticipantRow; eventState: string; act: (fn: () => Promise<string | void>) => Promise<void>; base: string; eventId: string; statusLabel: (s: string) => string;
}) {
  const { t } = useI18n();
  const [edit, setEdit] = useState(false);
  const [nick, setNick] = useState(p.nickname ?? '');
  const id = p.participantId;
  const sent = t('Odoslané.', 'Sent.');
  return (
    <>
      <tr>
        <td>{p.bib}</td>
        <td>{p.name}{p.nickname && <div className="mute">„{p.nickname}“{p.nicknameHidden ? ` (${t('skrytá', 'hidden')})` : ''}</div>}{p.isMinor && <div className="mute">{t('maloletý', 'minor')}</div>}</td>
        <td>{p.email}</td>
        <td>{p.category ?? '–'}</td>
        <td><StatusBadge value={p.status} label={statusLabel(p.status)} /></td>
        <td className="row">
          <button className="btn small" disabled={p.status === 'Declined'} onClick={() => act(async () => { await post(`${base}/${id}/resend`); return sent; })}><span>{t('Poslať znova', 'Resend')}</span></button>
          <button className="btn small" disabled={p.status === 'Declined'} title={t('Zruší token zariadenia a pošle nový odkaz', 'Revokes the device token and mails a new link')}
            onClick={() => act(async () => { const r = await post<{ mailQueued: boolean }>(`${base}/${id}/reissue-token`); return r.mailQueued ? sent : t('Token zrušený, e-mail sa pre limit neposlal – pošlite ho neskôr.', 'Token revoked; the email was held back by a limit – resend later.'); })}><span>{t('Nový odkaz', 'Re-issue')}</span></button>
          <button className="btn small ghost" onClick={() => setEdit(!edit)}><span>{t('Upraviť', 'Edit')}</span></button>
          {(eventState === 'draft' || eventState === 'ready') && (
            <button className="btn small danger" onClick={() => { if (window.confirm(t(`Odstrániť ${p.name}?`, `Remove ${p.name}?`))) act(() => del(`${base}/${id}`)); }}><span>{t('Odstrániť', 'Remove')}</span></button>)}
        </td>
      </tr>
      {edit && (
        <tr><td colSpan={6}>
          <div className="row">
            <input aria-label={t('Prezývka', 'Nickname')} placeholder={t('Prezývka', 'Nickname')} value={nick} onChange={(e) => setNick(e.target.value)} style={{ maxWidth: 240 }} />
            <button className="btn small" onClick={() => act(async () => { await put(`${base}/${id}`, { nickname: nick }); setEdit(false); })}><span>{t('Uložiť prezývku', 'Save nickname')}</span></button>
            <button className="btn small" onClick={() => act(async () => { await put(`${base}/${id}`, { hideNickname: !p.nicknameHidden }); setEdit(false); })}><span>{p.nicknameHidden ? t('Zobraziť prezývku divákom', 'Show nickname to spectators') : t('Skryť prezývku divákom', 'Hide nickname from spectators')}</span></button>
            {eventState !== 'draft' && eventState !== 'ready' && (
              <button className="btn small danger" onClick={() => { if (window.confirm(t('Vymazať osobné údaje a polohy tohto účastníka? Nedá sa vrátiť.', 'Delete this participant\'s personal data and positions? This cannot be undone.'))) act(async () => { await del(`/api/events/${eventId}/participants/${id}/data`); return t('Údaje vymazané.', 'Data deleted.'); }); }}><span>{t('Vymazať údaje', 'Delete data')}</span></button>)}
          </div>
        </td></tr>)}
    </>
  );
}

function AddParticipant({ base, disabled, onAdded, categories, waves }: { base: string; disabled: boolean; onAdded: () => void; categories: string[]; waves: string[] }) {
  const { t, lang } = useI18n();
  const empty = { name: '', email: '', gender: 'male', language: lang as string, category: '', wave: '', bib: '' };
  const [f, setF] = useState(empty);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const set = (k: keyof typeof empty) => (e: { target: { value: string } }) => setF({ ...f, [k]: e.target.value });
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await post(base, { ...f, category: f.category || null, wave: f.wave || null, bib: f.bib || null }); setF({ ...empty, language: f.language }); onAdded(); } catch (x) { setError(x); }
    setBusy(false);
  };
  const fields = ['name', 'email', 'gender', 'language', 'category', 'wave', 'bib'];
  return (
    <form className="card" onSubmit={submit}>
      <h2>{t('Pridať účastníka', 'Add a participant')}</h2>
      <ErrorBanner error={bannerError(error, fields)} />
      <div className="grid two">
        <Field label={t('Meno', 'Name')} name="name" error={error}><input id="name" value={f.name} onChange={set('name')} required disabled={disabled} /></Field>
        <Field label="E-mail" name="email" error={error}><input id="email" type="email" value={f.email} onChange={set('email')} required disabled={disabled} /></Field>
        <Field label={t('Pohlavie', 'Gender')} name="gender" error={error}>
          <select id="gender" value={f.gender} onChange={set('gender')} disabled={disabled}><option value="male">{t('Muž', 'Male')}</option><option value="female">{t('Žena', 'Female')}</option></select>
        </Field>
        <Field label={t('Jazyk e-mailu', 'Email language')} name="language" error={error}>
          <select id="language" value={f.language} onChange={set('language')} disabled={disabled}><option value="sk">Slovenčina</option><option value="en">English</option></select>
        </Field>
        <Field label={t('Kategória', 'Category')} name="category" error={error}>
          <select id="category" value={f.category} onChange={set('category')} disabled={disabled}><option value="">–</option>{categories.map((c) => <option key={c}>{c}</option>)}</select>
        </Field>
        <Field label={t('Vlna', 'Wave')} name="wave" error={error}>
          <select id="wave" value={f.wave} onChange={set('wave')} disabled={disabled}><option value="">–</option>{waves.map((c) => <option key={c}>{c}</option>)}</select>
        </Field>
        <Field label={t('Štartové číslo (prázdne = automaticky)', 'Bib (empty = automatic)')} name="bib" error={error}><input id="bib" value={f.bib} onChange={set('bib')} disabled={disabled} /></Field>
      </div>
      <button className="btn primary" disabled={disabled || busy}><span>{t('Pridať a poslať pozvánku', 'Add and send the invitation')}</span></button>
    </form>
  );
}
