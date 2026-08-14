# Siri Shortcut "Taak maken" — klik-voor-klik (iPhone)

Eenmalig in te stellen in de app **Opdrachten** (het witte icoon met twee gekleurde
tegels; standaard op elke iPhone). Daarna werkt alles hands-free, ook via CarPlay:
"Hé Siri, taak maken" → inspreken → antwoord wordt voorgelezen → "ja" zeggen of
corrigeren → klaar.

De assistent kan: taken aanmaken (eigen + team), taken laten voorlezen, afvinken of
snoozen, mails laten voorlezen of archiveren, en de agenda van de komende week vragen.

Je hebt het token nodig uit `config.php` — overal waar hieronder **TOKEN** staat,
vul je die lange tekenreeks in. Handig: mail het token even naar jezelf zodat je het
op de iPhone kan kopiëren en plakken.

---

## Opdracht aanmaken

1. Open **Opdrachten** → tabblad **Opdrachten** onderaan → tik rechtsboven op **+**.
2. Tik bovenaan op "Nieuwe opdracht" → **Wijzig naam** → typ **Taak maken**.
   (De naam is meteen het spraakcommando: "Hé Siri, taak maken".)

Nu voeg je 12 acties toe. Elke actie zoek je via de balk **"Zoek naar acties"**
onderaan het scherm: typ de zoekterm, tik op de actie in de lijst, en hij verschijnt
onder de vorige. Klap bij een actie de extra opties open via het pijltje (>) in het
actieblok.

---

## De 12 acties

**Actie 1 — zoek "Dicteer tekst"**
- Pijltje openklappen: **Taal** = Nederlands, **Stop met luisteren** = Na pauze.

**Actie 2 — zoek "Haal inhoud van URL op"** *(het commando insturen)*
- Tik op het URL-veld en typ/plak:
  `https://workmanager.urbanit.be/api.php?actie=commando`
- Pijltje openklappen:
  - **Methode** = POST
  - **Headers**: tik op "Nieuwe header toevoegen" → Sleutel `X-Wm-Token`, Tekst **TOKEN**
  - **Hoofdtekst aanvragen** = JSON
  - Tik op "Nieuw veld toevoegen" → **Tekst** → Sleutel `tekst` → tik in het
    waardeveld en kies boven het toetsenbord de blauwe variabele **Gedicteerde tekst**.

**Actie 3 — zoek "Haal woordenboekwaarde op"**
- In de actie staat "Haal **waarde** voor **sleutel** in **URL-inhoud**":
  tik op "sleutel" en typ `sessie`.

**Actie 4 — zoek "Stel variabele in"**
- Tik op "Naam variabele" en typ `Sessie`. (De invoer staat vanzelf op
  "Woordenboekwaarde" — zo onthouden we het sessienummer.)

**Actie 5 — zoek "Herhaal"** *(kies "Herhaal", níét "Herhaal met elk")*
- Zet het aantal op **3** (tik op het cijfer). Er verschijnen twee blokken:
  "Herhaal 3 keer" en "Einde herhaling".
- **Belangrijk:** de acties 6 t/m 12 moeten TUSSEN die twee blokken staan. Nieuwe
  acties komen daar vanzelf als je ze toevoegt terwijl "Einde herhaling" onderaan
  staat; belandt er toch één buiten, sleep het blok dan ertussen.

**Actie 6 — zoek "Haal inhoud van URL op"** *(wachten op het antwoord van de pc)*
- URL-veld, typ/plak: `https://workmanager.urbanit.be/api.php?actie=ophalen&wacht=45&sessie=`
  en kies dan — met de cursor direct achter het =-teken — boven het toetsenbord de
  variabele **Sessie**.
- Pijltje openklappen: **Methode** = GET, en dezelfde header:
  `X-Wm-Token` = **TOKEN**.
- Deze aanroep wacht zelf (tot 45 seconden) tot de pc geantwoord heeft — daarom is
  er geen aparte wachtlus nodig.

**Actie 7 — zoek "Haal woordenboekwaarde op"**
- Sleutel: `antwoord`. (Het woordenboek staat vanzelf op "URL-inhoud" van actie 6.)

**Actie 8 — zoek "Spreek tekst uit"**
- De invoer is vanzelf de Woordenboekwaarde van actie 7.
- Pijltje openklappen: **Taal** = Nederlands en **Wacht tot voltooid** = AAN.

**Actie 9 — zoek "Haal woordenboekwaarde op"**
- Sleutel: `fase`.
- Hier staat het woordenboek NIET vanzelf goed: tik op het (lege) woordenboek-veld
  en kies de variabele **URL-inhoud** — let op, die van **actie 6** (de bovenste
  "URL-inhoud" in de lijst binnen de herhaling).

**Actie 10 — zoek "Als"**
- De invoer is vanzelf de Woordenboekwaarde van actie 9.
- Voorwaarde: **is** → tik op "Kies" en typ `klaar`.
- Er verschijnen "Als …", "Anders" en "Einde Als"; het blok "Anders" mag je
  verwijderen (tik erop → verwijder).
- Zoek nu **"Stop deze opdracht"** en zorg dat die actie BINNEN het Als-blok staat
  (tussen "Als" en "Einde Als" — zonodig slepen).

**Actie 11 — zoek "Dicteer tekst"** *(jouw "ja" of correctie)*
- Weer: Taal = Nederlands, Stop met luisteren = Na pauze.
- Deze moet NÁ "Einde Als" staan, maar nog binnen de herhaling.

**Actie 12 — zoek "Haal inhoud van URL op"** *(jouw antwoord terugsturen)*
- URL: `https://workmanager.urbanit.be/api.php?actie=antwoord`
- Pijltje openklappen:
  - **Methode** = POST · **Headers**: `X-Wm-Token` = **TOKEN**
  - **Hoofdtekst aanvragen** = JSON, met twee velden (Tekst):
    - Sleutel `sessie` → waarde = variabele **Sessie**
    - Sleutel `tekst` → waarde = variabele **Gedicteerde tekst** — let op: kies de
      dictering van **actie 11** (de onderste in de lijst).

Tik rechtsboven op **Gereed**. Klaar!

---

## Eerste test

Doe de eerste run met het scherm aan: tik op de opdracht in de app en spreek iets in,
bv. "zet op mijn lijstje dat ik morgen de offerte moet nakijken". De iPhone vraagt
eenmalig toestemming voor dicteren en voor de verbinding met workmanager.urbanit.be
("Sta toe"). Binnen ±20–40 seconden hoor je het voorstel; zeg "ja" en de taak staat
op de pc in Mijn taken. Daarna werkt het ook volledig via "Hé Siri, taak maken" met
vergrendeld scherm en in de auto.

## Voorbeelden van wat je kan zeggen

- "Zet op mijn lijstje dat ik dinsdag de offerte moet nakijken, dringend."
- "Taak voor Wim: de staging-omgeving fixen." / "Twee taken voor Kris: …"
- "Wat staat er op mijn lijstje?" / "Welke taken heeft Christophe?"
- "Vink de offerte-taak af." / "Snooze die taak tot maandag."
- "Zitten er nog mails in mijn inbox?" / "Archiveer die eerste mail."
- "Wat staat er morgen in mijn agenda?"
- Corrigeren: "nee, voor Kris", "zonder deadline", "maak er lage prioriteit van."
- Annuleren: "laat maar."

## Tips

- Hernoem de opdracht gerust naar bv. "assistent" — de naam is het spraakcommando.
- Wil je langere gesprekken (eerst voorlezen, dan een paar dingen doen), zet de
  herhaling in actie 5 dan op 5 in plaats van 3.
- De pc thuis moet aanstaan (WorkManager draait in de tray).
- Antwoordt de pc even niet (uit/slaapstand), dan zegt de opdracht niets na ±45
  seconden en stopt hij vanzelf na de resterende rondes; het commando gaat niet
  verloren zolang je binnen 24 uur een nieuwe poging doet in dezelfde sessie — in
  de praktijk: gewoon opnieuw proberen.
