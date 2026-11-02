import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { useI18n } from '../lib/i18n';
import { STOP_DEEP_LINK } from '../owntracksStop';

/** After the event: one tap (or a QR scan) switches OwnTracks to quiet mode; the manual way as a fallback. */
export function StopTrackingPanel() {
  const { t } = useI18n();
  const [qr, setQr] = useState<string | null>(null);
  useEffect(() => {
    QRCode.toDataURL(STOP_DEEP_LINK, { margin: 1, width: 280, errorCorrectionLevel: 'M' }).then(setQr, () => setQr(null));
  }, []);
  return (
    <div className="card stack">
      <h2>{t('Vypnite sledovanie polohy', 'Switch off location tracking')}</h2>
      <p>{t('Podujatie sa skončilo. Polohu už neukladáme. Vypnite OwnTracks, aby telefón zbytočne nemíňal batériu a dáta.',
        'The event has finished. We no longer store your location. Switch off OwnTracks so your phone does not waste battery and data.')}</p>
      <div className="row">
        <a className="btn primary" href={STOP_DEEP_LINK}><span>{t('Vypnúť sledovanie v OwnTracks', 'Switch off tracking in OwnTracks')}</span></a>
      </div>
      {qr && (
        <div className="stack">
          <p className="mute">{t('Na počítači? Naskenujte kód telefónom.', 'On a computer? Scan the code with your phone.')}</p>
          <div className="qr"><img src={qr} alt={t('QR kód na vypnutie sledovania', 'QR code to switch off tracking')} width={280} height={280} /></div>
        </div>)}
      <p className="mute">{t('Alebo v OwnTracks prepnite režim na Quiet (Tichý), prípadne aplikáciu odinštalujte.', 'Or switch OwnTracks to Quiet mode, or uninstall the app.')}</p>
    </div>
  );
}
