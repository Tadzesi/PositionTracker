import { useEffect, useState, type FormEvent } from 'react';
import { Link, Navigate, useNavigate, useSearchParams } from 'react-router-dom';
import { post } from '../lib/api';
import { useI18n } from '../lib/i18n';
import { ErrorBanner, Field, Loading, bannerError } from '../components/Ui';
import { useSession } from '../components/Layout';

export function Home() {
  const { t } = useI18n();
  const { me, ready } = useSession();
  if (!ready) return <Loading />;
  // A signed-in organizer has no use for the landing page: straight to the events.
  if (me) return <Navigate to="/org" replace />;
  return (
    <div className="hero">
      <h1>{t('Sledovanie pretekov naživo', 'Live race tracking')}</h1>
      <p className="mute" style={{ maxWidth: 640, fontSize: '1.15rem' }}>
        {t('Organizujte beh alebo cyklistické preteky, pozvite účastníkov e-mailom a sledujte ich na mape v reálnom čase. Účastníci nepotrebujú účet – stačí aplikácia OwnTracks.',
          'Organize a run or a cycling race, invite participants by email and follow them on the map in real time. Participants need no account – just the OwnTracks app.')}
      </p>
      <div className="row" style={{ marginTop: 20 }}>
        <Link className="btn primary" to="/account/signup"><span>{t('Vytvoriť účet organizátora', 'Create an organizer account')}</span></Link>
        <Link className="btn" to="/account/signin"><span>{t('Prihlásiť sa', 'Sign in')}</span></Link>
      </div>
      <p className="dim" style={{ marginTop: 28 }}>
        {t('Divák? Otvorte verejný odkaz, ktorý vám poslal organizátor.', 'A spectator? Open the public link the organizer gave you.')}
      </p>
    </div>
  );
}

export function SignUp() {
  const { t, lang } = useI18n();
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [sent, setSent] = useState(false);
  const request = () => post('/api/account/signup', { name, email, language: lang });
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await request(); setSent(true); } catch (x) { setError(x); }
    setBusy(false);
  };
  if (sent) return <Sent email={email} resend={async () => { await request(); }} onChangeAddress={() => setSent(false)} />;
  return (
    <form className="card narrow stack" onSubmit={submit}>
      <h2>{t('Účet organizátora', 'Organizer account')}</h2>
      <p className="mute">{t('Bez hesla: prihlasujete sa jednorazovým odkazom z e-mailu.', 'No password: you sign in with a one-time link sent by email.')}</p>
      <ErrorBanner error={bannerError(error, ['name', 'email'])} />
      <Field label={t('Meno', 'Name')} name="name" error={error}>
        <input id="name" value={name} onChange={(e) => setName(e.target.value)} autoComplete="name" required />
      </Field>
      <Field label="E-mail" name="email" error={error}>
        <input id="email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
      </Field>
      <p className="mute">{t('Vytvorením účtu beriete na vedomie ', 'By creating an account you acknowledge the ')}<Link to="/privacy">{t('zásady ochrany osobných údajov', 'privacy notice')}</Link>.</p>
      <button className="btn primary" disabled={busy}><span>{t('Vytvoriť účet', 'Create account')}</span></button>
    </form>
  );
}

const RESEND_COOLDOWN_S = 30;

function Sent({ email, resend, onChangeAddress }: { email: string; resend: () => Promise<void>; onChangeAddress: () => void }) {
  const { t } = useI18n();
  const [wait, setWait] = useState(RESEND_COOLDOWN_S);
  const [again, setAgain] = useState(false);
  const [error, setError] = useState<unknown>(null);
  useEffect(() => {
    if (wait <= 0) return;
    const id = window.setTimeout(() => setWait((w) => w - 1), 1000);
    return () => window.clearTimeout(id);
  }, [wait]);
  const send = async () => {
    setError(null); setAgain(false); setWait(RESEND_COOLDOWN_S);
    try { await resend(); setAgain(true); } catch (x) { setError(x); }
  };
  return (
    <div className="card narrow stack">
      <h2>{t('Skontrolujte e-mail', 'Check your email')}</h2>
      <p>{t(`Ak je adresa ${email} platná, poslali sme na ňu odkaz. Platí krátko a dá sa použiť raz.`,
        `If ${email} is valid we have sent a link to it. It is short-lived and works once.`)}</p>
      <p className="mute">{t('Otvorte ho v tomto prehliadači. Ak e-mail nevidíte, pozrite aj spam.', 'Open it in this browser. If you do not see the email, check your spam folder too.')}</p>
      <ErrorBanner error={error} />
      {again && <div className="banner ok" role="status">{t('Odkaz sme poslali znova.', 'We sent the link again.')}</div>}
      <div className="row">
        <button type="button" className="btn" onClick={send} disabled={wait > 0}>
          <span>{wait > 0 ? t(`Poslať znova (${wait} s)`, `Send again (${wait} s)`) : t('Poslať znova', 'Send again')}</span>
        </button>
        <button type="button" className="btn ghost" onClick={onChangeAddress}><span>{t('Zmeniť adresu', 'Change address')}</span></button>
      </div>
    </div>
  );
}

export function SignIn() {
  const { t } = useI18n();
  const [params] = useSearchParams();
  const token = params.get('token');
  const { me, refresh } = useSession();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [tokenError, setTokenError] = useState<unknown>(null);
  const [sent, setSent] = useState(false);

  // Already signed in (this is also the moment the session appears after redeeming): nothing to ask, go to the events.
  if (me) return <Navigate to="/org" replace />;

  const redeem = async () => {
    setBusy(true); setTokenError(null);
    try { await post('/api/account/signin/redeem', { token }); await refresh(); navigate('/org', { replace: true }); } catch (x) { setTokenError(x); setBusy(false); }
  };
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await post('/api/account/signin', { email }); setSent(true); } catch (x) { setError(x); }
    setBusy(false);
  };

  if (sent) return <Sent email={email} resend={async () => { await post('/api/account/signin', { email }); }} onChangeAddress={() => setSent(false)} />;
  const form = (
    <form className="card narrow stack" onSubmit={submit}>
      <h2>{token ? t('Potrebujete nový odkaz?', 'Need a new link?') : t('Prihlásenie', 'Sign in')}</h2>
      <ErrorBanner error={bannerError(error, ['email'])} />
      <Field label="E-mail" name="email" error={error}>
        <input id="email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
      </Field>
      <button className="btn primary" disabled={busy}><span>{t('Poslať prihlasovací odkaz', 'Send a sign-in link')}</span></button>
      <p className="mute">{t('Nemáte účet?', 'No account yet?')} <Link to="/account/signup">{t('Vytvoriť', 'Create one')}</Link></p>
    </form>
  );
  if (!token) return form;
  return (
    <div className="stack">
      <div className="card narrow stack">
        <h2>{t('Vitajte späť', 'Welcome back')}</h2>
        <p className="mute">{t('Jedným klikom sa prihlásite na tomto zariadení a otvoríte svoje podujatia.', 'One click signs you in on this device and opens your events.')}</p>
        <ErrorBanner error={tokenError} />
        <button className="btn primary" onClick={redeem} disabled={busy} autoFocus><span>{t('Pokračovať do mojich podujatí', 'Continue to my events')}</span></button>
      </div>
      {tokenError != null && form}
    </div>
  );
}

export function ConfirmEmail() {
  const { t } = useI18n();
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [state, setState] = useState<'idle' | 'busy' | 'done'>('idle');
  const [error, setError] = useState<unknown>(null);
  const confirm = async () => {
    setState('busy'); setError(null);
    try { const r = await post<{ signedIn: boolean }>('/api/account/confirm', { token }); await refresh(); if (r?.signedIn) { navigate('/org'); return; } setState('done'); } catch (x) { setError(x); setState('idle'); }
  };
  return (
    <div className="card narrow stack">
      <h2>{t('Potvrdenie e-mailu', 'Confirm your email')}</h2>
      <ErrorBanner error={error} />
      {state === 'done'
        ? <>
            <div className="banner ok">{t('E-mail je potvrdený.', 'Your email is confirmed.')}</div>
            <Link className="btn primary" to="/account/signin"><span>{t('Pokračovať na prihlásenie', 'Continue to sign in')}</span></Link>
          </>
        : <button className="btn primary" onClick={confirm} disabled={state === 'busy' || !token}><span>{t('Potvrdiť e-mail', 'Confirm email')}</span></button>}
    </div>
  );
}
