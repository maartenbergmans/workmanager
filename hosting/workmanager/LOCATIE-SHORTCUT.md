# Aankomst en vertrek automatisch registreren (iPhone)

Hiermee schrijft je telefoon zelf op wanneer je bij een klant aankomt en weer
vertrekt — **zonder dat je WorkManager of de webversie hoeft te openen**, en
zonder merkbaar batterijverbruik. Het is de energiezuinige locatiedienst van iOS
die dat doet, dezelfde die "Zoek mijn iPhone" en verkeersmeldingen voedt; er
draait geen app van ons op de achtergrond.

Van elk afgerond bezoek van 15 minuten of langer maakt de pc een **voorstelregel**
bij je uren — nooit meteen een boeking. Je keurt het goed op het urentabblad van
de webversie.

## Eén keer per adres instellen

Doe dit voor elk adres dat de moeite is: Lauryssens, Vriesveem, Nemijtek, CED.
Per adres maak je twéé automatiseringen (aankomst en vertrek).

1. Open **Opdrachten** (Shortcuts) → tabblad **Automatisering** → **+**.
2. Kies **Aankomst** (voor de vertrek-versie: **Vertrek**).
3. Bij **Locatie**: kies het adres van de klant. Zet het bereik desnoods wat
   ruimer als het terrein groot is.
4. **Tijdsbereik**: laat op elk moment staan, of beperk het tot je werkuren —
   dat scheelt ruis als je er 's avonds langsrijdt.
5. Zet **Direct uitvoeren** aan en **Vraag voor uitvoeren** uit. Anders krijg je
   elke keer een melding die je moet bevestigen, en dat is precies wat we niet
   willen.
6. Voeg één actie toe: **Haal inhoud op van URL** (*Get Contents of URL*).
   Gebruik niet "Toon webpagina" — dat zou telkens een browser openen.
7. Vul als URL in (alles op één regel):

       https://workmanager.urbanit.be/wm.php?actie=plek&t=WM_TOKEN&soort=aankomst&plek=Lauryssens

   - `WM_TOKEN` is het token uit `config.php`; het staat ook in het venster
     **WorkManager online…** op de pc (knop "Link kopiëren" — het stuk na `?t=`).
   - `soort` is `aankomst` of `vertrek`.
   - `plek` is de naam die je in WorkManager wilt zien. Zit de klantnaam erin
     zoals de timesheets hem kennen (Lauryssens, CED, Aqurat…), dan kiest de pc
     meteen de juiste klant voor de voorstelregel.

8. Bewaar. Herhaal met **Vertrek** en `soort=vertrek`.

## Controleren dat het werkt

Open die URL één keer in Safari; je hoort `{"ok":true,...}` terug te krijgen.
Binnen een halve minuut staat de plek in de webversie bovenaan in de kop
(📍 Lauryssens) en na het vertrek verschijnt het bezoek onder **Waar je was** op
het urentabblad.

## Waarom de webversie zélf niet volstaat

De webpagina meet ook je positie, maar alleen op het moment dat je haar opent:
een browser mag op iOS geen locatie lezen als hij niet op de voorgrond staat.
Die meting is bewust grof (wifi en zendmasten, geen GPS) en kost daardoor
praktisch geen batterij — ze is handig om "waar ben ik nu" te tonen en om een
plek een naam te geven, maar ze registreert je dag niet. Daarvoor zijn de
automatiseringen hierboven.

## Privacy en opruimen

De posities staan op je eigen hosting, in de tabel `wm_locaties`, afgerond op
ongeveer tien meter, en ze worden na zeven dagen automatisch gewist. Ook lokaal
(`%APPDATA%\WorkManager\locatie-log.json`) blijft er niet meer dan een week
staan. Voor timesheets heb je niet meer nodig.
