import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import QRCode from 'qrcode';
import { ApiError, BASE, get, post, put } from '../lib/api';
import { formatDateTime, formatDuration, formatGap, formatKm, parseSpan } from '../lib/format';
import { useI18n, type Lang } from '../lib/i18n';
import { useLoad, usePoll } from '../lib/hooks';
import type { LinkResult, LinkView } from '../lib/types';
import { LinkDot, useLinkText } from '../components/LinkLight';
import { ErrorBanner, Field, Loading, StatusBadge, bannerError } from '../components/Ui';
import { StopTrackingPanel } from '../components/StopTrackingPanel';

interface Setup { deepLink: string; otrcJson: Record<string, unknown>; qrPayload: string }

/** The participant's personal page, opened from the invitation email: /p?token=… */
export default function ParticipantPage() {
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const { lang } = useI18n();
  const view = useLoad(() => get<LinkView>(`/api/p/${encodeURIComponent(token)}`), [token]);
  // The page follows the language of the invitation until the participant picks another with the toggle.
  const [chosen, setChosen] = useState(false);
  const { setLang } = useI18n();
  useEffect(() => { if (!chosen && view.data && (view.data.language === 'sk' || view.data.language === 'en') && view.data.language !== lang) setLang(view.data.language as Lang); },
    [view.data, chosen, lang, setLang]);
  useEffect(() => { setChosen(false); }, [token]);
  if (!token) return <LinkProblem error={new ApiError(404, 'link_invalid', { sk: 'Odkaz neobsahuje token.', en: 'The link has no token.' })} />;
  if (view.error) return <LinkProblem error={view.error} token={token} />;
  if (!view.data) return <Loading />;
  return <Flow token={token} view={view.data} reload={view.reload} />;
}

function LinkProblem({ error, token }: { error: unknown; token?: string }) {
  const { t } = useI18n();
  const e = error instanceof ApiError ? error : null;
  const [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false);
  const [renewError, setRenewError] = useState<unknown>(null);
  const canRenew = !!token && (e?.code === 'link_expired' || e?.code === 'link_superseded');
  const renew = async () => {
    setBusy(true); setRenewError(null);
    try { await post(`/api/p/${encodeURIComponent(token ?? '')}/renew`); setSent(true); } catch (x) { setRenewError(x); } finally { setBusy(false); }
  };
  return (
    <div className="card narrow stack">
      <h2>{t('Odkaz nefunguje', 'This link does not work')}</h2>
      <ErrorBanner error={error} />
      {canRenew && !sent && <button className="btn primary" disabled={busy} onClick={renew}><span>{t('Pošlite mi nový odkaz e-mailom', 'Email me a new link')}</span></button>}
      {sent && <div className="banner ok" role="status">{t('Nový odkaz sme poslali na váš e-mail.', 'We have sent a new link to your email address.')}</div>}
      <ErrorBanner error={renewError} />
      {e?.code === 'link_expired' && <p>{t('Požiadajte organizátora o nový odkaz.', 'Ask the organizer for a new link.')}</p>}
      {e?.code === 'link_superseded' && <p>{t('Použite odkaz z najnovšieho e-mailu od organizátora.', 'Use the link from the most recent email from the organizer.')}</p>}
    </div>
  );
}

function Flow({ token, view, reload }: { token: string; view: LinkView; reload: () => void }) {
  const { t, lang } = useI18n();
  const [deviceToken, setDeviceToken] = useState<string | null>(null);
  const [redo, setRedo] = useState(false);
  const me = view.me;
  const statusLabel = ({ Invited: t('Čaká na potvrdenie', 'Awaiting confirmation'), Confirmed: t('Potvrdené', 'Confirmed'), Declined: t('Odmietnuté', 'Declined'), EmailFailed: t('Čaká na potvrdenie', 'Awaiting confirmation') } as Record<string, string>)[me.status] ?? me.status;
  const phoneIsSetUp = me.status === 'Confirmed' && me.phoneSetUp && !deviceToken && !redo;
  return (
    <div className="stack narrow">
      <div className="card">
        <h1>{view.event.name}</h1>
        <p className="mute">{view.event.location} · {formatDateTime(view.event.startTime, lang)}</p>
        <p><b>{me.name}</b> · #{me.bib}{me.category ? ` · ${me.category}` : ''} <StatusBadge value={me.status} label={statusLabel} /></p>
      </div>
      {view.event.finished ? (
        <>
          <OwnResult result={view.result ?? null} eventId={view.event.id} participantId={me.id} />
          <StopTrackingPanel />
        </>) : (
        <>
          <AndroidApp token={token} />
          <Step1 token={token} view={view} reload={reload} />
          {me.status === 'Confirmed' && <ConnectionCard token={token} initial={view.link ?? null} />}
          {phoneIsSetUp && (
            <div className="card stack">
              <p className="big">{t('Ste prihlásený a telefón je nastavený.', 'You are registered and your phone is set up.')}</p>
              <div className="row"><button className="btn" onClick={() => setRedo(true)}><span>{t('Nastaviť telefón znova', 'Set up the phone again')}</span></button></div>
            </div>)}
          {me.status === 'Confirmed' && !phoneIsSetUp && <Step2 token={token} onToken={setDeviceToken} replaces={redo} />}
          {deviceToken && <Step3 token={token} deviceToken={deviceToken} />}
          {me.status === 'Confirmed' && <SpectatorLink eventId={view.event.id} participantId={me.id} name={me.name} />}
          <Share />
        </>)}
    </div>
  );
}

/** The participant's own line of the results once the event has finished, and the way to the public results. */
function OwnResult({ result, eventId, participantId }: { result: LinkResult | null; eventId: string; participantId: string }) {
  const { t } = useI18n();
  const url = `${BASE}/e/${eventId}?follow=${participantId}`;
  return (
    <div className="card stack">
      <h2>{t('Váš výsledok', 'Your result')}</h2>
      {!result || result.status === 'dns' ? (
        <p className="mute">{t('Na trati sme od vás nezachytili žiadnu polohu, preto nemáte výsledok.', 'We received no position of yours on the course, so you have no result.')}</p>
      ) : result.status === 'finished' ? (
        <div className="stats">
          <div className="stat"><b>{result.rank ?? '–'}.</b><span>{t(`Poradie z ${result.finishers}`, `Place of ${result.finishers}`)}</span></div>
          <div className="stat"><b>{formatDuration(parseSpan(result.netTime))}</b><span>{t('Čistý čas', 'Net time')}</span></div>
          <div className="stat"><b>{result.rank === 1 ? t('Víťaz', 'Winner') : formatGap(result.gapToLeader)}</b><span>{t('Strata na víťaza', 'Behind the winner')}</span></div>
          {result.categoryRank !== null && <div className="stat"><b>{result.categoryRank}.</b><span>{t('V kategórii', 'In the category')}</span></div>}
        </div>
      ) : (
        <div className="stats">
          <div className="stat"><b>DNF</b><span>{t('Do cieľa ste nedorazili', 'You did not reach the finish')}</span></div>
          <div className="stat"><b>{formatKm(result.distanceM)}</b><span>{t('Prejdené', 'Distance')}</span></div>
        </div>
      )}
      <p className="mute">{t('Oficiálne výsledky vydáva organizátor.', 'The organizer issues the official results.')}</p>
      <div className="row"><a className="btn" href={url}><span>{t('Všetky výsledky', 'All results')}</span></a></div>
    </div>
  );
}

/** A link to the public spectator page that follows this participant; the family can open it before the start. */
function SpectatorLink({ eventId, participantId, name }: { eventId: string; participantId: string; name: string }) {
  const { t } = useI18n();
  const [copied, setCopied] = useState(false);
  const url = `${location.origin}${BASE}/e/${eventId}?follow=${participantId}`;
  const share = async () => {
    try {
      if (navigator.share) await navigator.share({ title: name, url });
      else { await navigator.clipboard.writeText(url); setCopied(true); }
    } catch { /* cancelled */ }
  };
  return (
    <div className="card stack">
      <h2>{t('Odkaz pre divákov', 'Link for spectators')}</h2>
      <p className="mute">{t('Pošlite ho rodine – uvidia vás na mape od štartu.', 'Send it to your family – they will see you on the map from the start.')}</p>
      <p><a href={url}>{url}</a></p>
      <div className="row">
        <button type="button" className="btn" onClick={share}><span>{t('Zdieľať', 'Share')}</span></button>
        {copied && <span className="mute" role="status">{t('Skopírované', 'Copied')}</span>}
      </div>
    </div>
  );
}

/** The traffic light: is the phone's data reaching the server? Follows the server every 5 s while the page is open. */
function ConnectionCard({ token, initial }: { token: string; initial: LinkView['link'] | null }) {
  const { t } = useI18n();
  const text = useLinkText();
  const poll = usePoll(() => get<{ link: LinkView['link'] }>(`/api/p/${encodeURIComponent(token)}/link`), 5000, [token]);
  const health = poll.data ? poll.data.link ?? null : initial;
  const { title, hints } = text(health);
  const level = health?.level ?? 'waiting';
  return (
    <div className={`card stack link-card ${level}`} role="status" aria-live="polite">
      <h2>{t('Spojenie telefónu', 'Phone connection')} <LinkDot health={health} /></h2>
      <p className="big">{title}</p>
      {hints.length > 0 && <ul>{hints.map((h) => <li key={h}>{h}</li>)}</ul>}
      {health && health.level !== 'silent' && health.level !== 'waiting' && (
        <p className="mute">
          {health.batteryPct != null && t(`Batéria ${Math.round(health.batteryPct)} %`, `Battery ${Math.round(health.batteryPct)} %`)}
          {health.accuracyM != null && ` · ${t('presnosť', 'accuracy')} ${Math.round(health.accuracyM)} m`}
        </p>)}
      {poll.error != null && <p className="mute">{t('Stránka sa nevie spojiť so serverom – skontrolujte internet.', 'This page cannot reach the server – check your internet connection.')}</p>}
      <p className="mute">{t('Skúška pred štartom: spustite sledovanie v aplikácii (alebo v OwnTracks pošlite polohu) a do 10 sekúnd by sa tu mal objaviť zelený stav.', 'Test before the start: start tracking in the app (or send a position in OwnTracks) and a green state should appear here within 10 seconds.')}</p>
    </div>
  );
}

function Step1({ token, view, reload }: { token: string; view: LinkView; reload: () => void }) {
  const { t } = useI18n();
  const me = view.me;
  const [nickname, setNickname] = useState(me.nickname ?? '');
  const [gender, setGender] = useState(me.gender);
  const [error, setError] = useState<unknown>(null);
  const [saved, setSaved] = useState(false);
  const base = `/api/p/${encodeURIComponent(token)}`;
  const run = async (fn: () => Promise<unknown>) => {
    setError(null); setSaved(false);
    try { await fn(); setSaved(true); reload(); } catch (x) { setError(x); }
  };
  const confirmed = me.status === 'Confirmed';
  return (
    <div className="card stack">
      <h2>1 · {t('Účasť', 'Participation')}</h2>
      <ErrorBanner error={bannerError(error, ['nickname', 'gender'])} />
      <Field label={t('Prezývka pre divákov (nepovinné, max. 30 znakov)', 'Nickname for spectators (optional, max. 30 characters)')} name="nickname" error={error}>
        <input id="nickname" value={nickname} maxLength={60} onChange={(e) => setNickname(e.target.value)} />
      </Field>
      <Field label={t('Pohlavie (po štarte sa nedá zmeniť)', 'Gender (locked once the race starts)')} name="gender" error={error}>
        <select id="gender" value={gender} disabled={me.locked.gender} onChange={(e) => setGender(e.target.value)}>
          <option value="male">{t('Muž', 'Male')}</option><option value="female">{t('Žena', 'Female')}</option>
        </select>
      </Field>
      <div className="row">
        {!confirmed
          ? <button className="btn primary" onClick={() => run(() => post(`${base}/confirm`, { nickname, gender }))}><span>{t('Potvrdiť účasť', 'Confirm participation')}</span></button>
          : <button className="btn" onClick={() => run(() => put(`${base}/profile`, { nickname, gender }))}><span>{t('Uložiť zmeny', 'Save changes')}</span></button>}
        {me.status !== 'Declined' && (
          <button className="btn danger" onClick={() => { if (window.confirm(t('Naozaj odmietnuť účasť?', 'Really decline?'))) run(() => post(`${base}/decline`)); }}><span>{t('Odmietnuť', 'Decline')}</span></button>)}
      </div>
      {saved && <div className="banner ok" role="status">{t('Uložené.', 'Saved.')}</div>}
    </div>
  );
}

function Step2({ token, onToken, replaces = false }: { token: string; onToken: (t: string) => void; replaces?: boolean }) {
  const { t, err } = useI18n();
  const [location, setLocation] = useState(false);
  const [showName, setShowName] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);
  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const r = await post<{ deviceToken: string }>(`/api/p/${encodeURIComponent(token)}/consent`, { locationConsent: location, showNameToSpectators: showName });
      onToken(r.deviceToken);
    } catch (x) { setError(x); }
    setBusy(false);
  };
  return (
    <div className="card stack">
      <h2>2 · {t('Súhlas so sledovaním', 'Consent to tracking')}</h2>
      {replaces && <div className="banner">{t('Nové nastavenie nahradí to predchádzajúce – telefón potom treba nastaviť znova.', 'A new setup replaces the previous setup – the phone then has to be set up again.')}</div>}
      <ul className="mute">
        <li>{t('Organizátor vidí vašu polohu presne a hneď.', 'The organizer sees your position exactly and immediately.')}</li>
        <li>{t('Diváci ju vidia oneskorene a len počas pretekov.', 'Spectators see it delayed, and only during the race.')}</li>
        <li>{t('Polohy sa uchovávajú 30 dní po skončení podujatia, potom sa zmažú. Vymazanie môžete kedykoľvek požiadať u organizátora.', 'Positions are kept for 30 days after the event, then deleted. You can ask the organizer to delete them at any time.')}</li>
        <li>{t('Viac v ', 'More in the ')}<Link to="/privacy" target="_blank">{t('zásadách ochrany osobných údajov', 'privacy notice')}</Link>.</li>
      </ul>
      <ErrorBanner error={bannerError(error, ['locationConsent', 'showNameToSpectators'])} />
      <label className="check"><input type="checkbox" checked={location} onChange={(e) => setLocation(e.target.checked)} />
        <span>{t('Súhlasím so zdieľaním mojej polohy počas pretekov.', 'I agree to share my location during the race.')}</span></label>
      <label className="check"><input type="checkbox" checked={showName} onChange={(e) => setShowName(e.target.checked)} />
        <span>{t('Diváci môžu vidieť moje meno (inak len štartové číslo).', 'Spectators may see my name (otherwise only my bib).')}</span></label>
      {error instanceof ApiError && (error.field === 'locationConsent' || error.field === 'showNameToSpectators') && <div role="alert" style={{ color: 'var(--red)' }}>{err(error)}</div>}
      <button className="btn green" disabled={!location || busy} onClick={submit}><span>{t('Súhlasím a pokračovať', 'Agree and continue')}</span></button>
      <p className="mute">{t('Opakovaním tohto kroku získate nové nastavenie; predchádzajúce prestane fungovať.', 'Repeating this step gives you a new setup; the previous one stops working.')}</p>
    </div>
  );
}

/** The production app root; the Android app accepts invitations only for it (a debug build also for other servers). */
const PRODUCTION_APP = 'https://www.lab463.com/tracker/app';
/** The download page of the Android app (static, next to the product page). */
const ANDROID_PAGE = 'https://www.lab463.com/tracker/android/';

/**
 * Android: the PositionTracker app is the recommended tracker (one tap to start, switches itself off after the race).
 * "Open in the app" hands this invitation to the app (an intent link; without the app Chrome opens the download page).
 * The app confirms and consents itself, so this works before or instead of the steps below. On a computer: a hint to
 * open the invitation on the phone. Nothing on an iPhone (OwnTracks below).
 */
function AndroidApp({ token }: { token: string }) {
  const { t } = useI18n();
  const ua = navigator.userAgent;
  const android = /Android/i.test(ua);
  const ios = /iPhone|iPad|iPod/i.test(ua) || (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1);
  if (ios) return null;
  const root = `${location.origin}${BASE}`;
  const base = root === PRODUCTION_APP ? '' : `&base=${encodeURIComponent(root)}`;
  const intent = `intent://p?token=${encodeURIComponent(token)}${base}#Intent;scheme=positiontracker;package=com.lab463.positiontracker;`
    + `S.browser_fallback_url=${encodeURIComponent(ANDROID_PAGE)};end`;
  if (!android) return (
    <div className="card stack">
      <h2>{t('Telefón s Androidom?', 'An Android phone?')}</h2>
      <p className="mute">{t('Otvorte túto pozvánku na telefóne (e-mail alebo QR kód nižšie) a použite aplikáciu PositionTracker – sledovanie zapnete jedným ťuknutím a po preteku sa vypne samo.',
        'Open this invitation on the phone (the email or the QR code below) and use the PositionTracker app – one tap starts tracking and it switches itself off after the race.')}</p>
      <div className="row"><a className="btn small ghost" href={ANDROID_PAGE} target="_blank" rel="noopener noreferrer"><span>{t('O aplikácii pre Android', 'About the Android app')}</span></a></div>
    </div>
  );
  return (
    <div className="card stack">
      <h2>{t('Aplikácia PositionTracker (odporúčané)', 'PositionTracker app (recommended)')}</h2>
      <ol>
        <li>{t('Nainštalujte aplikáciu (ak ju ešte nemáte):', 'Install the app (if you do not have it yet):')}
          <div className="row" style={{ margin: '10px 0' }}>
            <a className="btn" href={ANDROID_PAGE}><span>{t('Stiahnuť aplikáciu', 'Download the app')}</span></a>
          </div>
        </li>
        <li>{t('Otvorte v nej túto pozvánku – potvrdenie účasti a súhlas urobíte priamo v aplikácii:', 'Open this invitation in it – you confirm and consent right in the app:')}
          <div className="row" style={{ margin: '10px 0' }}>
            <a className="btn primary" href={intent}><span>{t('Otvoriť v aplikácii', 'Open in the app')}</span></a>
          </div>
        </li>
        <li>{t('Pred štartom ťuknite v aplikácii na „Spustiť sledovanie“. Po skončení preteku sa vypne sama.', 'Before your start tap "Start tracking" in the app. It switches itself off when the race is over.')}</li>
      </ol>
      <p className="mute">{t('Aplikácia nie je v Google Play; inštaluje sa zo stránky lab463.com. Ak s ňou máte problém, použite nižšie aplikáciu OwnTracks.',
        'The app is not on Google Play; it is installed from lab463.com. If it does not work for you, use the OwnTracks app below.')}</p>
    </div>
  );
}

function Step3({ token, deviceToken }: { token: string; deviceToken: string }) {
  const { t, lang } = useI18n();
  const setup = useLoad(() => post<Setup>(`/api/p/${encodeURIComponent(token)}/owntracks-config`, { deviceToken }), [token, deviceToken]);
  const [qr, setQr] = useState<string | null>(null);
  useEffect(() => {
    if (setup.data) QRCode.toDataURL(setup.data.qrPayload, { margin: 1, width: 280, errorCorrectionLevel: 'M' }).then(setQr, () => setQr(null));
  }, [setup.data]);
  return (
    <div className="card stack">
      <h2>3 · {t('Nastavenie telefónu (OwnTracks – iPhone, alebo Android bez aplikácie PositionTracker)', 'Phone setup (OwnTracks – iPhone, or Android without the PositionTracker app)')}</h2>
      <ErrorBanner error={setup.error} />
      {!setup.data ? <Loading /> : (
        <>
          <ol>
            <li>{t('Nainštalujte zadarmo aplikáciu OwnTracks (ak ju ešte nemáte):', 'Install the free OwnTracks app (if you do not have it yet):')}
              <div className="row" style={{ margin: '10px 0', alignItems: 'center', gap: 16 }}>
                <a href="https://apps.apple.com/us/app/owntracks/id692424691" target="_blank" rel="noopener noreferrer" aria-label={t('OwnTracks v App Store (iPhone)', 'OwnTracks on the App Store (iPhone)')}>
                  <img src={`${BASE}/badges/apple-${lang}.svg`} alt={t('Stiahnuť v App Store', 'Download on the App Store')} height={44} style={{ height: 44, width: 'auto', display: 'block' }} />
                </a>
                <a href="https://play.google.com/store/apps/details?id=org.owntracks.android" target="_blank" rel="noopener noreferrer" aria-label={t('OwnTracks v Google Play (Android)', 'OwnTracks on Google Play (Android)')}>
                  <img src={`${BASE}/badges/play-${lang}.png`} alt={t('Stiahnuť z Google Play', 'Get it on Google Play')} height={66} style={{ height: 66, width: 'auto', display: 'block', margin: '-11px -5px' }} />
                </a>
              </div>
            </li>
            <li>{t('Na telefóne otvorte toto tlačidlo, alebo naskenujte QR kód z iného zariadenia a potvrďte import konfigurácie:', 'On the phone open this button, or scan the QR code from another device, and accept the configuration import:')}
              <div className="row" style={{ margin: '10px 0' }}>
                <a className="btn primary" href={setup.data.deepLink}><span>{t('Nastaviť OwnTracks', 'Set up OwnTracks')}</span></a>
              </div>
              {qr && <div className="qr"><img src={qr} alt={t('QR kód nastavenia OwnTracks', 'OwnTracks setup QR code')} width={280} height={280} /></div>}
            </li>
            <li><b>Android:</b> {t('Nastavenia → Aplikácie → OwnTracks → Batéria → Bez obmedzení; Poloha → Povoliť vždy.', 'Settings → Apps → OwnTracks → Battery → Unrestricted; Location → Allow all the time.')}</li>
            <li><b>iOS:</b> {t('Nastavenia → OwnTracks → Poloha → Vždy, Presná poloha zapnutá, Obnovovanie na pozadí zapnuté.', 'Settings → OwnTracks → Location → Always, Precise Location on, Background App Refresh on.')}</li>
            <li>{t('V aplikácii nechajte režim „Move“. Pred štartom skontrolujte, že aplikácia po odoslaní nehlási chybu.', 'Keep the app in "Move" mode. Before the start check that the app shows no error after sending.')}</li>
            <li>{t('Počas preteku nevypínajte telefón ani šetrič batérie. SOS: zavolajte organizátorovi.', 'Do not power off the phone or enable battery saver during the race. SOS: phone the organizer.')}</li>
          </ol>
          <div className="banner">{t('Toto nastavenie obsahuje tajný kľúč vášho zariadenia. Nezdieľajte ho.', 'This setup contains your device\'s secret key. Do not share it.')}</div>
        </>)}
    </div>
  );
}

function Share() {
  const { t } = useI18n();
  const [qr, setQr] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  useEffect(() => { if (open) QRCode.toDataURL(window.location.href, { margin: 1, width: 220 }).then(setQr, () => setQr(null)); }, [open]);
  return (
    <div className="card">
      <button className="btn small ghost" onClick={() => setOpen(!open)}><span>{t('Otvoriť tento odkaz na inom telefóne (QR)', 'Open this link on another phone (QR)')}</span></button>
      {open && qr && <div className="qr" style={{ marginTop: 10 }}><img src={qr} alt="QR" width={220} height={220} /></div>}
    </div>
  );
}
