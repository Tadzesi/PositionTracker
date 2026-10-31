import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ApiError } from './api';

export type Lang = 'sk' | 'en';

interface I18n {
  lang: Lang;
  setLang: (l: Lang) => void;
  /** The text in the current language: every user-facing string is given in both (Slovak first). */
  t: (sk: string, en: string) => string;
  /** The message of an error in the current language. */
  err: (e: unknown) => string;
}

const Ctx = createContext<I18n | null>(null);

function initialLang(): Lang {
  try {
    const stored = localStorage.getItem('pt-lang');
    if (stored === 'sk' || stored === 'en') return stored;
  } catch { /* storage blocked */ }
  return typeof navigator !== 'undefined' && navigator.language?.toLowerCase().startsWith('sk') ? 'sk' : 'en';
}

export function I18nProvider({ children, initial }: { children: ReactNode; initial?: Lang }) {
  const [lang, setLangState] = useState<Lang>(initial ?? initialLang());
  useEffect(() => { document.documentElement.lang = lang; }, [lang]);
  const setLang = useCallback((l: Lang) => {
    setLangState(l);
    try { localStorage.setItem('pt-lang', l); } catch { /* storage blocked */ }
  }, []);
  const value = useMemo<I18n>(() => ({
    lang, setLang,
    t: (sk, en) => (lang === 'sk' ? sk : en),
    err: (e) => (e instanceof ApiError ? e.text[lang] : lang === 'sk' ? 'Nastala neočakávaná chyba.' : 'An unexpected error occurred.'),
  }), [lang, setLang]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useI18n(): I18n {
  const v = useContext(Ctx);
  if (!v) throw new Error('I18nProvider missing');
  return v;
}

export type Theme = 'auto' | 'dark' | 'light';

export function currentTheme(): Theme {
  try {
    const t = localStorage.getItem('pt-theme');
    if (t === 'dark' || t === 'light') return t;
  } catch { /* storage blocked */ }
  return 'auto';
}

export function applyTheme(theme: Theme) {
  if (theme === 'auto') document.documentElement.removeAttribute('data-theme');
  else document.documentElement.setAttribute('data-theme', theme);
  try {
    if (theme === 'auto') localStorage.removeItem('pt-theme');
    else localStorage.setItem('pt-theme', theme);
  } catch { /* storage blocked */ }
}
