# WorkManager voice-API + AH-bestelpagina — deployen

## 1. Configuratie

`config.php` bevat de MySQL-gegevens en het token; beide staan al ingevuld.
Hetzelfde token staat versleuteld op de pc in `%APPDATA%\WorkManager\voice-settings.json`.

## 2. Uploaden

Draai `deploy.bat` in deze map (gebruikt de deploytool, profiel `default` in
`deployconfig.json`); dat zet `api.php` en `config.php` via SFTP in
`subsites/workmanager.urbanit.be/`, bereikbaar op:

    https://workmanager.urbanit.be/api.php

De databasetabel (`wm_voice_sessies`) wordt automatisch aangemaakt bij het eerste
gebruik; er hoeft geen SQL-script gedraaid te worden.

## 3. Testen

Verbindingstest (vervang TOKEN door de waarde uit config.php):

    curl "https://workmanager.urbanit.be/api.php?actie=ping" -H "X-Wm-Token: TOKEN"

Verwacht: `{"ok":true,"tijd":"..."}`. Daarna een volledige testronde:

    curl -X POST "https://workmanager.urbanit.be/api.php?actie=commando" \
      -H "X-Wm-Token: TOKEN" -H "Content-Type: application/json" \
      -d "{\"tekst\": \"maak een taak: testje via curl\"}"

Dat geeft `{"sessie":"..."}`. Zolang WorkManager op de pc draait, verschijnt binnen
±30 seconden het antwoord via:

    curl "https://workmanager.urbanit.be/api.php?actie=ophalen&sessie=SESSIE-ID" -H "X-Wm-Token: TOKEN"

(fase "beantwoord" = vervolgvraag verwacht; met een POST `actie=antwoord` en
`{"sessie": "...", "tekst": "ja"}` bevestig je, waarna fase "klaar" volgt en de
taak op de pc in Mijn taken staat.)

## 4. Siri Shortcut

Zie `SIRI-SHORTCUT.md` voor het stappenplan op de iPhone.

## 5. AH-bestelpagina (ah.php)

Mobielvriendelijke boodschappenpagina (voor op de gsm), met een eigen token
(`ah_token` in config.php — bewust apart van het voice-token). De gsm-link is:

    https://workmanager.urbanit.be/ah.php?t=AH_TOKEN

De pc (AhWebSync, instellingen in `%APPDATA%\WorkManager\ah-web-settings.json`)
zet elke ± 6 uur een snapshot van de gerechten/prijzen klaar en pollt elke 30 s
de bestelwachtrij; een binnengekomen bestelling wordt via het winkelvenster in
het echte AH-mandje gelegd. Tabellen `wm_ah_snapshot` en `wm_ah_bestellingen`
worden automatisch aangemaakt; bestellingen worden na 14 dagen opgeruimd.

Testen (vervang AH_TOKEN):

    curl "https://workmanager.urbanit.be/ah.php?actie=data" -H "X-Wm-Token: AH_TOKEN"
    curl "https://workmanager.urbanit.be/ah.php?actie=ahwerk" -H "X-Wm-Token: AH_TOKEN"

## 6. Persoonlijke WorkManager-pagina (wm.php)

Het cockpitbeeld onderweg. Acht tabbladen:

- **Plan** — de dagplanning van vandaag, met de reden erbij waarom iets daar
  staat. Per taak **Timer starten** en **Afvinken**; loopt er een timer, dan
  staat die bovenaan met de verstreken tijd en een stopknop. Stoppen boekt de
  tijd meteen als timesheetregel (onder de drie minuten niet).
- **Taken** — te laat / vandaag / verder, met afvinken en verzetten (3 u of tot
  morgen) en een zoekveld. Onderaan een inklapbare lijst **Later**: taken met
  een startdatum in de toekomst of die nog snoozen.
- **Agenda** — vandaag en morgen (eigen agenda + CED), met "bezig" op de lopende
  afspraak. Bij een afspraak die al bezig of voorbij is staat **Uren boeken**:
  dat springt naar het urenformulier met titel, duur en klant al ingevuld.
  Geplande maaltijden (🍴) krijgen die knop niet — dat is geen werktijd.
- **Berichten** — wachtende mails en chats, met **Antwoorden…**, **Archiveren**,
  **👍** (Google Chat), **Vanavond**/**Morgen** (snoozen) en **Lezen** (eerste
  alinea's). Staat er al een concept van Claude klaar, dan heet de knop
  **Concept…** en zit dat concept meteen in het antwoordvak. Mail gaat als reply
  in de thread via SMTP en wordt daarna gearchiveerd; snoozen gebruikt het
  Gmail-label "Gesnoozed" en de tray-app zet de mail op tijd terug. Outlook,
  Teams en WhatsApp kunnen alleen archiveren/gelezen zetten, via de duurzame
  actiewachtrij — antwoorden en snoozen hebben daar de ingelogde sessie op de pc
  voor nodig.
- **Uren** — stand van vandaag en van de week per klant, een formulier om ter
  plaatse uren te boeken, **Voorstel maken…** (Claude zet je activiteitenlog om in
  regels die je regel voor regel aan- of uitvinkt vóór je ze boekt) en
  **Doorboeken naar urbanadmin**.
- **Klanten** — de klantdossiers: openstaande punten bovenaan, het volledige
  dossier eronder om na te lezen, en telefoonnummers als beltoets. Bedoeld om in
  de auto vlak voor een bezoek nog even door te nemen.
- **Team** — openstaande teamtaken per lid, afvinkbaar, met een veld om er een
  bij te maken.
- **Starten** — Claude of PhpStorm op de pc laten opstarten in een projectmap
  terwijl je onderweg bent. De pagina stuurt alleen een sleutel door, geen pad of
  commando.

Bij een mail met bijlagen staat **Naar Drive**: die zet alle bijlagen in de map
waar je op de pc het laatst iets in opsloeg, zodat je ze in de Drive-app kunt
openen. Bijlagen komen bewust niet op de webserver te staan.

Onderaan staat altijd een invoerveld voor een nieuwe taak, met een
microfoonknop als de browser spraakherkenning aankan (anders doet de
dicteertoets van het toetsenbord hetzelfde). Die zin gaat langs `claude -p`
(dezelfde parser als de spraakcommando's), dus "Nicolas bellen over de
plaatsingsprijzen, moet maandag af" krijgt meteen categorie Lauryssens en
deadline maandag. Lukt dat niet, dan komt de zin er letterlijk in.

### Locatie

De pagina stuurt bij het openen één grove positie door (wifi/zendmast, geen GPS,
cache van vijf minuten) — dat kost praktisch geen batterij, maar werkt alleen
zolang de pagina openstaat. Bovenaan verschijnt dan 📍 met de plek; op het
urentabblad kun je de huidige positie een naam geven, en die plek wordt daarna
herkend binnen 250 m.

Voor registratie **zonder iets te openen** zijn er de iOS-automatiseringen: zie
`LOCATIE-SHORTCUT.md`. Aankomst en vertrek komen via `wm.php?actie=plek` binnen;
de pc maakt er bezoeken van en zet elk afgerond bezoek van ≥ 15 minuten als
voorstelregel bij je uren (nooit meteen een boeking). Tabel `wm_locaties`, na
zeven dagen automatisch gewist.

### Pushmeldingen

Optioneel, via [ntfy.sh](https://ntfy.sh) — geen account nodig. Zet in het
venster **WorkManager online…** een push-topic (knop "Topic verzinnen" maakt een
willekeurige), installeer de ntfy-app op de gsm en abonneer je op datzelfde
topic. Let op: het topic ís het geheim, wie het kent leest mee — daarom staat
het DPAPI-versleuteld in `wm-web-settings.json`.

De pc pusht spaarzaam en per onderwerp hooguit één keer per dag: een urgente
mail, tussen 16 en 20 u de taken die vandaag verlopen, en de dagafsluiter.
Tikken op de melding opent de webversie.

Alleen voor Maarten, dus met een derde token (`wm_token` in config.php — los van
`ah_token`, zodat de AH-link van thuis niet bij taken en mail kan). De link is:

    https://workmanager.urbanit.be/wm.php?t=WM_TOKEN

Instellen op de pc: tray-menu → **WorkManager online…** — daar staan het adres,
het token en een QR-code om de link naar de gsm te scannen. De instellingen komen
in `%APPDATA%\WorkManager\wm-web-settings.json` (token DPAPI-versleuteld).

De pc (WmWebSync) pollt elke 30 s: hij zet een vers snapshot klaar zodra er iets
verandert (en sowieso elke 4 minuten) en voert de acties van de pagina uit —
taak afvinken, 3 uur/tot morgen verzetten, en nieuwe taken. Tabellen `wm_snapshot`
en `wm_acties` worden automatisch aangemaakt; acties worden na 7 dagen opgeruimd.
Staat de pc uit, dan blijft een actie in de wachtrij staan tot hij weer aangaat;
de pagina meldt dat ook zo.

Zonder bereik toont de pagina de laatst bekende stand met een gele balk erboven
("dit is de stand van …"); wat je dan aantikt, kan de pc niet bereiken. Bij een
agenda-afspraak met locatie staat er een **Route**-knop naar Google Maps.

Op het beginscherm zetten: open de link op de gsm en kies "Zet op beginscherm".
De pagina levert een manifest (`wm.php?actie=manifest`) en `wm-icon-192.png` /
`wm-icon-512.png`, dus hij opent fullscreen met het WorkManager-icoon.

Testen (vervang WM_TOKEN):

    curl "https://workmanager.urbanit.be/wm.php?actie=versie" -H "X-Wm-Token: WM_TOKEN"
    curl "https://workmanager.urbanit.be/wm.php?actie=data" -H "X-Wm-Token: WM_TOKEN"
    curl "https://workmanager.urbanit.be/wm.php?actie=wmwerk" -H "X-Wm-Token: WM_TOKEN"

> **Let op — opcache.** De hosting compileert PHP en houdt dat ongeveer een
> minuut vast. Vlak na `deploy.bat` draait je oude code nog (soms met een kale
> HTTP 500 als de oude en nieuwe helft niet samengaan). Wacht een minuut en test
> opnieuw voor je gaat zoeken naar een fout die er niet is; `actie=versie` geeft
> de versiemarkering uit `wm.php`, zodat je zeker weet welke code er draait.

## Hoe het werkt

    telefoon ──commando/antwoord──▶ api.php + MySQL ◀──poll werk/resultaat── WorkManager (pc)
             ◀──voorleesbaar antwoord──          (claude -p parseert het gesprek)

- Sessies worden na 24 uur automatisch opgeruimd.
- De pc pollt elke 15 s (instelbaar via `pollSeconden` in voice-settings.json);
  reken in de auto op ±15–30 s tussen inspreken en antwoord.
- Staat de pc uit, dan blijft de sessie wachten (de Shortcut meldt na ±60 s dat er
  nog geen antwoord is; de taak gaat NIET verloren zolang de sessie < 24 u oud is —
  maar zonder bevestigingsronde wordt hij ook niet doorgevoerd).
