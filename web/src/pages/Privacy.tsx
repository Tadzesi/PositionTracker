import type { ReactNode } from 'react';
import { useI18n } from '../lib/i18n';

/** The date the text below took effect; change it whenever the content changes. */
export const PRIVACY_EFFECTIVE = '2026-11-02';
const CONTACT = 'martinhorny74@gmail.com';

/**
 * Privacy notice (GDPR art. 13/14) including the list of everything the application stores in the browser. There is no
 * cookie consent banner on purpose: the application stores only what is strictly necessary for a service the visitor
 * asked for (the organizer session) and choices the visitor made explicitly (language, appearance, followed riders),
 * which § 109(8) of Slovak Act 452/2021 Coll. (art. 5(3) of Directive 2002/58/EC) exempts from consent. Adding analytics,
 * ads or any third-party script that stores or reads data in the browser would require a consent banner first.
 */
function Sec({ title, children }: { title: string; children: ReactNode }) {
  return <section className="card stack"><h2>{title}</h2>{children}</section>;
}

export default function Privacy() {
  const { t, lang } = useI18n();
  const date = new Date(`${PRIVACY_EFFECTIVE}T12:00:00Z`).toLocaleDateString(lang === 'sk' ? 'sk-SK' : 'en-GB', { day: 'numeric', month: 'long', year: 'numeric' });
  return (
    <div className="privacy">
      <h1>{t('Ochrana osobných údajov a cookies', 'Privacy and cookies')}</h1>
      <p className="mute">{t(`Platné od ${date}.`, `Effective from ${date}.`)}</p>

      <Sec title={t('Kto spracúva vaše údaje', 'Who processes your data')}>
        <p>{t('Prevádzkovateľom služby PositionTracker (www.lab463.com/tracker) je Martin Horný, Košice, Slovensko. Kontakt pre otázky a uplatnenie práv: ', 'PositionTracker (www.lab463.com/tracker) is operated by Martin Horný, Košice, Slovakia. Contact for questions and for exercising your rights: ')}
          <a href={`mailto:${CONTACT}`}>{CONTACT}</a>.</p>
        <ul>
          <li>{t('Pri účtoch organizátorov je prevádzkovateľom osobných údajov prevádzkovateľ služby.', 'For organizer accounts, the operator of the service is the controller.')}</li>
          <li>{t('Pri účastníkoch podujatia je prevádzkovateľom organizátor podujatia: on rozhoduje, koho pozve a prečo. PositionTracker údaje účastníkov spracúva v jeho mene (sprostredkovateľ). Žiadosti týkajúce sa účasti na podujatí smerujte predovšetkým na organizátora; pomôžeme aj my.', 'For event participants, the event organizer is the controller: they decide whom to invite and why. PositionTracker processes participant data on their behalf (processor). Please address requests about an event primarily to its organizer; we will help as well.')}</li>
        </ul>
      </Sec>

      <Sec title={t('Aké údaje, na aký účel a na akom základe', 'What data, for what purpose and on what basis')}>
        <h3>{t('Organizátor', 'Organizer')}</h3>
        <p>{t('Meno, e-mail, jazyk e-mailov, čas registrácie a potvrdenia e-mailu. Slúžia na vedenie účtu, prihlásenie jednorazovým odkazom a odosielanie pozvánok vo vašom mene. Základ: plnenie zmluvy (čl. 6 ods. 1 písm. b) GDPR). Kým ste prihlásený, aplikácia raz za 30 s oznámi, že ju aktívne používate; ukladá sa len súčet sekúnd za deň, aby sme videli, ako sa aplikácia používa. Základ: oprávnený záujem na zlepšovaní služby (písm. f).', 'Name, email, email language, time of registration and of email confirmation. Used to run your account, to sign you in with a one-time link and to send invitations on your behalf. Basis: performance of a contract (GDPR art. 6(1)(b)). While you are signed in, the application reports every 30 s that you are actively using it; only the total seconds per day are stored, so that we can see how the application is used. Basis: legitimate interest in improving the service (art. 6(1)(f)).')}</p>
        <h3>{t('Účastník podujatia', 'Event participant')}</h3>
        <p>{t('Organizátor zadáva meno, e-mail, pohlavie, jazyk, kategóriu, vlnu, štartové číslo a príznak maloletého. Vy môžete doplniť prezývku. Ukladá sa váš súhlas (čas a verzia textu). Počas pretekov aplikácia OwnTracks posiela polohu (s presnosťou a časom) a stav zariadenia (batéria, nabíjanie) priamo na náš server; z nich vznikajú živé zobrazenie a výsledky.', 'The organizer enters your name, email, gender, language, category, wave, bib number and whether you are a minor. You may add a nickname. Your consent is stored (time and version of the text). During the race the OwnTracks app sends your position (with accuracy and time) and device status (battery, charging) directly to our server; the live view and the results are made from them.')}</p>
        <ul>
          <li>{t('Zdieľanie polohy a zobrazenie mena divákom: váš súhlas (čl. 6 ods. 1 písm. a) GDPR). Súhlas môžete kedykoľvek odvolať: vypnite odosielanie v OwnTracks a požiadajte organizátora alebo nás o vymazanie. Odvolanie neovplyvní zákonnosť spracúvania pred ním.', 'Sharing your position and showing your name to spectators: your consent (GDPR art. 6(1)(a)). You may withdraw it at any time: stop sending in OwnTracks and ask the organizer or us to delete your data. Withdrawal does not affect processing that took place before it.')}</li>
          <li>{t('Ostatné údaje: organizácia podujatia, na ktoré ste sa prihlásili (základ určuje organizátor, spravidla zmluva alebo oprávnený záujem).', 'Other data: organizing the event you take part in (the organizer determines the basis, usually a contract or legitimate interest).')}</li>
          <li>{t('Organizátor vidí polohu presne a hneď, diváci oneskorene a len počas pretekov. U maloletých sa verejne zobrazuje iba štartové číslo.', 'The organizer sees your position exactly and immediately, spectators with a delay and only during the race. For minors only the bib number is shown publicly.')}</li>
        </ul>
        <h3>{t('Divák', 'Spectator')}</h3>
        <p>{t('Divák sa neregistruje. O divákovi neukladáme nič okrem technických záznamov nižšie.', 'Spectators do not register. We store nothing about a spectator except the technical records below.')}</p>
        <h3>{t('Technické záznamy', 'Technical records')}</h3>
        <p>{t('Webový server zaznamenáva IP adresu, čas a adresu požiadavky (tajné tokeny v adresách sú maskované). Slúžia na prevádzku a ochranu pred zneužitím a uchovávajú sa len krátkodobo. Základ: oprávnený záujem (písm. f).', 'The web server records the IP address, time and address of a request (secret tokens in addresses are masked). They serve operation and protection against abuse and are kept only briefly. Basis: legitimate interest (art. 6(1)(f)).')}</p>
      </Sec>

      <Sec title={t('Ako dlho údaje uchovávame', 'How long we keep data')}>
        <ul>
          <li>{t('Polohy a stav zariadení: 30 dní po skončení podujatia, potom sa automaticky zmažú.', 'Positions and device status: 30 days after the event ends, then deleted automatically.')}</li>
          <li>{t('Údaje účastníka a výsledky: kým ich organizátor nezmaže alebo kým nepožiadate o vymazanie.', 'Participant data and results: until the organizer deletes them or you ask for deletion.')}</li>
          <li>{t('Odoslané e-maily (záznam vo fronte): 7 dní.', 'Sent emails (queue record): 7 days.')}</li>
          <li>{t('Účet organizátora: kým ho nepožiadate zrušiť.', 'Organizer account: until you ask us to close it.')}</li>
        </ul>
      </Sec>

      <Sec title={t('Komu údaje poskytujeme', 'Who receives data')}>
        <ul>
          <li>{t('Aplikácia beží na vlastnom serveri prevádzkovateľa na Slovensku. Údaje nepredávame ani neposkytujeme na marketing.', 'The application runs on the operator’s own server in Slovakia. We do not sell data or share it for marketing.')}</li>
          <li>{t('E-maily (pozvánky, prihlasovacie odkazy) odchádzajú cez poskytovateľa odosielania pošty (SMTP), ktorý vidí adresu príjemcu a obsah správy.', 'Emails (invitations, sign-in links) are sent through an email delivery provider (SMTP), which sees the recipient address and the message.')}</li>
          <li>{t('Mapové podklady načítava váš prehliadač priamo zo serverov OpenStreetMap Foundation (tile.openstreetmap.org), ktorá tak vidí vašu IP adresu. OSMF sídli vo Veľkej Británii, pre ktorú platí rozhodnutie Európskej komisie o primeranej ochrane.', 'Your browser loads map tiles directly from OpenStreetMap Foundation servers (tile.openstreetmap.org), which therefore see your IP address. OSMF is based in the United Kingdom, which is covered by a European Commission adequacy decision.')}</li>
          <li>{t('Odkazy na App Store a Google Play sa otvoria až po kliknutí; ďalej platia zásady týchto obchodov.', 'Links to the App Store and Google Play open only when you click them; from there the stores’ own policies apply.')}</li>
        </ul>
      </Sec>

      <Sec title={t('Cookies a údaje uložené v prehliadači', 'Cookies and data stored in your browser')}>
        <p>{t('Nepoužívame analytické, reklamné ani sledovacie cookies a nenačítavame skripty tretích strán. Ukladáme len to, čo je nevyhnutné pre službu, o ktorú ste požiadali, alebo voľbu, ktorú ste sami urobili. Na to sa súhlas nevyžaduje (§ 109 ods. 8 zákona č. 452/2021 Z. z. o elektronických komunikáciách, čl. 5 ods. 3 smernice 2002/58/ES), preto vás nežiadame o súhlas s cookies.', 'We use no analytics, advertising or tracking cookies and load no third-party scripts. We store only what is strictly necessary for a service you asked for, or a choice you made yourself. No consent is required for that (§ 109(8) of Slovak Act No. 452/2021 Coll. on electronic communications, art. 5(3) of Directive 2002/58/EC), which is why we do not ask you to accept cookies.')}</p>
        <div className="tablewrap">
          <table>
            <thead><tr><th>{t('Názov', 'Name')}</th><th>{t('Typ', 'Type')}</th><th>{t('Účel', 'Purpose')}</th><th>{t('Platnosť', 'Expiry')}</th></tr></thead>
            <tbody>
              <tr><td><code>__Secure-pt-session</code></td><td>cookie</td><td>{t('Prihlásenie organizátora (nevyhnutné)', 'Organizer sign-in (strictly necessary)')}</td><td>{t('30 dní od poslednej aktivity alebo do odhlásenia', '30 days after last activity or until sign-out')}</td></tr>
              <tr><td><code>pt-lang</code></td><td>localStorage</td><td>{t('Jazyk, ktorý ste zvolili', 'The language you chose')}</td><td>{t('Kým ho nezmažete v prehliadači', 'Until you clear it in your browser')}</td></tr>
              <tr><td><code>pt-theme</code></td><td>localStorage</td><td>{t('Svetlý alebo tmavý režim, ktorý ste zvolili', 'The light or dark mode you chose')}</td><td>{t('Kým ho nezmažete v prehliadači', 'Until you clear it in your browser')}</td></tr>
              <tr><td><code>pt-follow-…</code></td><td>localStorage</td><td>{t('Jazdci, ktorých sledujete v diváckom pohľade', 'The riders you follow in the spectator view')}</td><td>{t('Kým ho nezmažete v prehliadači', 'Until you clear it in your browser')}</td></tr>
            </tbody>
          </table>
        </div>
        <p className="mute">{t('Údaje v localStorage zostávajú len vo vašom prehliadači a neodosielajú sa na server.', 'localStorage data stays in your browser and is not sent to the server.')}</p>
      </Sec>

      <Sec title={t('Vaše práva', 'Your rights')}>
        <p>{t('Máte právo na prístup k údajom, ich opravu, vymazanie, obmedzenie spracúvania, prenosnosť a právo namietať proti spracúvaniu na základe oprávneného záujmu. Súhlas môžete kedykoľvek odvolať. Napíšte na ', 'You have the right of access, rectification, erasure, restriction of processing, data portability and the right to object to processing based on legitimate interest. You may withdraw consent at any time. Write to ')}
          <a href={`mailto:${CONTACT}`}>{CONTACT}</a>{t('; odpovieme do jedného mesiaca.', '; we will reply within one month.')}</p>
        <p>{t('Ak sa domnievate, že spracúvanie porušuje predpisy, môžete podať sťažnosť Úradu na ochranu osobných údajov SR, Hraničná 12, 820 07 Bratislava, ', 'If you believe the processing breaks the law, you may lodge a complaint with the Office for Personal Data Protection of the Slovak Republic, Hraničná 12, 820 07 Bratislava, ')}
          <a href="https://dataprotection.gov.sk" rel="noopener">dataprotection.gov.sk</a>.</p>
        <p>{t('Nerobíme automatizované rozhodovanie ani profilovanie s právnymi účinkami.', 'We do not make automated decisions or profiling with legal effects.')}</p>
      </Sec>
    </div>
  );
}
