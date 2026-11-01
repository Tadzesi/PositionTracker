import { useEffect, type ReactNode } from 'react';
import { Link, useParams } from 'react-router-dom';
import { get, setActAs } from '../lib/api';
import { useLoad } from '../lib/hooks';
import { useI18n } from '../lib/i18n';
import type { AdminOrganizer } from '../lib/types';
import { useSession } from './Layout';

/**
 * The administrator's route to an organizer's event: while it is mounted, every organizer request carries the organizer's id,
 * so the whole organizer UI works on that event. A banner says whose event it is; a non-administrator is sent away.
 */
export function ActAs({ children }: { children: ReactNode }) {
  const { oid } = useParams();
  const { me } = useSession();
  const { t } = useI18n();
  // set during render so the children's first requests already carry it; removed again when the route is left
  setActAs(me?.isAdmin ? oid ?? null : null);
  useEffect(() => () => setActAs(null), []);
  const organizers = useLoad(() => (me?.isAdmin ? get<AdminOrganizer[]>('/api/admin/organizers') : Promise.resolve([] as AdminOrganizer[])), [me?.isAdmin]);
  if (!me?.isAdmin) return <p className="note" role="alert">{t('Táto stránka je len pre správcu.', 'This page is for the administrator only.')}</p>;
  const owner = organizers.data?.find((o) => o.organizerId === oid);
  return (
    <>
      <div className="note admin-banner" role="status">
        <b>{t('Správca', 'Administrator')}</b>{' · '}
        {t('upravujete podujatie organizátora', 'you are editing the event of')} <b>{owner ? `${owner.name} (${owner.email})` : '…'}</b>
        {' · '}<Link to="/admin">{t('späť na prehľad', 'back to the overview')}</Link>
      </div>
      {children}
    </>
  );
}
