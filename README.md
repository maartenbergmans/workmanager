# WorkManager

Klein Windows-systeemvakprogramma (naast de klok) waarmee je werkcontexten (**CED**, **Aqurat**, **RadiologyPartners**) aan- en uitzet. Meerdere contexten kunnen tegelijk aan staan. Aanzetten start de werkomgeving van die context; uitzetten sluit de gestarte apps weer.

## Gebruik

- Het icoon in het systeemvak toont de actieve contexten: blauwe **C** = CED, oranje **A** = Aqurat, teal **R** = RadiologyPartners. Staan er meerdere aan, dan is het icoon in kleurvlakken verdeeld; staat niets aan, dan is het grijs. De tooltip toont de volledige namen.
- Klik (links of rechts) op het icoon en klik op een context om die aan of uit te zetten (vinkje = aan).
- Via het menu kan de app ook automatisch met Windows meestarten.

> Windows 11 verbergt nieuwe systeemvakiconen standaard in het overloopmenu (^). Sleep het icoon één keer naast de klok om het permanent zichtbaar te maken.

## Aansturing door andere tools

De actieve contexten worden persistent bijgehouden in `%APPDATA%\WorkManager\`:

- `active-contexts.json` — actuele status, wordt bij elke wijziging overschreven:
  ```json
  {
    "active": ["CED", "Aqurat"],
    "since": "2026-07-16T09:00:00+02:00"
  }
  ```
- `switch-log.jsonl` — append-only log van alle aan/uit-acties (één JSON-object per regel, met `timestamp`, `client` en `action`: `"on"` of `"off"`). Bruikbaar voor latere tijdsregistratie per klant.

Andere scripts/tools kunnen `active-contexts.json` lezen (of op wijzigingen watchen) om gedrag aan te sturen.

## Timesheets (UrbanAdmin)

Bij het aanzetten van een context start WorkManager een werkuur in UrbanAdmin (`POST /api/workmanager/werkuur/start/{token}`, van = tot = nu); bij het uitzetten wordt de eindtijd gezet (`.../stop/{token}`). De werkuur-id wordt per context bewaard in `%APPDATA%\WorkManager\timesheet-state.json`, zodat dit ook na een herstart van WorkManager blijft werken. Valt het stoppen over middernacht, dan sluit de backend het werkuur af op 23:59:59 van de startdag.

Instellingen in `launch-config.json`: globaal `timesheets` (`baseUrl`, `token` = `WORKMANAGER_TOKEN` uit de UrbanAdmin-.env, `gebruikerId`) en per context `timesheet` (`projectId`, optioneel `omschrijving`; leeg = "WorkManager &lt;context&gt;"). Geconfigureerde projecten: Aqurat → 114 (Ontwikkeling Fase 1), CED → 1 (consultancy dagbasis), RadiologyPartners → 99 (IT advies). Zonder token wordt de stap overgeslagen; API-fouten blokkeren de rest van de launch niet.

Los testen: `WorkManager.exe --timesheet start Aqurat` en `--timesheet stop Aqurat`.

## Werkomgeving-launcher

Bij het **aanzetten** van een context start WorkManager de werkomgeving — elk onderdeel alleen als het nog niet open staat. Bij het **uitzetten** worden dezelfde onderdelen weer gesloten (vensters krijgen een nette sluitopdracht; de app kan dus nog om opslaan vragen):

| Onderdeel | Detectie "al open" | Start | Sluiten |
|---|---|---|---|
| PhpStorm | venstertitel bevat projectnaam | opent het project | sluit het projectvenster |
| DataGrip | venstertitel bevat projectnaam | opent het project | sluit het projectvenster |
| Browser (Firefox of Chrome, per context) | een venstertitel bevat de ingestelde tekst (alleen actieve tab zichtbaar) | opent de URL in een nieuwe tab; met `waitForApp` pas nadat de app-URL bereikbaar is (max. 10 min); `extraWindows` opent daarna extra vensters (bv. Mailpit, Asana) | sluit elk venster waarvan de titel matcht |
| Claude | een `claude.exe`-proces heeft de projectmap als working directory | opent Windows Terminal in de projectmap met `claude` | beëindigt de sessie (shell + terminaltab) |
| Programma's (bv. Outlook voor CED) | een venster van de procesnaam | start het opgegeven pad | sluit de vensters van het proces |

Voor Aqurat start de browser dus bewust pas nadat de app draait (`waitForApp`): PhpStorm start bij het openen van het Aqurat-project via een Startup Task automatisch `npm start` (`.idea/runConfigurations/Start_app.xml` + `.idea/startup.xml`), en WorkManager pollt de app-URL tot die antwoordt. Wordt de context ondertussen uitgezet, dan wordt het wachten geannuleerd. RadiologyPartners opent de datastatus-pagina direct in Chrome, zonder te wachten.

### Schermindeling

Bij het aanzetten van een context wordt elk venster (PhpStorm, DataGrip, browservensters, programma's) gemaximaliseerd en naar de voorgrond gehaald — ook als het al open stond. Met een `monitor`-instelling (1 = meest linkse scherm) gebeurt dat op dat scherm; ontbreekt dat scherm (bv. laptop zonder dock) of is er geen `monitor` ingesteld, dan op het scherm waar het venster nu staat. Voor Aqurat: PhpStorm op scherm 1, alle Firefox-vensters (app, Mailpit, Asana) op scherm 2, DataGrip op scherm 3.

De acties staan in `%APPDATA%\WorkManager\launch-config.json` en zijn daar per context aan te passen (verwijder het bestand om de defaults terug te krijgen). Voor CED is één programma geconfigureerd: de nieuwe Outlook (`%LOCALAPPDATA%\Microsoft\WindowsApps\olk.exe`, procesnaam `olk`).

Testen zonder iets te starten of te sluiten: `WorkManager.exe --dry-run Aqurat` of `WorkManager.exe --dry-run-close Aqurat` — het resultaat komt in `%APPDATA%\WorkManager\launcher.log`.

## Facturen goedkeuren (ISPnext, CED)

Via het tray-menu **"Facturen goedkeuren (ISPnext)…"** opent een venster dat de wekelijkse goedkeuringsroutine in ISPnext AP Automation overneemt (voorheen een Claude-skill):

- Rechts staat een ingebedde browser (WebView2) met een **eigen, blijvend profiel** (`%APPDATA%\WorkManager\webview2-ispnext`): éénmaal inloggen via Single Sign-On (+ MFA) en de sessie blijft bewaard.
- Een **login-assistent** klikt de loginflow automatisch door: op de ISPnext-loginpagina wordt de gebruikersnaam ingevuld en op *"Ga verder met Single Sign-On"* geklikt, en in het Microsoft-accountkeuzescherm wordt de juiste account-tegel aangeklikt. Alleen een eventuele MFA-vraag blijft handmatig.
- **Facturen ophalen** leest de tabel op de "My Activities"-pagina uit en toont die links, met per factuur of ze onder een auto-goedkeuringsregel valt. Facturen die aan een regel voldoen worden automatisch aangevinkt; de rest staat er in het oranje bij met de reden (geen regel, boven plafond, geen EUR). Vinkjes kunnen handmatig aangepast worden.
- **Geselecteerde goedkeuren…** vinkt de rijen in ISPnext aan en doorloopt zonder verdere bevestiging **Acties → Facturen goedkeuren → OK**. Het resultaat (groene vinkjes) verschijnt in de browser en wordt in het logvenster gerapporteerd — controleer dit visueel.
- **Regels beheren…** opent het beheerscherm met per leverancier het maximumbedrag (exacte naam, hoofdletterongevoelig). Rechtsklik op een factuur → *"Regel maken/aanpassen voor deze leverancier…"* maakt direct een regel met de juiste naam aan. De regels staan in `%APPDATA%\WorkManager\invoice-approval-rules.json` en worden bij de eerste start gevuld met de drempels uit de oude skill.

Mislukt een stap in de automatisering (knop niet gevonden, tabel gewijzigd), dan meldt het log dit en kan de handeling gewoon handmatig in de ingebedde browser afgemaakt worden.

## Taken team (weekmail)

Via het tray-menu **"Taken team…"** opent een venster om taken aan teamleden toe te wijzen en daar wekelijks de prioriteitenmail uit te genereren:

- De lijst toont per teamlid een groep met taken. Bovenaan kies je een teamlid, typ je de taak en druk je op Enter (of "Toevoegen"). **Afvinken** = klaar (doorstreept, telt niet meer mee voor de mail). Bewerken kan met F2 of via rechtsklik; verder via rechtsklik: verwijderen (ook Del), verplaatsen naar een ander teamlid en omhoog/omlaag binnen het lid.
- **Leden beheren…** bepaalt de teamleden én hun volgorde in de mail (één per regel). Taken van een verwijderd lid blijven gewoon staan.
- Onderaan staat de **opmerking bovenaan de weekmail** (bv. wie afwezig is en dat de tickets opgevolgd moeten worden); die wordt na de aanhef in de mail verwerkt.
- **Mail opstellen…** laat Claude — via de Claude Code CLI (`claude -p`), op het bestaande abonnement — een weekmail opstellen met alle **open** taken, in de stijl van de voorbeeldmails onder **Stijl weekmail…** (de bekende mix Nederlands/Frans/Engels, opsomming per persoon). De taakteksten worden letterlijk overgenomen. Lukt Claude niet (CLI niet beschikbaar), dan wordt een eenvoudige standaardmail aangeboden.
- In het voorbeeldvenster zijn ontvangers, onderwerp en tekst bewerkbaar; het **feedbackveld** laat Claude de tekst herwerken (bv. "korter", "aanhef in het Frans"). **Versturen** gaat via de Gmail-SMTP-instellingen van de mailassistent (na bevestiging); **Kopiëren** zet de tekst op het klembord. De ontvangers worden onthouden voor de volgende week.
- Na het versturen ruim je afgewerkte taken op met **"Afgevinkte opruimen"**; open taken blijven staan en komen volgende week opnieuw in de mail.
- Data staat in `%APPDATA%\WorkManager\team-tasks.json` (leden, taken, opmerking, ontvangers) en `team-mail-style.txt` (stijlvoorbeelden).

## Mail beantwoorden (Gmail)

Via het tray-menu **"Mail beantwoorden (Gmail)…"** opent een venster dat de inbox uitleest en voor elke mail waar dat van toepassing is een conceptantwoord klaarzet:

- Bij het openen (en via **Mails ophalen**) leest het venster de inbox via IMAP (standaard alle mails, max. 25) en laat het Claude — via de Claude Code CLI (`claude -p`), op het bestaande abonnement, geen API-key nodig — per mail beoordelen of een persoonlijk antwoord zinvol is. Nieuwsbrieven, reclame, notificaties en no-reply-afzenders worden overgeslagen — die staan in het oranje in de lijst met de reden erbij.
- Links staat de maillijst, rechts de originele mail (met opmaak, zoals in Gmail) en het **bewerkbare concept**. Alle acties (versturen, archiveren, snoozen) werken op de **geselecteerde** rijen — meerdere selecteren kan met Ctrl/Shift-klik; versturen slaat geselecteerde mails zonder concept over. Rechtsklik op een mail → *"Concept opnieuw genereren"*. Wisselen van selectie bewaart je aanpassingen.
- Concepten (ook handmatige bewerkingen) worden per Message-ID bewaard in `%APPDATA%\WorkManager\mail-reply-concepts.json`: bij herladen of heropenen worden alleen **nieuwe** mails door Claude beoordeeld. Na het versturen (of na 90 dagen) verdwijnt een concept uit de cache; opnieuw genereren kan altijd via het rechtsklikmenu.
- Onder het concept staat een **feedbackveld**: typ wat er anders moet (bv. "korter", "vermeld dat ik vrijdag afwezig ben") en druk op Enter of *"Pas concept aan"* — Claude herwerkt het concept volgens de feedback, opnieuw strikt volgens de stijl-skill uit de instructies.
- **Archiveren** (werkbalkknop of rechtsklik) haalt de geselecteerde mails uit de inbox zoals in Gmail: ze blijven bewaard onder "Alle berichten".
- Mails met bijlagen tonen een 📎; rechtsklik → *"Bijlagen opslaan op Google Drive…"* opent een dialoog waarin je per bijlage aanvinkt wat je bewaart en de bestandsnaam kiest (vooraf ingevuld als `jjMMdd originele naam`, bv. `260723 factuur.pdf`), plus de doelmap (standaard `G:\Mijn Drive\administratie`). Google Drive synchroniseert de bestanden daarna zelf. Ook **download-links naar PDF's** in de mailtekst (bv. Stripe-facturen "Download invoice" bij Anthropic) worden herkend en als linkbijlage aangeboden — de app downloadt die dan zelf.
- Rechtsklik → *"Bijlage doorsturen naar Billit…"* toont dezelfde bijlagenkeuze (echte bijlagen én linkbijlagen; standaard niets aangevinkt, tenzij er maar één is) en mailt de **aangevinkte** bijlagen door naar het Billit-inboxadres uit de instellingen (standaard `bermacon-uminyqd-nosplit@my.billit.be`).
- **Snoozen…** (werkbalkknop of rechtsklik) haalt de mail tijdelijk uit de inbox en zet hem er automatisch terug op het gekozen moment (met tray-melding), ook als het venster dicht is. De dialoog toont bovenaan een **voorstel dat bijleert**: kies je een ander moment, dan wordt die keuze onthouden (`mail-snooze-history.json`) en wordt de vaakst gekozen combinatie van "dagen vooruit + uur" het volgende voorstel. Openstaande snoozes staan in `mail-snoozes.json`; het concept blijft in de cache klaarstaan voor als de mail terugkomt.
- **Geselecteerde versturen…** toont eerst een bevestiging; daarna gaan de antwoorden via SMTP de deur uit als reply in de juiste thread (In-Reply-To/References), worden de originele mails in Gmail als beantwoord + gelezen gemarkeerd en meteen **gearchiveerd** (uit de inbox, blijven in "Alle berichten"). Niets vertrekt zonder jouw klik.
- **Instructies beheren…** opent de vrije tekst die Claude bij elke mail meekrijgt (toon, ondertekening, wat wel/niet beantwoorden) — persistent in `%APPDATA%\WorkManager\mail-reply-instructions.txt`.
- **Instellingen…**: e-mailadres, Gmail-**app-wachtwoord** (aanmaken op myaccount.google.com/apppasswords, vereist tweestapsverificatie), max. aantal mails en wel/niet alleen ongelezen. Het wachtwoord staat DPAPI-versleuteld in `%APPDATA%\WorkManager\mail-reply-settings.json`.

## Bouwen en starten

```powershell
dotnet publish -c Release -o publish
.\publish\WorkManager.exe
```

## Contexten toevoegen of wijzigen

Pas de `Clients`-array aan in `TrayAppContext.cs` (naam + kleur) en herbouw.
