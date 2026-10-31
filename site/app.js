/* i18n (Slovak is the text in the HTML, English comes from this dictionary), theme toggle, guide tabs and the demo. */
(function () {
  const EN = {
    skip: 'Skip to the guides', nav: 'Main navigation', theme: 'Theme', theme_t: 'Theme',
    nav_who: 'Who it is for', nav_screens: 'What it looks like', nav_guides: 'Guides', nav_faq: 'Questions',
    h1a: 'Every rider.', h1b: 'Every second.', h1c: 'Live.',
    hero_lead: 'PositionTracker turns your run or cycling race into a TV broadcast: the fastest, most accurate and most beautiful you can imagine. Spectators open a link, the organizer sees everything, and a runner installs nothing but one free app.',
    cta_demo: 'Start the live demo', cta_guides: 'Read the guides',
    demo_label: 'Live race demo', demo_map: 'Course map with riders', demo_win: 'Finish!', demo_again: 'Restart the race',
    tower: 'Live standings', tower_label: 'Standings', sport_c: 'Cycling', sport_r: 'Running', demo_stop: 'Stops', demo_off: 'Leaves the course', demo_batt: 'Low battery', view_org: 'Organizer view', view_spec: 'Spectator view', demo_restart: 'Restart',
    demo_note: 'The demo runs in your browser (made-up riders and course) but shows the same as the real application. The organizer view sees everything instantly, including alarms and battery level. The spectator view is delayed by 15 s, has no alarms and hides the names of those who did not agree. Select a rider and try "Stops".',
    who_h: 'Four roles. One experience that cannot be beaten.',
    w1_h: 'Cyclist', w1_p: 'Your family finally knows exactly where you are and does not have to ask at the finish "are you there yet?". You just pedal.',
    w1_a: 'A phone in the jersey pocket, no extra gadget.', w1_b: 'Friends follow you on the map and see when you arrive.', w1_c: 'Your number and nickname appear in the public standings, if you want.',
    w2_h: 'Runner', w2_p: 'Pace per kilometre, the gap to the runner ahead and an ETA to the minute. Just like at the big championships, only on your loop.',
    w2_a: 'Pace in min/km instead of km/h, automatically by sport.', w2_b: 'Below 20 % battery the organizer is warned before the phone dies.', w2_c: 'After the finish broadcasting stops by itself and the data is gone within 30 days.',
    w3_h: 'Organizer', w3_p: 'A control room like in Formula 1: map, standings, coloured sectors and alarms for stopped riders, riders who left the course, lost signal and low battery. Nobody is left in the forest unnoticed.',
    w3_a: 'Email invitations in Slovak and English, reminders with one click.', w3_b: 'GPX course, laps, checkpoints, categories and start waves.', w3_c: 'A public link for spectators with an adjustable delay.',
    w4_h: 'Spectator', w4_p: 'No installation, no account. You open the link, find your favourite and watch them approach the finish.',
    w4_a: 'A star next to a name and the map does the rest.', w4_b: 'An alert on the page when your rider crosses the finish line.', w4_c: 'Share a rider with one tap.',
    sc_h: 'No mock-ups. This is the real application.', sc_lead: 'Every picture below is a screenshot of the running application with sample riders and a sample course.',
    sc1_alt: 'Race control: map, standings and messages', sc1_h: 'Race control', sc1_p: 'A map with all riders, standings with coloured sectors, messages about stopped riders, riders off the course, lost signal and low battery.',
    sc2_alt: 'Spectator page on a phone', sc2_h: 'Spectator on a phone', sc2_p: 'The biggest figure is the ETA to the finish. Below it distance, speed and sectors.',
    sc3_alt: 'Spectator page in dark mode', sc3_h: 'Dark and light mode', sc3_p: 'The page follows the device setting. The toggle is always at hand.',
    sc4_alt: 'Participants list with statuses', sc4_h: 'Participants and invitations', sc4_p: 'Who confirmed and who did not. Resend, remind and re-issue the link with one click.',
    sc5_alt: 'Phone setup with a QR code', sc5_h: 'Phone setup in a minute', sc5_p: 'One tap or a QR code configures the OwnTracks app. No typing of addresses.',
    sc6_alt: 'Event setup', sc6_h: 'An event in minutes', sc6_p: 'Upload a GPX, set laps, checkpoints, categories and the spectator delay.',
    g_h: 'Guides so clear that even your grandmother can follow them.', g_lead: 'Pick who you are. Every procedure has exactly the steps you will see in the app.',
    g_tabs: 'Guides by role', g_org: 'Organizer', g_par: 'Participant', g_spe: 'Spectator',
    o1_h: 'Create an account', o1_p: 'Enter your name and email. There is no password: you sign in with a one-time link sent by email. Open the link and press "Confirm email". Only then can you upload a course and invite participants (abuse protection).',
    o2_h: 'Create the event and upload the course', o2_p: 'Name, place, start date and time, sport. Then a GPX file, the number of laps (1 to 10), checkpoints and the spectator delay (5 to 300 seconds). A course with several laps must be closed.', o2_tip: 'Tip: a delay of 15 to 30 seconds keeps the broadcast smooth and still fair to the riders.',
    o3_h: 'Invite participants', o3_p: 'Name, email, gender, email language, category, wave. The bib is assigned automatically, or you enter it. The invitation goes out immediately. The list shows Invited, Confirmed, Declined and Email failed, and you can send a reminder, resend, or issue a new link.', o3_tip: 'Spectators see a participant\'s name only with consent. For minors only the bib is shown publicly.',
    o4_h: 'Ready, then live', o4_p: 'Press "Mark as ready" and on race day "Go live". The app shows the public link for spectators: post it and put it on posters as a QR code. Once live, the course can no longer be changed.',
    o5_h: 'Run the race', o5_p: 'The "Race control" tab shows the map, standings, sectors and messages. Press "Acknowledge" to handle an alarm, find the rider on the map, and mark a rider who has withdrawn as DNF. Unhandled messages stay on top, handled ones are dimmed.',
    o6_h: 'After the finish', o6_p: 'Press "Finish event". Positions are deleted after 30 days by themselves. If a participant asks, you delete their data immediately (only the bib remains). You issue the official results: read the spectator standings as indicative.',
    p1_h: 'Open the invitation', p1_p: 'In the organizer\'s email click your personal link. The page appears in your language and shows the event, your name and bib. Confirm (or decline) and, if you like, choose a nickname (max. 30 characters).', p1_alt: 'The invitation on a phone',
    p2_h: 'Give your consent', p2_p: 'Consent to share your location during the race is required. The organizer sees you exactly, spectators with a delay. Optionally allow showing your name (otherwise they see only the bib). The data is deleted after 30 days.', p2_alt: 'Consent to tracking',
    p3_h: 'Set up OwnTracks', p3_p: 'Install the free OwnTracks app (Android and iPhone). On the phone tap "Set up OwnTracks", or scan the QR code from another device, and accept the import. Nothing is typed by hand.', p3_tip: 'The setup contains your device\'s secret key. Do not share it. If you lost it, repeat the consent: the old key stops working.',
    p4_h: 'Allow location and battery', p4_p: '<b>Android:</b> Settings → Apps → OwnTracks → Battery → Unrestricted; Location → Allow all the time. <b>iPhone:</b> Settings → OwnTracks → Location → Always, Precise Location on, Background App Refresh on. Keep the app in "Move" mode.',
    p5_h: 'At the start and during the race', p5_p: 'Before the start check that the app shows no error after sending a position. Do not switch the phone off or turn on battery saver. SOS is not a button in the app: phone the organizer. The organizer marks a withdrawal.',
    s1_h: 'Open the public link', s1_p: 'The organizer sends you a link or a QR code. No installation, no account. At the top a notice says positions are delayed: the standings are indicative.',
    s2_h: 'Find your favourite', s2_p: 'Search by name or bib, filter by category. The star next to a name follows them: other riders shrink on the map, yours stands out.',
    s3_h: 'It reads itself', s3_p: 'The biggest figure is the expected finish time and its range. Below it distance covered, speed (or pace), rank and gap, and coloured sectors: purple is the fastest sector of everyone, green your best, yellow slower.',
    s4_h: 'Finish alert and sharing', s4_p: 'Turn on "Notify me at the finish" and the page alerts you when the rider crosses the line (while it stays open). "Share" sends a link straight to your rider.',
    how_h: 'Behind the beauty stand hard numbers.',
    f1: 'apps for spectators. A link in the browser is enough.', f2: 'free app for the participant: OwnTracks, in the background, even with a locked screen.',
    f3: 'and positions delete themselves. Spectators see a name only with consent, and never a minor\'s.', f4: 'spectator delay is yours to set; the organizer sees everything instantly.',
    faq_h: 'Questions you would ask later.',
    q1: 'Do I need my own GPS tracker?', a1: 'No. A phone in a jersey pocket, backpack or armband is enough. The free OwnTracks app sends the position in the background.',
    q2: 'What if the signal drops?', a2: 'The app queues positions and sends them when the connection returns. The organizer and spectators see an "offline" notice meanwhile. Behaviour in long outages is still being verified in the field.',
    q3: 'Can spectators see my exact position?', a3: 'No. Spectators see the position with a delay and only during the race. Only the organizer sees you exactly.',
    q4: 'Does it work for running and cycling?', a4: 'Yes. For running the app shows pace in min/km, for cycling km/h, and it adjusts the position sampling too.',
    q5: 'What does it cost and where does it run?', a5: 'PositionTracker is an open project you can run on your own server (Docker, PostgreSQL). The deployment guide is in the repository.',
    q6: 'Are the standings official?', a6: 'No. Spectator standings are indicative because positions are delayed. The organizer issues the official results.',
    fin_h: 'Your next race deserves a broadcast.', fin_p: 'Run the demo once more, switch to the spectator view and imagine it is your event. Then create your own.', fin_cta: 'Try the demo again', fin_app: 'Create an account and start your race', nav_app: 'Open the app',
    foot: 'The demo on this page uses made-up data. Map data © OpenStreetMap contributors.',
  };
  const ATTR = [['data-i-aria', 'aria-label'], ['data-i-alt', 'alt']];
  const root = document.documentElement;
  const sk = new Map();
  document.querySelectorAll('[data-i]').forEach((el) => sk.set(el, el.innerHTML));
  ATTR.forEach(([d, a]) => document.querySelectorAll('[' + d + ']').forEach((el) => el.setAttribute('data-sk-' + a, el.getAttribute(a) || '')));

  let lang = 'en';
  const nav = (navigator.language || '').toLowerCase();
  if (nav.startsWith('sk') || nav.startsWith('cs')) lang = 'sk';
  try { const saved = localStorage.getItem('pt-site-lang'); if (saved === 'sk' || saved === 'en') lang = saved; } catch (e) { /* storage blocked */ }

  const t = (a, b) => (lang === 'sk' ? a : b);

  function apply() {
    root.lang = lang;
    document.querySelectorAll('[data-i]').forEach((el) => {
      el.innerHTML = lang === 'sk' ? sk.get(el) : (EN[el.dataset.i] ?? sk.get(el));
    });
    ATTR.forEach(([d, a]) => document.querySelectorAll('[' + d + ']').forEach((el) => {
      el.setAttribute(a, lang === 'sk' ? el.getAttribute('data-sk-' + a) : (EN[el.getAttribute(d)] ?? el.getAttribute('data-sk-' + a)));
    }));
    document.title = lang === 'sk' ? 'PositionTracker – živé sledovanie pretekov, aké ste ešte nevideli' : 'PositionTracker – live race tracking like you have never seen';
    document.getElementById('lang').textContent = lang === 'sk' ? 'EN' : 'SK';
    if (demo) demo.redraw();
  }

  document.getElementById('lang').addEventListener('click', () => {
    lang = lang === 'sk' ? 'en' : 'sk';
    try { localStorage.setItem('pt-site-lang', lang); } catch (e) { /* storage blocked */ }
    apply();
  });

  document.getElementById('theme').addEventListener('click', () => {
    const cur = root.getAttribute('data-theme');
    const next = cur === 'dark' ? 'light' : cur === 'light' ? null : 'dark';
    if (next) root.setAttribute('data-theme', next); else root.removeAttribute('data-theme');
    try { next ? localStorage.setItem('pt-site-theme', next) : localStorage.removeItem('pt-site-theme'); } catch (e) { /* storage blocked */ }
  });

  // guide tabs
  const tabs = [...document.querySelectorAll('[role="tab"]')];
  function show(tab) {
    tabs.forEach((x) => {
      const on = x === tab;
      x.setAttribute('aria-selected', String(on)); x.tabIndex = on ? 0 : -1;
      document.getElementById(x.getAttribute('aria-controls')).hidden = !on;
    });
  }
  tabs.forEach((tab, i) => {
    tab.addEventListener('click', () => show(tab));
    tab.addEventListener('keydown', (e) => {
      const n = e.key === 'ArrowRight' ? (i + 1) % tabs.length : e.key === 'ArrowLeft' ? (i + tabs.length - 1) % tabs.length : -1;
      if (n >= 0) { tabs[n].focus(); show(tabs[n]); }
    });
  });
  if (location.hash === '#guide-participant') show(tabs[1]);
  if (location.hash === '#guide-spectator') show(tabs[2]);

  if (window.PT_APP_URL) { for (const id of ['appcta', 'appnav']) { const a = document.getElementById(id); a.href = window.PT_APP_URL; a.hidden = false; } }

  let demo = null;
  demo = window.PTDemo.init(document.getElementById('demo'), t);
  apply();
})();
