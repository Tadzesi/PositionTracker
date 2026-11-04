"""Prepares a release of the Android app for self-install (no Google Play).

Run after `gradlew assembleRelease` (signed with the key from ~/.android-keys, see README.md):

    python android/publish.py

Writes android/dist/ (ignored by git):
  positiontracker.apk            the signed release APK (the download link of /tracker/android/)
  positiontracker-<version>.apk  the same file under its version
  latest.json                    {"versionCode","versionName","url","certSha256"}: the app's update check, the download page
  assetlinks.json                Android App Links: goes to https://www.lab463.com/.well-known/assetlinks.json

site/build.py copies the folder to site/dist/android/ (deployed with the product page).
"""
import glob, json, os, re, shutil, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
APK = os.path.join(HERE, 'app', 'build', 'outputs', 'apk', 'release', 'app-release.apk')
DIST = os.path.join(HERE, 'dist')
PUBLIC = 'https://www.lab463.com/tracker/android/'
PACKAGE = 'com.lab463.positiontracker'


def gradle_value(name):
    text = open(os.path.join(HERE, 'app', 'build.gradle.kts'), encoding='utf-8').read()
    return re.search(name + r'\s*=\s*"?([^"\n]+)"?', text).group(1).strip()


def apksigner():
    sdk = os.environ.get('ANDROID_HOME') or os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Android', 'Sdk')
    tools = sorted(glob.glob(os.path.join(sdk, 'build-tools', '*', 'apksigner*')))
    if not tools:
        sys.exit('apksigner not found under ' + sdk)
    return tools[-1]


def main():
    if not os.path.isfile(APK):
        sys.exit('missing ' + APK + ' (run: gradlew assembleRelease)')
    out = subprocess.run([apksigner(), 'verify', '--print-certs', APK], capture_output=True, text=True, shell=os.name == 'nt')
    if out.returncode != 0:
        sys.exit('the APK is not signed: ' + out.stderr.strip())
    digest = re.search(r'certificate SHA-256 digest:\s*([0-9a-f]+)', out.stdout).group(1)
    cert = ':'.join(digest[i:i + 2] for i in range(0, len(digest), 2)).upper()
    code, name = int(gradle_value('versionCode')), gradle_value('versionName')

    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    os.makedirs(DIST)
    shutil.copy(APK, os.path.join(DIST, 'positiontracker.apk'))
    shutil.copy(APK, os.path.join(DIST, 'positiontracker-%s.apk' % name))
    json.dump({'versionCode': code, 'versionName': name, 'url': PUBLIC, 'certSha256': cert},
              open(os.path.join(DIST, 'latest.json'), 'w', encoding='utf-8'), indent=2)
    json.dump([{'relation': ['delegate_permission/common.handle_all_urls'],
                'target': {'namespace': 'android_app', 'package_name': PACKAGE, 'sha256_cert_fingerprints': [cert]}}],
              open(os.path.join(DIST, 'assetlinks.json'), 'w', encoding='utf-8'), indent=2)
    print('published %s (%d) to %s, certificate %s' % (name, code, DIST, cert))


if __name__ == '__main__':
    main()
