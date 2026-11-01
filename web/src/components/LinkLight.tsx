import { useI18n } from '../lib/i18n';
import type { LinkHealth } from '../lib/types';

/** "4 s", "2 min", "1 h 05 min": how long ago something was, from a number of seconds. */
export function agoText(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  return m < 60 ? `${m} min` : `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min`;
}

/** The traffic light's words for one participant: a short title and what to do about it. */
export function useLinkText() {
  const { t } = useI18n();
  return (h: LinkHealth | null | undefined): { title: string; hints: string[] } => {
    if (!h) return { title: t('Spojenie sa zatiaľ nedá zistiť', 'The connection cannot be checked yet'), hints: [t('Údaje sa zobrazia, keď podujatie začne prijímať polohy.', 'This shows once the event starts receiving positions.')] };
    if (h.level === 'waiting') return { title: t('Čakáme na prvé údaje', 'Waiting for the first data'), hints: [t('Otvorte OwnTracks a pošlite polohu (tlačidlo na odoslanie teraz).', 'Open OwnTracks and send a position (the send-now button).')] };
    if (h.level === 'silent') {
      const since = h.lastBatchAgoSeconds == null
        ? t('Zatiaľ nič neprišlo.', 'Nothing has arrived yet.')
        : t(`Server od vás nič nedostal už ${agoText(h.lastBatchAgoSeconds)}.`, `The server has received nothing from you for ${agoText(h.lastBatchAgoSeconds)}.`);
      return { title: since, hints: [
        t('Otvorte aplikáciu OwnTracks a nechajte obrazovku zapnutú.', 'Open the OwnTracks app and keep the screen on.'),
        t('Poloha musí byť povolená „vždy“ a šetrič batérie vypnutý.', 'Location must be allowed "always" and battery saver off.'),
        t('Ak ste dostali nový odkaz, použite ten z posledného e-mailu a nastavenie telefónu urobte znova.', 'If you got a new link, use the one from the latest email and set the phone up again.'),
      ] };
    }
    const hints: string[] = [];
    for (const p of h.problems) {
      if (p === 'clock') hints.push(t(`Hodiny telefónu idú o ${Math.round(h.clockAheadSeconds ?? 0)} s popredu. Zapnite automatický čas (z GPS alebo z internetu).`, `The phone clock is ${Math.round(h.clockAheadSeconds ?? 0)} s ahead. Turn on automatic time (from GPS or the internet).`));
      if (p === 'accuracy') hints.push(t(`GPS je nepresná${h.accuracyM != null ? ` (${Math.round(h.accuracyM)} m)` : ''}. Choďte na otvorené priestranstvo.`, `GPS accuracy is poor${h.accuracyM != null ? ` (${Math.round(h.accuracyM)} m)` : ''}. Go to an open area.`));
      if (p === 'lowBattery') hints.push(t(`Batéria je nízka${h.batteryPct != null ? ` (${Math.round(h.batteryPct)} %)` : ''}. Pripojte powerbanku a nechajte aplikáciu bežať.`, `The battery is low${h.batteryPct != null ? ` (${Math.round(h.batteryPct)} %)` : ''}. Connect a power bank and keep the app running.`));
      if (p === 'rejected') hints.push(t('Server údaje dostáva, ale polohu neukladá. Reštartujte OwnTracks a skontrolujte polohu.', 'The server receives data but stores no position. Restart OwnTracks and check location.'));
    }
    if (h.level === 'warning') return { title: t('Údaje chodia, ale s problémom', 'Data are coming, but with a problem'), hints };
    if (h.earlyOnly) return { title: t('Spojenie funguje', 'The connection works'), hints: [t('Polohy sa začnú ukladať hodinu pred štartom.', 'Positions are stored from an hour before the start.')] };
    const ago = h.lastPositionAgoSeconds != null ? t(` Naposledy pred ${agoText(h.lastPositionAgoSeconds)}.`, ` Last one ${agoText(h.lastPositionAgoSeconds)} ago.`) : '';
    return { title: t('Údaje odchádzajú a sú v systéme.', 'Your data are leaving the phone and are in the system.') + ago, hints: [t('Diváci vás vidia s oneskorením.', 'Spectators see you with a delay.')] };
  };
}

/** The small light (a dot with a label) for lists. */
export function LinkDot({ health }: { health: LinkHealth | null | undefined }) {
  const { t } = useI18n();
  const level = health?.level ?? 'none';
  const label = ({ good: t('Funguje', 'Working'), warning: t('Problém', 'Problem'), silent: t('Bez dát', 'No data'), waiting: t('Čaká', 'Waiting'), none: '–' } as Record<string, string>)[level];
  const ago = health?.lastBatchAgoSeconds != null ? ` · ${agoText(health.lastBatchAgoSeconds)}` : '';
  return <span className="link-dot"><span className={`dot link-${level}`} aria-hidden="true" /> {label}{level === 'none' ? '' : ago}</span>;
}
