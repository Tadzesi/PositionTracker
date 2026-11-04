# PositionTracker for Android

The native tracker of a race participant (Kotlin, Jetpack Compose, minSdk 26 / Android 8). It replaces OwnTracks on Android:
the invitation link opens the app, the participant agrees once and taps **Start tracking**; tracking **switches itself off**
when the organizer finishes the race. OwnTracks stays for iPhones and as the fallback.

Distribution: **self-install APK** from <https://www.lab463.com/tracker/android/> (no Google Play). The app checks
`/tracker/android/latest.json` and offers newer versions.

## How it works

| Step | App | Server |
|---|---|---|
| Pair | The email link `https://www.lab463.com/tracker/app/p?token=…` (Android App Link) or `positiontracker://p?token=…` (button "Open in the app" of the participant page) | `GET /api/p/{link}`, `POST /confirm`, `POST /consent` → device token |
| Store | Device token encrypted with an Android Keystore key; the link token is not kept | |
| Track | Foreground service of type `location`, started by the user (no background-location permission); fused location (platform GPS without Google services); a fix every 5 s (cycling) / 10 s (running), every 30 s until 10 min before the own start; partial wake lock | |
| Send | SQLite queue (max 20 000 fixes), batches of ≤ 500 every interval, `Retry-After` honoured, back-off 5…60 s offline | `POST /api/events/{id}/positions` (bearer) |
| Follow | Every 60 s: event state, own (wave) start, tuning, data-link light | `GET /api/device/session` (bearer) |
| Stop | `409 event_finished` or session state `finished` → stops, deletes the queue and the pairing; `401` → the same ("set up on another phone"); the user can stop; safety stop 12 h after the own start | |
| Wait | `409 event_not_live` → the batch is dropped, the app shows "the organizer has not started the event yet" | |

Only the release build accepts invitations for `https://www.lab463.com/tracker/app`; a debug build accepts any server
(`positiontracker://p?token=…&base=http%3A%2F%2F10.0.2.2%3A5080` for a local API from the emulator, cleartext allowed in debug only).

## Build

Needs JDK 17 (`JAVA_HOME`) and the Android SDK (platform 36; `local.properties` with `sdk.dir`, not committed).

```sh
cd android
./gradlew testDebugUnitTest assembleDebug     # unit tests + debug APK
./gradlew assembleRelease                     # signed release APK (key below)
python publish.py                             # android/dist/: APK, latest.json, assetlinks.json
```

Version: `versionCode` (must grow) and `versionName` in `app/build.gradle.kts`.

## Signing key (keep it safe)

The release key is **outside the repository**: `~/.android-keys/positiontracker-release.jks` and
`~/.android-keys/positiontracker-signing.properties` (`storeFile`, `storePassword`, `keyAlias`, `keyPassword`;
`-PptSigning=<path>` overrides the location). **Back both files up.** Without the same key no update can be installed over
the existing app (participants would have to uninstall it first), and the App Links of `assetlinks.json` stop matching.

## Publish

1. Raise `versionCode`/`versionName`, `./gradlew assembleRelease`, `python publish.py`.
2. `python site/build.py` copies `android/dist/` into `site/dist/android/`; deploy the site as in
   `docs/ops/deployment-home-server.md` (the APK is served at `/tracker/android/positiontracker.apk`).
3. Only when the key changes (normally once): `android/dist/assetlinks.json` →
   `https://www.lab463.com/.well-known/assetlinks.json` (web stack, see the deployment doc).

## Field test (open)

Phones of several makers (Samsung, Xiaomi, Pixel), screen locked, 30+ minutes: interval achieved, gaps, battery per hour,
offline catch-up, the automatic stop when the organizer finishes the event. Record the results here.
