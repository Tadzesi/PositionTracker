import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import QRCode from 'qrcode';
import { ApiError, BASE, get, post, put } from '../lib/api';
import { formatDateTime } from '../lib/format';
import { useI18n, type Lang } from '../lib/i18n';
import { useLoad } from '../lib/hooks';
import type { LinkView } from '../lib/types';
import { ErrorBanner, Field, Loading, StatusBadge, bannerError } from '../components/Ui';

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
  if (view.error) return <LinkProblem error={view.error} />;
  if (!view.data) return <Loading />;
  return <Flow token={token} view={view.data} reload={view.reload} />;
}

function LinkProblem({ error }: { error: unknown }) {
  const { t } = useI18n();
  const e = error instanceof ApiError ? error : null;
  return (
    <div className="card narrow stack">
      <h2>{t('Odkaz nefunguje', 'This link does not work')}</h2>
      <ErrorBanner error={error} />
      {e?.code === 'link_expired' && <p>{t('Požiadajte organizátora o nový odkaz.', 'Ask the organizer for a new link.')}</p>}
      {e?.code === 'link_superseded' && <p>{t('Použite odkaz z najnovšieho e-mailu od organizátora.', 'Use the link from the most recent email from the organizer.')}</p>}
    </div>
  );
}

function Flow({ token, view, reload }: { token: string; view: LinkView; reload: () => void }) {
  const { t, lang } = useI18n();
  const [deviceToken, setDeviceToken] = useState<string | null>(null);
  const me = view.me;
  const statusLabel = ({ Invited: t('Čaká na potvrdenie', 'Awaiting confirmation'), Confirmed: t('Potvrdené', 'Confirmed'), Declined: t('Odmietnuté', 'Declined'), EmailFailed: t('Čaká na potvrdenie', 'Awaiting confirmation') } as Record<string, string>)[me.status] ?? me.status;
  return (
    <div className="stack narrow">
      <div className="card">
        <h1>{view.event.name}</h1>
        <p className="mute">{view.event.location} · {formatDateTime(view.event.startTime, lang)}</p>
        <p><b>{me.name}</b> · #{me.bib}{me.category ? ` · ${me.category}` : ''} <StatusBadge value={me.status} label={statusLabel} /></p>
      </div>
      <Step1 token={token} view={view} reload={reload} />
      {me.status === 'Confirmed' && <Step2 token={token} onToken={setDeviceToken} />}
      {deviceToken && <Step3 token={token} deviceToken={deviceToken} />}
      <Share />
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

function Step2({ token, onToken }: { token: string; onToken: (t: string) => void }) {
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
      <ul className="mute">
        <li>{t('Organizátor vidí vašu polohu presne a hneď.', 'The organizer sees your position exactly and immediately.')}</li>
        <li>{t('Diváci ju vidia oneskorene a len počas pretekov.', 'Spectators see it delayed, and only during the race.')}</li>
        <li>{t('Polohy sa uchovávajú 30 dní po skončení podujatia, potom sa zmažú. Vymazanie môžete kedykoľvek požiadať u organizátora.', 'Positions are kept for 30 days after the event, then deleted. You can ask the organizer to delete them at any time.')}</li>
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

function Step3({ token, deviceToken }: { token: string; deviceToken: string }) {
  const { t, lang } = useI18n();
  const setup = useLoad(() => post<Setup>(`/api/p/${encodeURIComponent(token)}/owntracks-config`, { deviceToken }), [token, deviceToken]);
  const [qr, setQr] = useState<string | null>(null);
  useEffect(() => {
    if (setup.data) QRCode.toDataURL(setup.data.qrPayload, { margin: 1, width: 280, errorCorrectionLevel: 'M' }).then(setQr, () => setQr(null));
  }, [setup.data]);
  return (
    <div className="card stack">
      <h2>3 · {t('Nastavenie telefónu (OwnTracks)', 'Phone setup (OwnTracks)')}</h2>
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
