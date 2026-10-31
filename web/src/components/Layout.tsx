import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { Link, Outlet, useNavigate } from 'react-router-dom';
import { ApiError, get, post } from '../lib/api';
import { applyTheme, currentTheme, useI18n, type Theme } from '../lib/i18n';
import type { Me } from '../lib/types';

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
      setMe(await get<Me>('/api/account/me'));
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

export function useSession(): Session {
  const v = useContext(SessionCtx);
  if (!v) throw new Error('SessionProvider missing');
  return v;
}

export function Toggles() {
  const { lang, setLang, t } = useI18n();
  const [theme, setTheme] = useState<Theme>(currentTheme());
  const next = () => {
    const order: Theme[] = ['auto', 'dark', 'light'];
    const n = order[(order.indexOf(theme) + 1) % order.length];
    setTheme(n);
    applyTheme(n);
  };
  const label = theme === 'auto' ? t('Auto', 'Auto') : theme === 'dark' ? t('Tmavá', 'Dark') : t('Svetlá', 'Light');
  return (
    <div className="toggles">
      <button className="btn small ghost" onClick={() => setLang(lang === 'sk' ? 'en' : 'sk')} aria-label={t('Jazyk', 'Language')}>
        <span>{lang === 'sk' ? 'EN' : 'SK'}</span>
      </button>
      <button className="btn small ghost" onClick={next} aria-label={t('Téma', 'Theme')} title={t('Téma', 'Theme')}>
        <span>{label}</span>
      </button>
    </div>
  );
}

export default function Layout() {
  const { t } = useI18n();
  const { me, signOut } = useSession();
  const navigate = useNavigate();
  return (
    <>
      <header className="topbar">
        <div className="wrap">
          <Link to="/" className="brand">Position<b>Tracker</b></Link>
          <span className="spacer" />
          {me ? (
            <>
              <Link to="/org" className="btn small ghost"><span>{t('Moje podujatia', 'My events')}</span></Link>
              <button className="btn small ghost" onClick={async () => { await signOut(); navigate('/'); }}><span>{t('Odhlásiť', 'Sign out')}</span></button>
            </>
          ) : (
            <Link to="/account/signin" className="btn small ghost"><span>{t('Prihlásiť', 'Sign in')}</span></Link>
          )}
          <Toggles />
        </div>
      </header>
      <main><div className="wrap"><Outlet /></div></main>
    </>
  );
}
