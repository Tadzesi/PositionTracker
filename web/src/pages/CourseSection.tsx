import { useEffect, useMemo, useState } from 'react';
import { get, put } from '../lib/api';
import { useI18n } from '../lib/i18n';
import { useLoad } from '../lib/hooks';
import { nearestDistance, parseGpx, positionAt, type ParsedGpx, type Pt } from '../lib/gpx';
import type { CoursePreview, ManageView } from '../lib/types';
import { ErrorBanner, Field } from '../components/Ui';
import MapView from '../components/MapView';
import ElevationProfile from '../components/ElevationProfile';
import { useManage } from './EventLayout';

interface Row { name: string; km: string }

const toRows = (event: ManageView): Row[] =>
  (event.course?.checkpoints ?? []).map((c) => ({ name: c.name, km: String(Math.round(c.lapDistanceM) / 1000) }));
const kmValue = (km: string) => Number(km.trim().replace(',', '.'));
const toBase64 = (buffer: ArrayBuffer) => {
  let s = '';
  const bytes = new Uint8Array(buffer);
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
};

/** Course card: stats, map, elevation profile, GPX upload (with a preview of the file), laps, delay and checkpoints placed by clicking. */
export default function CourseSection({ disabled }: { disabled: boolean }) {
  const { event, setEvent } = useManage();
  const { t } = useI18n();
  const c = event.course;
  const [file, setFile] = useState<File | null>(null);
  const [parsed, setParsed] = useState<ParsedGpx | null>(null);
  const [fileProblem, setFileProblem] = useState<string | null>(null);
  const [laps, setLaps] = useState(event.laps);
  const [delay, setDelay] = useState(event.spectatorDelaySeconds);
  const [rows, setRows] = useState<Row[]>(() => toRows(event));
  const [saveError, setSaveError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  // the saved course: geometry and elevation for the map and the profile (reloaded when a new GPX was stored)
  const saved = useLoad(() => (c ? get<CoursePreview>(`/api/events/${event.eventId}/course-preview`) : Promise.resolve(null)), [event.eventId, c?.uploadedAt]);

  // the line and the lap length shown: the chosen file (before it is uploaded) or the saved course
  const pending = parsed && file ? parsed : null;
  const line: Pt[] = pending ? pending.points : (saved.data?.points as Pt[] | undefined) ?? [];
  const lapLengthM = pending ? pending.lengthM : saved.data?.lapLengthM ?? c?.lapLengthM ?? 0;
  const hasElevation = pending ? pending.hasElevation : saved.data?.hasElevation ?? null;
  const dirty = !!file
    || laps !== event.laps || delay !== event.spectatorDelaySeconds
    || JSON.stringify(rows.map((r) => [r.name.trim(), kmValue(r.km)])) !== JSON.stringify(toRows(event).map((r) => [r.name.trim(), kmValue(r.km)]));

  const chosen = (f: File | null) => {
    setFile(f); setParsed(null); setFileProblem(null);
    if (!f) return;
    if (f.size > 1024 * 1024) { setFileProblem(t('Súbor je väčší ako 1 MB.', 'The file is larger than 1 MB.')); return; }
    f.text().then((text) => {
      const p = parseGpx(text);
      if (!p) setFileProblem(t('Súbor nie je platný GPX s aspoň dvoma bodmi trasy.', 'The file is not a valid GPX with at least two track points.'));
      else setParsed(p);
    }, () => setFileProblem(t('Súbor sa nepodarilo prečítať.', 'The file could not be read.')));
  };

  // after a save the server's checkpoints (sorted) replace the rows
  const serverKey = JSON.stringify([event.laps, event.spectatorDelaySeconds, c?.checkpoints, c?.uploadedAt]);
  useEffect(() => { setRows(toRows(event)); setLaps(event.laps); setDelay(event.spectatorDelaySeconds); },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [serverKey]);

  const cps = useMemo(() => rows.map((r) => ({ name: r.name.trim() || '?', distM: kmValue(r.km) * 1000 })), [rows]);
  const marks = useMemo(() => cps.flatMap((p) => {
    const at = Number.isFinite(p.distM) ? positionAt(line, p.distM) : null;
    return at ? [{ name: p.name, lat: at[0], lon: at[1] }] : [];
  }), [cps, line]);
  const mapLine = useMemo(() => line.map((p) => [p[0], p[1]] as [number, number]), [line]);

  const addAt = (distM: number) => {
    if (disabled || lapLengthM <= 0) return;
    const d = Math.min(Math.max(Math.round(distM), 1), Math.max(1, Math.floor(lapLengthM) - 1));
    if (rows.some((r) => Math.abs(kmValue(r.km) * 1000 - d) < 1)) return;
    const next = [...rows, { name: t(`Kontrolný bod ${rows.length + 1}`, `Checkpoint ${rows.length + 1}`), km: String(d / 1000) }]
      .sort((a, b) => kmValue(a.km) - kmValue(b.km));
    setRows(next);
  };
  const problem = (r: Row): string | null => {
    const m = kmValue(r.km) * 1000;
    if (!r.name.trim()) return t('Zadajte názov.', 'Enter a name.');
    if (!Number.isFinite(m) || r.km.trim() === '') return t('Zadajte vzdialenosť v km.', 'Enter a distance in km.');
    if (m <= 0 || (lapLengthM > 0 && m >= lapLengthM))
      return t(`Bod musí ležať vnútri kola (0 – ${(lapLengthM / 1000).toFixed(2)} km).`, `The point must lie inside the lap (0 – ${(lapLengthM / 1000).toFixed(2)} km).`);
    if (rows.some((o) => o !== r && o.name.trim().toLowerCase() === r.name.trim().toLowerCase())) return t('Názov sa opakuje.', 'The name is repeated.');
    if (rows.some((o) => o !== r && Math.abs(kmValue(o.km) - kmValue(r.km)) < 0.0005)) return t('Rovnaká vzdialenosť ako iný bod.', 'The same distance as another point.');
    return null;
  };
  const invalid = rows.some((r) => problem(r) !== null) || (!!file && !parsed);

  const save = async () => {
    setSaveError(null);
    const gpxBase64 = file ? toBase64(await file.arrayBuffer()) : undefined;
    const body = {
      ...(gpxBase64 ? { gpxBase64 } : {}), laps, spectatorDelaySeconds: delay,
      checkpoints: rows.map((r) => ({ name: r.name.trim(), lapDistanceM: Math.round(kmValue(r.km) * 1000) })),
    };
    setBusy(true);
    try {
      setEvent(await put<ManageView>(`/api/events/${event.eventId}/course`, body));
      setFile(null); setParsed(null);
    } catch (x) { setSaveError(x); }
    setBusy(false);
  };

  return (
    <form id="course" className="card stack" onSubmit={(e) => { e.preventDefault(); save(); }}>
      <h2>{t('Trasa', 'Course')}</h2>
      {!c && !pending && (
        <p className="help">{t('Nahrajte súbor GPX s trasou jedného kola (exportujete ho z Garminu, Stravy, Komootu a pod.). Potom na mape pridáte kontrolné body.',
          'Upload a GPX file with the route of one lap (export it from Garmin, Strava, Komoot and similar). Then you add checkpoints on the map.')}</p>
      )}
      {pending && (
        <div className="banner" role="status">
          {t('Náhľad vybraného súboru – zatiaľ nie je uložený. Uložíte ho tlačidlom nižšie.', 'Preview of the chosen file – it is not saved yet. Save it with the button below.')}
        </div>
      )}
      <ErrorBanner error={saveError} />
      {(c || pending) && (
        <div className="stats">
          <div className="stat"><b>{(lapLengthM / 1000).toFixed(2)} km</b><span>{t('Dĺžka kola', 'Lap length')}</span></div>
          {event.laps > 1 && !pending && <div className="stat"><b>{((lapLengthM * event.laps) / 1000).toFixed(2)} km</b><span>{t(`Spolu (${event.laps} kôl)`, `Total (${event.laps} laps)`)}</span></div>}
          {!pending && c && hasElevation !== false && <div className="stat"><b>{Math.round(c.ascentM)} m</b><span>{t('Stúpanie / kolo', 'Ascent / lap')}</span></div>}
          {!pending && c && hasElevation !== false && <div className="stat"><b>{Math.round(c.descentM)} m</b><span>{t('Klesanie / kolo', 'Descent / lap')}</span></div>}
          {hasElevation && (pending?.minEle ?? saved.data?.minElevationM) != null && (
            <div className="stat"><b>{Math.round((pending?.minEle ?? saved.data?.minElevationM) as number)}–{Math.round((pending?.maxEle ?? saved.data?.maxElevationM) as number)} m</b><span>{t('Najnižší – najvyšší bod', 'Lowest – highest point')}</span></div>
          )}
          {!pending && c && <div className="stat"><b>{c.closed ? t('Uzavretá', 'Closed') : t('Otvorená', 'Open')}</b><span>{t('Typ trasy', 'Course type')}</span></div>}
          {pending && <div className="stat"><b>{pending.count}</b><span>{t('Bodov v súbore', 'Points in the file')}</span></div>}
        </div>
      )}
      {hasElevation === false && (
        <div className="banner" role="status">
          {t('Súbor neobsahuje výšky, preto nie je k dispozícii výškový profil ani stúpanie (zobrazuje sa 0 m). Kontrolné body fungujú normálne.',
            'The file has no elevation data, so there is no elevation profile and the ascent is not available (shown as 0 m). Checkpoints work as usual.')}
        </div>
      )}
      {mapLine.length > 1 && (
        <MapView key={pending && file ? `file-${file.name}-${file.size}` : 'saved'} points={mapLine} markers={[]} checkpoints={marks} className="map" onMapClick={disabled ? undefined : (lat, lon) => addAt(nearestDistance(line, lat, lon))} />
      )}
      {!disabled && mapLine.length > 1 && <p className="mute">{t('Kliknutím na mapu alebo do výškového profilu pridáte kontrolný bod na najbližšie miesto trasy.', 'Click the map or the elevation profile to add a checkpoint at the nearest point of the route.')}</p>}
      {hasElevation && line.length > 1 && <ElevationProfile points={line} lapLengthM={lapLengthM} checkpoints={cps} onPick={disabled ? undefined : addAt} />}

      <Field label={c ? t('Nahradiť súbor GPX (nepovinné)', 'Replace the GPX file (optional)') : t('Súbor GPX', 'GPX file')} name="gpxBase64" error={saveError}>
        <input id="gpxBase64" type="file" accept=".gpx,application/gpx+xml,text/xml" disabled={disabled} onChange={(e) => chosen(e.target.files?.[0] ?? null)} />
      </Field>
      {fileProblem && <div className="banner error" role="alert">{fileProblem}</div>}
      <div className="grid two">
        <Field label={t('Počet kôl (1–10)', 'Laps (1–10)')} name="laps" error={saveError}>
          <input id="laps" type="number" min={1} max={10} value={laps} disabled={disabled} onChange={(e) => setLaps(Number(e.target.value))} />
        </Field>
        <Field label={t('Oneskorenie pre divákov (5–300 s)', 'Spectator delay (5–300 s)')} name="spectatorDelaySeconds" error={saveError}>
          <input id="spectatorDelaySeconds" type="number" min={5} max={300} value={delay} disabled={disabled} onChange={(e) => setDelay(Number(e.target.value))} />
        </Field>
      </div>
      <p className="mute">{t('Oneskorenie: diváci vidia polohy o toľko sekúnd neskôr ako organizátor.', 'Delay: spectators see positions this many seconds later than you do.')}</p>

      <h3>{t('Kontrolné body', 'Checkpoints')}</h3>
      <p className="help">{t('Kontrolný bod je miesto na trase, kde sa meria medzičas. Body rozdelia kolo na úseky; divák vidí, ako sa kto v úseku mení (farebné sektory), a odhad príchodu do cieľa sa spresní. Nie sú povinné.',
        'A checkpoint is a place on the route where a split time is taken. The points divide the lap into sections; spectators see how each rider does in a section (coloured sectors) and the finish estimate gets more precise. They are optional.')}</p>
      {rows.length === 0 && <p className="mute">{t('Zatiaľ žiadny kontrolný bod.', 'No checkpoints yet.')}</p>}
      {rows.length > 1 && !disabled && (
        <p><button type="button" className="btn small danger" onClick={() => setRows([])}><span>{t(`Odstrániť všetky body (${rows.length})`, `Remove all points (${rows.length})`)}</span></button>
          <span className="mute"> {t('Zmena sa uloží až tlačidlom „Uložiť zmeny“.', 'The change is saved only with “Save changes”.')}</span></p>
      )}
      {rows.map((r, i) => {
        const p = problem(r);
        return (
          <div className="cprow" key={i}>
            <input aria-label={t('Názov kontrolného bodu', 'Checkpoint name')} placeholder={t('Názov (napr. Bufet)', 'Name (e.g. Aid station)')} value={r.name} disabled={disabled}
              onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, name: e.target.value } : x)))} />
            <label className="km">
              <input inputMode="decimal" aria-label={t('Vzdialenosť od štartu kola (km)', 'Distance from the lap start (km)')} value={r.km} disabled={disabled}
                onChange={(e) => setRows(rows.map((x, j) => (j === i ? { ...x, km: e.target.value } : x)))} />
              <span className="mute">{t('km od štartu', 'km from start')}</span>
            </label>
            <button type="button" className="btn small danger" disabled={disabled} aria-label={t('Odstrániť kontrolný bod', 'Remove checkpoint')} onClick={() => setRows(rows.filter((_, j) => j !== i))}><span>{t('Odstrániť', 'Remove')}</span></button>
            {p && <div className="note" role="alert">{p}</div>}
          </div>
        );
      })}
      <div className="row">
        {!disabled && lapLengthM > 0 && (
          <button type="button" className="btn small" onClick={() => addAt(lapLengthM / 2)}><span>{t('Pridať bod do polovice kola', 'Add a point at half of the lap')}</span></button>
        )}
        <button className="btn primary" disabled={disabled || busy || !dirty || invalid}>
          <span>{file ? (c ? t('Nahradiť trasu', 'Replace course') : t('Nahrať trasu', 'Upload course')) : t('Uložiť zmeny', 'Save changes')}</span>
        </button>
        {!dirty && c && <span className="mute">{t('Žiadne neuložené zmeny.', 'No unsaved changes.')}</span>}
      </div>
    </form>
  );
}
