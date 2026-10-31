import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { post } from '../lib/api';
import { useI18n } from '../lib/i18n';
import { ErrorBanner, Field, bannerError } from '../components/Ui';
import { useSession } from '../components/Layout';

export function Home() {
  const { t } = useI18n();
  const { me } = useSession();
  return (
    <div className="hero">
      <h1>{t('Sledovanie pretekov naživo', 'Live race tracking')}</h1>
      <p className="mute" style={{ maxWidth: 640, fontSize: '1.15rem' }}>
        {t('Organizujte beh alebo cyklistické preteky, pozvite účastníkov e-mailom a sledujte ich na mape v reálnom čase. Účastníci nepotrebujú účet – stačí aplikácia OwnTracks.',
          'Organize a run or a cycling race, invite participants by email and follow them on the map in real time. Participants need no account – just the OwnTracks app.')}
      </p>
      <div className="row" style={{ marginTop: 20 }}>
        {me
          ? <Link className="btn primary" to="/org"><span>{t('Moje podujatia', 'My events')}</span></Link>
          : <>
              <Link className="btn primary" to="/account/signup"><span>{t('Vytvoriť účet organizátora', 'Create an organizer account')}</span></Link>
              <Link className="btn" to="/account/signin"><span>{t('Prihlásiť sa', 'Sign in')}</span></Link>
            </>}
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
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await post('/api/account/signup', { name, email, language: lang }); setSent(true); } catch (x) { setError(x); }
    setBusy(false);
  };
  if (sent) return <Sent email={email} />;
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
      <button className="btn primary" disabled={busy}><span>{t('Vytvoriť účet', 'Create account')}</span></button>
    </form>
  );
}

function Sent({ email }: { email: string }) {
  const { t } = useI18n();
  return (
    <div className="card narrow">
      <h2>{t('Skontrolujte e-mail', 'Check your email')}</h2>
      <p>{t(`Ak je adresa ${email} platná, poslali sme na ňu odkaz. Platí krátko a dá sa použiť raz.`,
        `If ${email} is valid we have sent a link to it. It is short-lived and works once.`)}</p>
    </div>
  );
}

export function SignIn() {
  const { t } = useI18n();
  const [params] = useSearchParams();
  const token = params.get('token');
  const { refresh } = useSession();
  const navigate = useNavigate();
  const [email, setEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [sent, setSent] = useState(false);

  if (token) {
    const redeem = async () => {
      setBusy(true); setError(null);
      try { await post('/api/account/signin/redeem', { token }); await refresh(); navigate('/org'); } catch (x) { setError(x); setBusy(false); }
    };
    return (
      <div className="card narrow stack">
        <h2>{t('Prihlásenie', 'Sign in')}</h2>
        <p className="mute">{t('Potvrďte prihlásenie na tomto zariadení.', 'Confirm signing in on this device.')}</p>
        <ErrorBanner error={error} />
        <button className="btn primary" onClick={redeem} disabled={busy}><span>{t('Prihlásiť sa', 'Sign in')}</span></button>
      </div>
    );
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true); setError(null);
    try { await post('/api/account/signin', { email }); setSent(true); } catch (x) { setError(x); }
    setBusy(false);
  };
  if (sent) return <Sent email={email} />;
  return (
    <form className="card narrow stack" onSubmit={submit}>
      <h2>{t('Prihlásenie', 'Sign in')}</h2>
      <ErrorBanner error={bannerError(error, ['email'])} />
      <Field label="E-mail" name="email" error={error}>
        <input id="email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="email" required />
      </Field>
      <button className="btn primary" disabled={busy}><span>{t('Poslať prihlasovací odkaz', 'Send a sign-in link')}</span></button>
      <p className="mute">{t('Nemáte účet?', 'No account yet?')} <Link to="/account/signup">{t('Vytvoriť', 'Create one')}</Link></p>
    </form>
  );
}

export function ConfirmEmail() {
  const { t } = useI18n();
  const [params] = useSearchParams();
  const token = params.get('token') ?? '';
  const { refresh } = useSession();
  const [state, setState] = useState<'idle' | 'busy' | 'done'>('idle');
  const [error, setError] = useState<unknown>(null);
  const confirm = async () => {
    setState('busy'); setError(null);
    try { await post('/api/account/confirm', { token }); await refresh(); setState('done'); } catch (x) { setError(x); setState('idle'); }
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
