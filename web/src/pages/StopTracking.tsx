import { useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';
import { useI18n } from '../lib/i18n';
import { StopTrackingPanel } from '../components/StopTrackingPanel';

/** Public page linked from the "tracking still on" email: /stop-tracking?lang=sk|en. No token, no data. */
export default function StopTracking() {
  const [params] = useSearchParams();
  const { lang, setLang } = useI18n();
  const wanted = params.get('lang');
  // The page opens in the language of the email; the toggle in the header still works afterwards.
  useEffect(() => { if ((wanted === 'sk' || wanted === 'en') && wanted !== lang) setLang(wanted); }, [wanted]); // eslint-disable-line react-hooks/exhaustive-deps
  return <div className="narrow stack"><StopTrackingPanel /></div>;
}
