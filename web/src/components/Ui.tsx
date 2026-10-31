import type { ReactNode } from 'react';
import { ApiError } from '../lib/api';
import { useI18n } from '../lib/i18n';

export function ErrorBanner({ error }: { error: unknown }) {
  const { err } = useI18n();
  if (!error) return null;
  return <div className="banner error" role="alert">{err(error)}</div>;
}

export function Loading() {
  const { t } = useI18n();
  return <p className="mute" role="status">{t('Načítavam…', 'Loading…')}</p>;
}

/** A form field with its label and the server's message for this field. */
export function Field({ label, name, error, children }: { label: string; name: string; error?: unknown; children: ReactNode }) {
  const { err } = useI18n();
  const mine = error instanceof ApiError && error.field === name ? err(error) : null;
  return (
    <div className="field">
      <label htmlFor={name}>{label}</label>
      {children}
      {mine && <div className="err" role="alert">{mine}</div>}
    </div>
  );
}

/** The error to show as a banner: not the one a Field already shows. */
export function bannerError(error: unknown, fields: string[]): unknown {
  return error instanceof ApiError && error.field && fields.includes(error.field) ? null : error;
}

export function StatusBadge({ value, label }: { value: string; label?: string }) {
  return <span className={`status ${value}`}>{label ?? value}</span>;
}

export function EmailModeNotice({ mode }: { mode: string | undefined }) {
  const { t } = useI18n();
  if (mode !== 'simulated') return null;
  return (
    <div className="banner" role="status">
      {t('E-maily sa len simulujú – nič sa neodosiela. Nastavte SMTP server, aby pozvánky skutočne odišli.',
        'Emails are simulated – nothing is sent. Configure the SMTP server so invitations really go out.')}
    </div>
  );
}
