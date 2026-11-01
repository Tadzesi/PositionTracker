import { createContext, useCallback, useContext, useEffect, useMemo, useState, Suspense, type ReactNode } from 'react';
import { Link, NavLink, Outlet, useNavigate } from 'react-router-dom';
import { ApiError, get, post } from '../lib/api';
import { applyTheme, effectiveTheme, useI18n } from '../lib/i18n';
import type { Me } from '../lib/types';
import { Loading } from './Ui';

interface Session {
  me: Me | null;
  ready: boolean;
  refresh: () => Promise<void>;
  signOut: () => Promise<void>;
}

const SessionCtx = createContext<Session | null>(null);

export function SessionProvider({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<Me | null>(null);
  const [ready, setReady] = useState(false);
  const refresh = useCallback(async () => {
    try {
      setMe((await get<Me | undefined>('/api/account/session')) ?? null);
    } catch (e) {
      if (!(e instanceof ApiError)) throw e;
      setMe(null);
    }
    setReady(true);
  }, []);
  useEffect(() => { refresh(); }, [refresh]);
  const signOut = useCallback(async () => {
    try { await post('/api/account/signout'); } catch { /* the cookie is cleared by the server or already invalid */ }
    setMe(null);
  }, []);
  const value = useMemo(() => ({ me, ready, refresh, signOut }), [me, ready, refresh, signOut]);
  return <SessionCtx.Provider value={value}>{children}</SessionCtx.Provider>;
}

const BEAT_MS = 30_000;
const IDLE_MS = 90_000;

/**
 * Tells the server every 30 s that a signed-in organizer has the page open, visible and in use (a click, key or scroll in the
 * last 90 s), so the administrator can see roughly how long people work with the application. Only a signed-in organizer is
 * measured; nothing is sent for spectators or participants.
 */
export function useActivityBeat(signedIn: boolean) {
  useEffect(() => {
    if (!signedIn) return;
    let lastUse = Date.now();
    const used = () => { lastUse = Date.now(); };
    const events = ['pointerdown', 'keydown', 'scroll', 'touchstart'] as const;
    events.forEach((n) => window.addEventListener(n, used, { passive: true }));
    const timer = window.setInterval(() => {
      if (document.visibilityState !== 'visible' || Date.now() - lastUse > IDLE_MS) return;
      post('/api/account/activity').catch(() => { /* a missed beat only makes the measured time a little shorter */ });
    }, BEAT_MS);
    return () => { window.clearInterval(timer); events.forEach((n) => window.removeEventListener(n, used)); };
  }, [signedIn]);
}

export function useSession(): Session {
  const v = useContext(SessionCtx);
  if (!v) throw new Error('SessionProvider missing');
  return v;
}

/** Language and appearance as two explicit choices each (the active one is filled), not a button that cycles through states. */
export function Toggles() {
  const { lang, setLang, t } = useI18n();
  const [theme, setTheme] = useState<'light' | 'dark'>(effectiveTheme());
  useEffect(() => {
    const media = window.matchMedia?.('(prefers-color-scheme: dark)');
    const onChange = () => setTheme(effectiveTheme());
    media?.addEventListener?.('change', onChange);
    return () => media?.removeEventListener?.('change', onChange);
  }, []);
  const choose = (next: 'light' | 'dark') => { applyTheme(next); setTheme(next); };
  return (
    <div className="toggles">
      <div className="seg" role="group" aria-label={t('Jazyk', 'Language')}>
        <button type="button" lang="sk" aria-pressed={lang === 'sk'} onClick={() => setLang('sk')}>SK</button>
        <button type="button" lang="en" aria-pressed={lang === 'en'} onClick={() => setLang('en')}>EN</button>
      </div>
      <div className="seg" role="group" aria-label={t('Vzhľad stránky', 'Page appearance')}>
        <button type="button" aria-pressed={theme === 'light'} aria-label={t('Svetlý režim', 'Light mode')} title={t('Svetlý režim', 'Light mode')} onClick={() => choose('light')}>☀<span className="lbl"> {t('Svetlý', 'Light')}</span></button>
        <button type="button" aria-pressed={theme === 'dark'} aria-label={t('Tmavý režim', 'Dark mode')} title={t('Tmavý režim', 'Dark mode')} onClick={() => choose('dark')}>☾<span className="lbl"> {t('Tmavý', 'Dark')}</span></button>
      </div>
    </div>
  );
}

export default function Layout() {
  const { t } = useI18n();
  const { me, signOut } = useSession();
  const navigate = useNavigate();
  useActivityBeat(!!me);
  return (
    <>
      <header className="topbar">
        <div className="wrap">
          <Link to={me ? '/org' : '/'} className="brand">Position<b>Tracker</b></Link>
          <span className="spacer" />
          {me ? (
            <>
              <NavLink to="/org" className="btn small ghost"><span>{t('Moje podujatia', 'My events')}</span></NavLink>
              {me.isAdmin && <NavLink to="/admin" className="btn small ghost"><span>{t('Správa', 'Admin')}</span></NavLink>}
              <span className="who" title={me.email}>{me.name}</span>
              <button className="btn small ghost" onClick={async () => { await signOut(); navigate('/'); }}><span>{t('Odhlásiť', 'Sign out')}</span></button>
            </>
          ) : (
            <Link to="/account/signin" className="btn small ghost"><span>{t('Prihlásiť', 'Sign in')}</span></Link>
          )}
          <Toggles />
        </div>
      </header>
      <main><div className="wrap"><Suspense fallback={<Loading />}><Outlet /></Suspense></div></main>
    </>
  );
}
