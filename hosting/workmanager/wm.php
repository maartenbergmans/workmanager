<?php
/**
 * Persoonlijke WorkManager-webpagina: het beeld van de cockpit, maar dan op de gsm of op
 * een andere pc. Alleen voor Maarten — vandaar een eigen token (wm_token), los van het
 * ah_token van de bestelpagina en van het voice-token.
 *
 * De pc (WmWebSync in WorkManager) zet periodiek een snapshot klaar met taken, agenda,
 * wachtende berichten en de urenstand. De pagina toont dat en kan er acties op terugsturen:
 * een taak afvinken, snoozen of bijmaken; een bericht beantwoorden (met het concept dat de
 * pc al klaarzette), archiveren, snoozen of er een duim op zetten; uren boeken, een
 * dagvoorstel laten maken en doorboeken; teamtaken afvinken of bijmaken; en een project op
 * de pc laten opstarten. Die
 * belanden in een wachtrij die de pc oppikt; de pagina toont daarna wat ermee gebeurd is
 * (en meldt netjes als de pc uit staat).
 *
 * Acties (token = wm_token uit config.php, via header X-Wm-Token of parameter t/token):
 *   GET  (geen actie)                 -> de webpagina zelf (token in de link: wm.php?t=…)
 *   GET  data                         -> {snapshot, bijgewerkt}
 *   POST snapshot   {snapshot}        -> {ok}                              (van de pc)
 *   POST actie      {inhoud}          -> {id}                              (van de gsm)
 *   GET  status     ?id=...           -> {status, melding}
 *   GET  wmwerk                       -> {acties: [{id, inhoud}]}          (voor de pc)
 *   POST wmklaar    {id, melding}     -> {ok}                              (van de pc)
 *   POST locatie    {lat,lon,acc}     -> {ok}       (grove positie van de gsm)
 *   GET  plek       ?plek=..&soort=..  -> {ok}       (iOS-automatisering: aankomst/vertrek)
 *   GET  locwerk                      -> {punten}   (voor de pc)
 *   POST locklaar   {ids}             -> {ok}       (van de pc)
 *   GET  manifest                     -> web-app-manifest (voor op het beginscherm)
 *   GET  versie                       -> {versie}   (welke code draait er echt; zie DEPLOY.md
 *                                                    over de opcache van een minuut)
 */

declare(strict_types=1);

$config = require __DIR__ . '/config.php';

function antwoord(array $data, int $status = 200): never
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode($data, JSON_UNESCAPED_UNICODE);
    exit;
}

// ---- Token controleren (constant-time vergelijking) ----
$token = $_SERVER['HTTP_X_WM_TOKEN'] ?? $_REQUEST['token'] ?? $_REQUEST['t'] ?? '';
$actie = $_REQUEST['actie'] ?? '';
if (!is_string($token) || !hash_equals($config['wm_token'], $token)) {
    if ($actie === '') {
        http_response_code(403);
        header('Content-Type: text/html; charset=utf-8');
        echo '<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
           . '<body style="font-family:system-ui;padding:2em;text-align:center">'
           . '<h2>Deze link klopt niet</h2><p>Vraag de juiste link opnieuw op in WorkManager.</p>';
        exit;
    }
    antwoord(['fout' => 'ongeldig token'], 401);
}

// ---- De pagina zelf (geen database nodig) ----
if ($actie === '') {
    header('Content-Type: text/html; charset=utf-8');
    echo str_replace('__TOKEN__', htmlspecialchars($token, ENT_QUOTES), pagina());
    exit;
}

// ---- Manifest: maakt "zet op beginscherm" een echte app-tegel ----
if ($actie === 'manifest') {
    $t = rawurlencode($token);
    header('Content-Type: application/manifest+json; charset=utf-8');
    echo json_encode([
        'name' => 'WorkManager',
        'short_name' => 'WorkManager',
        'description' => 'Taken, agenda, berichten en uren onderweg.',
        // Het token hoort in de start-URL: anders opent de tegel op een 403.
        'start_url' => "wm.php?t=$t",
        'scope' => './',
        'display' => 'standalone',
        'background_color' => '#0f0f16',
        'theme_color' => '#12121a',
        'lang' => 'nl',
        'icons' => [
            ['src' => 'wm-icon-192.png', 'sizes' => '192x192', 'type' => 'image/png', 'purpose' => 'any'],
            ['src' => 'wm-icon-512.png', 'sizes' => '512x512', 'type' => 'image/png', 'purpose' => 'any'],
        ],
    ], JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;
}

// ---- Database ----
try {
    $db = new PDO($config['db_dsn'], $config['db_gebruiker'], $config['db_wachtwoord'], [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
    ]);
} catch (PDOException $e) {
    antwoord(['fout' => 'databaseverbinding mislukt'], 500);
}

$db->exec("CREATE TABLE IF NOT EXISTS wm_snapshot (
    id TINYINT NOT NULL PRIMARY KEY,
    inhoud MEDIUMTEXT NOT NULL,
    bijgewerkt DATETIME NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

$db->exec("CREATE TABLE IF NOT EXISTS wm_acties (
    id CHAR(24) NOT NULL PRIMARY KEY,
    inhoud MEDIUMTEXT NOT NULL,
    status ENUM('wacht','verwerkt') NOT NULL DEFAULT 'wacht',
    melding VARCHAR(300) NOT NULL DEFAULT '',
    aangemaakt DATETIME NOT NULL,
    bijgewerkt DATETIME NOT NULL,
    INDEX idx_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

$db->exec("CREATE TABLE IF NOT EXISTS wm_locaties (
    id INT AUTO_INCREMENT PRIMARY KEY,
    soort ENUM('punt','aankomst','vertrek') NOT NULL DEFAULT 'punt',
    lat DOUBLE NULL,
    lon DOUBLE NULL,
    nauwkeurig INT NULL,
    plek VARCHAR(60) NULL,
    moment DATETIME NOT NULL,
    verwerkt TINYINT NOT NULL DEFAULT 0,
    INDEX idx_verwerkt (verwerkt)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

$db->exec("DELETE FROM wm_acties WHERE aangemaakt < (NOW() - INTERVAL 7 DAY)");
// Posities zijn alleen nuttig voor de timesheets van deze week; daarna weg.
$db->exec("DELETE FROM wm_locaties WHERE moment < (NOW() - INTERVAL 7 DAY)");

// ---- Invoer ----
$body = [];
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $raw = file_get_contents('php://input');
    if (is_string($raw) && $raw !== '') {
        $decoded = json_decode($raw, true);
        if (is_array($decoded)) {
            $body = $decoded;
        }
    }
    $body += $_POST;
}

switch ($actie) {
    case 'data': {
        $rij = $db->query('SELECT inhoud, bijgewerkt FROM wm_snapshot WHERE id = 1')->fetch();
        if ($rij === false) {
            antwoord(['snapshot' => null, 'bijgewerkt' => null]);
        }
        antwoord([
            'snapshot' => json_decode($rij['inhoud'], true),
            // ISO-8601 mét tijdzone: anders moet elke lezer raden of dit UTC of lokale tijd
            // is, en dan zit je er stilletjes uren naast.
            'bijgewerkt' => date('c', strtotime($rij['bijgewerkt'])),
        ]);
    }

    case 'snapshot': {
        $snapshot = $body['snapshot'] ?? null;
        if (!is_array($snapshot)) {
            antwoord(['fout' => 'snapshot ontbreekt'], 400);
        }
        $db->prepare('REPLACE INTO wm_snapshot (id, inhoud, bijgewerkt) VALUES (1, :inhoud, NOW())')
            ->execute(['inhoud' => json_encode($snapshot, JSON_UNESCAPED_UNICODE)]);
        antwoord(['ok' => true]);
    }

    case 'actie': {
        $inhoud = $body['inhoud'] ?? null;
        if (!is_array($inhoud) || !isset($inhoud['soort'])) {
            antwoord(['fout' => 'inhoud ontbreekt'], 400);
        }
        $id = bin2hex(random_bytes(12));
        $db->prepare("INSERT INTO wm_acties (id, inhoud, aangemaakt, bijgewerkt)
                      VALUES (:id, :inhoud, NOW(), NOW())")
            ->execute(['id' => $id, 'inhoud' => json_encode($inhoud, JSON_UNESCAPED_UNICODE)]);
        antwoord(['id' => $id]);
    }

    case 'status': {
        $id = $_REQUEST['id'] ?? '';
        $stmt = $db->prepare('SELECT status, melding FROM wm_acties WHERE id = :id');
        $stmt->execute(['id' => is_string($id) ? $id : '']);
        $rij = $stmt->fetch();
        if ($rij === false) {
            antwoord(['fout' => 'onbekend'], 404);
        }
        antwoord(['status' => $rij['status'], 'melding' => $rij['melding']]);
    }

    case 'locatie': {
        // Grove positie vanaf de webpagina. Bewust een eigen route en geen "actie": hier
        // hoeft niemand op te wachten, en het mag de wachtrij van echte acties niet vullen.
        $lat = isset($body['lat']) ? (float)$body['lat'] : null;
        $lon = isset($body['lon']) ? (float)$body['lon'] : null;
        if ($lat === null || $lon === null || abs($lat) > 90 || abs($lon) > 180) {
            antwoord(['fout' => 'lat/lon ontbreekt'], 400);
        }
        $db->prepare("INSERT INTO wm_locaties (soort, lat, lon, nauwkeurig, moment)
                      VALUES ('punt', :lat, :lon, :acc, NOW())")
            ->execute([
                'lat' => round($lat, 4),   // ~10 m: genoeg om een klant te herkennen
                'lon' => round($lon, 4),
                'acc' => isset($body['acc']) ? (int)$body['acc'] : null,
            ]);
        antwoord(['ok' => true]);
    }

    case 'plek': {
        // Voor de iOS-automatisering "wanneer ik aankom bij / vertrek van X": één GET, zodat
        // een Shortcut niets meer hoeft te doen dan een URL openen.
        $plek = trim((string)($_REQUEST['plek'] ?? ''));
        $soort = ($_REQUEST['soort'] ?? '') === 'vertrek' ? 'vertrek' : 'aankomst';
        if ($plek === '') {
            antwoord(['fout' => 'plek ontbreekt'], 400);
        }
        $db->prepare("INSERT INTO wm_locaties (soort, plek, moment) VALUES (:soort, :plek, NOW())")
            ->execute(['soort' => $soort, 'plek' => mb_substr($plek, 0, 60)]);
        antwoord(['ok' => true, 'plek' => $plek, 'soort' => $soort]);
    }

    case 'locwerk': {
        $stmt = $db->query("SELECT id, soort, lat, lon, nauwkeurig, plek, moment
                            FROM wm_locaties WHERE verwerkt = 0 ORDER BY id ASC LIMIT 200");
        $punten = [];
        foreach ($stmt as $rij) {
            $rij['moment'] = date('c', strtotime($rij['moment']));
            $punten[] = $rij;
        }
        antwoord(['punten' => $punten]);
    }

    case 'locklaar': {
        $ids = array_values(array_filter(array_map('intval', (array)($body['ids'] ?? []))));
        if ($ids) {
            $db->exec('UPDATE wm_locaties SET verwerkt = 1 WHERE id IN (' . implode(',', $ids) . ')');
        }
        antwoord(['ok' => true]);
    }

    case 'wmwerk': {
        $stmt = $db->query("SELECT id, inhoud FROM wm_acties
                            WHERE status = 'wacht' ORDER BY aangemaakt ASC LIMIT 20");
        $acties = [];
        foreach ($stmt as $rij) {
            $acties[] = ['id' => $rij['id'], 'inhoud' => json_decode($rij['inhoud'], true)];
        }
        antwoord(['acties' => $acties]);
    }

    case 'wmklaar': {
        $id = $body['id'] ?? '';
        $melding = $body['melding'] ?? '';
        $db->prepare("UPDATE wm_acties SET status = 'verwerkt', melding = :melding, bijgewerkt = NOW()
                      WHERE id = :id")
            ->execute([
                'id' => is_string($id) ? $id : '',
                'melding' => mb_substr(is_string($melding) ? $melding : '', 0, 300),
            ]);
        antwoord(['ok' => true]);
    }

    case 'versie':
        antwoord(['versie' => '2026-08-12-a']);

    default:
        antwoord(['fout' => 'onbekende actie'], 400);
}

function pagina(): string
{
    return <<<'HTML'
<!doctype html>
<html lang="nl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#12121a">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="WorkManager">
<link rel="manifest" href="wm.php?actie=manifest&amp;t=__TOKEN__">
<link rel="apple-touch-icon" href="wm-icon-192.png">
<link rel="icon" href="wm-icon-192.png">
<title>WorkManager</title>
<style>
:root {
  --bg: #0f0f16; --kaart: #1a1a24; --kaart2: #22222f; --rand: #2f2f3d;
  --tekst: #eceaf3; --grijs: #9b98ad; --accent: #6c8cff; --accent2: #8aa4ff;
  --rood: #e8734c; --groen: #79c08a; --geel: #e8b54b;
}
* { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
/* Dossiers en mailonderwerpen bevatten paden en URL's zonder spaties; zonder dit duwen
   die de hele pagina breder dan het scherm en schuift alles horizontaal weg. */
.titel, .meta, .fragment, .dossier, .kop { overflow-wrap: anywhere; }
html, body { max-width: 100%; }
body {
  margin: 0; background: var(--bg); color: var(--tekst); font: 16px/1.45 system-ui, -apple-system, sans-serif;
  padding: 0 0 env(safe-area-inset-bottom);
}
header {
  position: sticky; top: 0; z-index: 5; background: rgba(15,15,22,.94);
  backdrop-filter: blur(8px); border-bottom: 1px solid var(--rand);
  padding: calc(10px + env(safe-area-inset-top)) var(--marge) 8px; display: flex; align-items: center; gap: 10px;
}
/* Op de gsm loopt alles tot de rand; op een breed scherm blijft de kolom leesbaar. */
body { --marge: max(14px, calc((100vw - 760px) / 2)); }
header h1 { font-size: 17px; margin: 0; flex: 1; font-weight: 650; letter-spacing: .2px; }
header .stand { font-size: 11.5px; color: var(--grijs); }
button { font: inherit; color: inherit; background: none; border: none; cursor: pointer; }
.rond {
  width: 34px; height: 34px; border-radius: 50%; background: var(--kaart2);
  border: 1px solid var(--rand); display: grid; place-items: center; font-size: 15px;
}
nav { display: flex; gap: 6px; padding: 10px 14px 0; overflow-x: auto; scrollbar-width: none; }
nav::-webkit-scrollbar { display: none; }
nav button {
  padding: 7px 14px; border-radius: 999px; background: var(--kaart); border: 1px solid var(--rand);
  font-size: 14px; color: var(--grijs); white-space: nowrap;
}
nav button.aan { background: var(--accent); border-color: var(--accent); color: #0d1020; font-weight: 600; }
nav button .tel {
  display: inline-block; margin-left: 6px; padding: 0 6px; border-radius: 999px;
  background: rgba(255,255,255,.13); font-size: 11.5px; font-weight: 600;
}
main { padding: 12px 14px 90px; max-width: 760px; margin: 0 auto; }
nav { max-width: 760px; margin: 0 auto; }
section { display: none; }
section.aan { display: block; }
.kaart {
  background: var(--kaart); border: 1px solid var(--rand); border-radius: 14px;
  padding: 12px 14px; margin-bottom: 9px;
}
.kaart.klikbaar:active { background: var(--kaart2); }
.rij { display: flex; align-items: flex-start; gap: 11px; }
.vink {
  flex: none; width: 24px; height: 24px; border-radius: 7px; border: 2px solid var(--rand);
  margin-top: 1px; display: grid; place-items: center; font-size: 14px; color: transparent;
}
.vink.bezig { border-color: var(--accent); color: var(--accent); }
.vink.klaar { border-color: var(--groen); background: var(--groen); color: #10210f; }
.inhoud { flex: 1; min-width: 0; }
.titel { font-size: 15px; }
.titel.af { text-decoration: line-through; color: var(--grijs); }
.meta { font-size: 12px; color: var(--grijs); margin-top: 3px; display: flex; flex-wrap: wrap; gap: 8px; }
.badge { padding: 1px 7px; border-radius: 999px; background: var(--kaart2); font-size: 11.5px; }
/* Filterchips (teamtab): compacte, tapbare knopjes op één scrollbare regel. */
.chips { display: flex; gap: 6px; overflow-x: auto; padding: 2px 0 8px; -webkit-overflow-scrolling: touch; }
.chip { flex: 0 0 auto; padding: 5px 12px; border-radius: 999px; border: 1px solid var(--rand);
  background: var(--kaart); color: var(--tekst); font-size: 13px; }
.chip.aan { background: var(--accent); color: #14140e; border-color: var(--accent); font-weight: 600; }
.badge.hoog { background: rgba(232,115,76,.18); color: var(--rood); }
.badge.vandaag { background: rgba(232,181,75,.18); color: var(--geel); }
.badge.laat { background: rgba(232,115,76,.22); color: var(--rood); font-weight: 600; }
.tijd { flex: none; width: 52px; font-variant-numeric: tabular-nums; font-size: 14px; color: var(--accent2); }
.tijd small { display: block; color: var(--grijs); font-size: 11px; }
.nu { border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent) inset; }
.leeg { text-align: center; color: var(--grijs); padding: 34px 10px; font-size: 14.5px; }
.kop { font-size: 12px; text-transform: uppercase; letter-spacing: .8px; color: var(--grijs); margin: 16px 2px 7px; }
.kop:first-child { margin-top: 2px; }
.balk { height: 7px; border-radius: 4px; background: var(--kaart2); overflow: hidden; margin-top: 6px; }
.balk i { display: block; height: 100%; background: var(--accent); }
.acties { display: flex; gap: 7px; margin-top: 9px; flex-wrap: wrap; }
.acties button {
  padding: 5px 11px; border-radius: 9px; background: var(--kaart2); border: 1px solid var(--rand);
  font-size: 13px; color: var(--grijs);
}
.acties button:disabled { opacity: .45; }
.acties button.sterk { color: var(--tekst); border-color: var(--accent); }
.fragment {
  font-size: 13px; color: var(--grijs); margin-top: 7px; max-height: 3.9em; overflow: hidden;
}
.antwoord { margin-top: 9px; }
.timer {
  display: flex; align-items: center; gap: 10px; padding: 11px 14px; margin-bottom: 9px;
  border-radius: 14px; background: rgba(108,140,255,.14); border: 1px solid var(--accent);
}
.timer .inhoud { flex: 1; min-width: 0; }
.timer .duur { font-variant-numeric: tabular-nums; font-size: 17px; font-weight: 650; }
.zoekbalk { margin-bottom: 9px; }
.zoekbalk input {
  width: 100%; padding: 10px 13px; border-radius: 11px; background: var(--kaart);
  border: 1px solid var(--rand); color: var(--tekst); font: inherit;
}
.dicteer { flex: none; width: 44px; border-radius: 11px; background: var(--kaart);
  border: 1px solid var(--rand); font-size: 17px; }
.dicteer.luistert { border-color: var(--accent); background: rgba(108,140,255,.2); }
.bijlage { font-size: 12px; color: var(--accent2); }
.belknop {
  display: inline-block; padding: 5px 11px; border-radius: 9px; background: var(--kaart2);
  border: 1px solid var(--rand); font-size: 13px; color: var(--accent2); text-decoration: none;
}
.dossier {
  margin-top: 10px; padding-top: 10px; border-top: 1px solid var(--rand);
  font-size: 13.5px; line-height: 1.5; white-space: pre-wrap; color: var(--tekst);
  max-height: 70vh; overflow-y: auto; overflow-x: auto;
}
.offline {
  background: rgba(232,181,75,.16); border: 1px solid var(--geel); color: var(--geel);
  border-radius: 11px; padding: 9px 13px; margin-bottom: 10px; font-size: 13.5px;
}
.antwoord textarea {
  width: 100%; padding: 11px 12px; border-radius: 11px; background: var(--kaart2);
  border: 1px solid var(--rand); color: var(--tekst); font: inherit; line-height: 1.4;
  resize: vertical;
}
.vouw {
  width: 100%; text-align: left; padding: 9px 2px; color: var(--grijs); font-size: 13px;
  display: flex; align-items: center; gap: 7px;
}
.vouw i { font-style: normal; transition: transform .15s; }
.vouw.open i { transform: rotate(90deg); }
form.boeking { display: grid; gap: 9px; }
form.boeking select, form.boeking input {
  width: 100%; padding: 10px 12px; border-radius: 10px; background: var(--kaart2);
  border: 1px solid var(--rand); color: var(--tekst); font: inherit;
}
form.boeking .keuzes { display: flex; gap: 7px; flex-wrap: wrap; }
form.boeking .keuzes button {
  flex: 1; min-width: 62px; padding: 8px 0; border-radius: 10px; background: var(--kaart2);
  border: 1px solid var(--rand); font-size: 14px; color: var(--grijs);
}
form.boeking .keuzes button.aan { border-color: var(--accent); color: var(--tekst); }
form.boeking .verstuur {
  padding: 11px; border-radius: 11px; background: var(--accent); color: #0d1020; font-weight: 650;
}
form.boeking .verstuur:disabled { opacity: .45; }
.nieuw {
  position: fixed; left: 0; right: 0; bottom: 0; padding: 10px 14px calc(10px + env(safe-area-inset-bottom));
  background: rgba(15,15,22,.96); backdrop-filter: blur(8px); border-top: 1px solid var(--rand);
  display: flex; gap: 8px; padding-left: var(--marge); padding-right: var(--marge);
}
.nieuw input {
  flex: 1; min-width: 0; padding: 11px 13px; border-radius: 11px; background: var(--kaart);
  border: 1px solid var(--rand); color: var(--tekst); font: inherit;
}
.nieuw input::placeholder { color: var(--grijs); }
.nieuw button {
  padding: 0 17px; border-radius: 11px; background: var(--accent); color: #0d1020; font-weight: 650;
}
.nieuw button:disabled { opacity: .45; }
#toast {
  position: fixed; left: 50%; transform: translateX(-50%) translateY(20px); bottom: 78px;
  background: var(--kaart2); border: 1px solid var(--rand); border-radius: 11px;
  padding: 9px 15px; font-size: 14px; opacity: 0; transition: .25s; pointer-events: none;
  max-width: calc(100vw - 28px); z-index: 9;
}
#toast.aan { opacity: 1; transform: translateX(-50%) translateY(0); }
</style>
</head>
<body>
<header>
  <h1>WorkManager</h1>
  <span class="stand" id="stand">laden…</span>
  <span class="stand" id="hier"></span>
  <button class="rond" id="ververs" title="Verversen">⟳</button>
</header>
<nav id="tabs"></nav>
<main>
  <section id="s-plan"></section>
  <section id="s-taken"></section>
  <section id="s-agenda"></section>
  <section id="s-berichten"></section>
  <section id="s-uren"></section>
  <section id="s-klanten"></section>
  <section id="s-team"></section>
  <section id="s-starten"></section>
</main>
<div class="nieuw">
  <input id="nieuwTekst" placeholder="Nieuwe taak — gewoon in een zin…" enterkeyhint="done">
  <button class="dicteer" id="dicteerKnop" title="Inspreken" hidden>🎙</button>
  <button id="nieuwKnop">Toevoegen</button>
</div>
<div id="toast"></div>

<script>
const TOKEN = '__TOKEN__';
const api = (actie, opties) =>
  fetch(`wm.php?actie=${actie}&token=${encodeURIComponent(TOKEN)}`, opties).then(r => r.json());

let snapshot = null;
let tab = 'taken';
let laterOpen = false;             // "Later"-lijst uitgeklapt
let agendaLater = false;           // agenda: dagen ná morgen uitgeklapt
const agendaOpen = new Set();      // afspraken waarvan de volledige tekst getoond wordt
let antwoordOp = '';               // bericht-id waarvoor het antwoordvak openstaat
let antwoordTekst = '';            // wat er in dat vak staat
let zoek = '';                     // filter op de takenlijst
let openDossier = '';              // welk klantdossier uitgeklapt staat
let teamLid = '';                  // gekozen lid voor een nieuwe teamtaak
let teamFilterLid = '';            // teamtab: alleen dit lid tonen ('' = iedereen)
let teamFilterPrio = false;        // teamtab: alleen ★★★-taken (prioriteit hoog)
let teamSorteer = 'lid';           // teamtab: 'lid' (groepen) of 'prio' (hoog eerst)
const voorstelUit = new Set();     // voorstelregels die je níét wilt boeken
const bezig = new Set();           // id's waarvan de actie nog onderweg is
const gedaan = new Set();          // id's die de pc bevestigd heeft (taken én berichten)
const uitgeklapt = new Set();      // berichten waarvan de tekst getoond wordt
const boeking = { klant: '', minuten: 30, tekst: '' };  // stand van het urenformulier

const TABS = [
  { id: 'plan', naam: 'Plan', tel: s => (s.plan || []).length },
  { id: 'taken', naam: 'Taken', tel: s => (s.taken || []).filter(t => !gedaan.has(t.id)).length },
  { id: 'agenda', naam: 'Agenda', tel: s => (s.agenda || []).length },
  { id: 'berichten', naam: 'Berichten', tel: s => (s.berichten || []).filter(b => !gedaan.has(b.id)).length },
  { id: 'uren', naam: 'Uren', tel: () => 0 },
  { id: 'klanten', naam: 'Klanten', tel: s => (s.dossiers || []).length },
  { id: 'team', naam: 'Team', tel: s => (s.team?.taken || []).length },
  { id: 'starten', naam: 'Starten', tel: () => 0 },
];

const esc = t => String(t ?? '').replace(/[&<>"]/g, c =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);

function toast(tekst) {
  const el = document.getElementById('toast');
  el.textContent = tekst;
  el.classList.add('aan');
  clearTimeout(el._t);
  el._t = setTimeout(() => el.classList.remove('aan'), 3200);
}

function geleden(iso) {
  if (!iso) return 'nog geen gegevens';
  // De server levert ISO-8601 mét tijdzone. Hier stond ooit een handmatige 'Z' achter een
  // tijd die al lokaal was — dan is het verschil altijd negatief en meldt de pagina eeuwig
  // "zonet bijgewerkt", ook als de pc al uren uitstaat.
  const min = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (min < 0) return 'zonet bijgewerkt';
  if (min < 2) return 'zonet bijgewerkt';
  if (min < 60) return `${min} min geleden`;
  const uur = Math.round(min / 60);
  return uur < 24 ? `${uur} u geleden` : `${Math.round(uur / 24)} d geleden`;
}

function tekenTabs() {
  document.getElementById('tabs').innerHTML = TABS.map(t => {
    const n = snapshot ? t.tel(snapshot) : 0;
    return `<button data-tab="${t.id}" class="${t.id === tab ? 'aan' : ''}">${t.naam}` +
           `${n ? `<span class="tel">${n}</span>` : ''}</button>`;
  }).join('');
  document.querySelectorAll('#tabs button').forEach(b =>
    b.onclick = () => { tab = b.dataset.tab; teken(); });
}

function tekenTaken() {
  const balk = `<div class="zoekbalk">
    <input id="zoekVak" placeholder="Zoeken in taken…" value="${esc(zoek)}"
           enterkeyhint="search" autocapitalize="off"></div>`;
  const term = zoek.trim().toLowerCase();
  const taken = (snapshot?.taken || [])
    .filter(t => !gedaan.has(t.id))
    .filter(t => !term || (t.tekst + ' ' + t.categorie).toLowerCase().includes(term));
  if (!taken.length) {
    return balk + `<div class="leeg">${term ? 'Niets gevonden' : 'Geen openstaande taken 🎉'}</div>`
      + tekenLater();
  }
  const groepen = [
    ['Te laat', t => t.laat],
    ['Vandaag', t => !t.laat && t.vandaag],
    ['Verder', t => !t.laat && !t.vandaag],
  ];
  return balk + groepen.map(([naam, filter]) => {
    const rijen = taken.filter(filter);
    if (!rijen.length) return '';
    return `<div class="kop">${naam} (${rijen.length})</div>` + rijen.map(t => `
      <div class="kaart">
        <div class="rij">
          <button class="vink ${bezig.has(t.id) ? 'bezig' : ''}" data-af="${t.id}">✓</button>
          <div class="inhoud">
            <div class="titel">${esc(t.tekst)}</div>
            <div class="meta">
              ${t.categorie ? `<span class="badge">${esc(t.categorie)}</span>` : ''}
              ${t.prioriteit === 0 ? '<span class="badge hoog">hoog</span>' : ''}
              ${t.deadline ? `<span class="badge ${t.laat ? 'laat' : t.vandaag ? 'vandaag' : ''}">${esc(t.deadlineTekst)}</span>` : ''}
            </div>
            <div class="acties">
              <button data-snooze="${t.id}" data-uren="3">3 u later</button>
              <button data-snooze="${t.id}" data-uren="24">Morgen</button>
            </div>
          </div>
        </div>
      </div>`).join('');
  }).join('') + tekenLater();
}

/** De lopende timer, bovenaan het plan: je moet in één blik zien dat hij nog loopt. */
function tekenTimer() {
  const t = snapshot?.timer;
  if (!t) return '';
  const uur = Math.floor(t.minuten / 60), min = t.minuten % 60;
  return `<div class="timer">
    <div class="inhoud">
      <div class="duur">${uur ? uur + 'u' + String(min).padStart(2, '0') : t.minuten + ' min'}</div>
      <div class="meta">${esc(t.tekst)}${t.klant ? ' · ' + esc(t.klant) : ''} · sinds ${esc(t.sinds)}</div>
    </div>
    <button class="rond" data-timerstop="1" title="Stoppen en boeken">⏹</button>
  </div>`;
}

function tekenPlan() {
  const items = snapshot?.plan || [];
  const timer = tekenTimer();
  if (!items.length) {
    return timer + '<div class="leeg">Geen dagplanning voor vandaag</div>';
  }
  return timer + items.map(i => `
    <div class="kaart">
      <div class="rij">
        <div class="tijd">${esc(i.start || '~')}<small>${i.minuten} m</small></div>
        <div class="inhoud">
          <div class="titel">${esc(i.tekst)}</div>
          ${i.waarom ? `<div class="meta">${esc(i.waarom)}</div>` : ''}
          ${i.vast || !i.taakId ? '' : `<div class="acties">
            <button data-timerstart="${i.taakId}">Timer starten</button>
            <button data-af="${i.taakId}">Afvinken</button>
          </div>`}
        </div>
      </div>
    </div>`).join('');
}

/** Wat er nog aankomt: startdatum in de toekomst of nog even gesnoozed. Standaard ingeklapt. */
function tekenLater() {
  const later = (snapshot?.later || []).filter(t => !gedaan.has(t.id));
  if (!later.length) return '';
  return `<button class="vouw ${laterOpen ? 'open' : ''}" data-vouw="later">
      <i>›</i> Later (${later.length})
    </button>` + (!laterOpen ? '' : later.map(t => `
      <div class="kaart">
        <div class="rij">
          <button class="vink ${bezig.has(t.id) ? 'bezig' : ''}" data-af="${t.id}">✓</button>
          <div class="inhoud">
            <div class="titel">${esc(t.tekst)}</div>
            <div class="meta">
              ${t.categorie ? `<span class="badge">${esc(t.categorie)}</span>` : ''}
              <span class="badge vandaag">${esc(t.wanneer)}</span>
              ${t.deadlineTekst ? `<span class="badge">${esc(t.deadlineTekst)}</span>` : ''}
            </div>
          </div>
        </div>
      </div>`).join(''));
}

// Een locatie als "Le Crotoy → Sierville (± 430 km)" of "Rouen (25 min) + Parc de Clères"
// is geen geldig Maps-doel. Daarom: haakjes (rijtijden e.d.) eruit, splitsen op → en +,
// en per bestemming een eigen routeknopje — de totale route hoeft niet.
function routeDoelen(locatie) {
  return locatie.replace(/\([^)]*\)/g, ' ')
    .split(/→|\+/)
    .map(d => d.trim().replace(/\s+/g, ' '))
    .filter(d => d.length > 1);
}

function tekenAgenda() {
  const items = snapshot?.agenda || [];
  if (!items.length) {
    return '<div class="leeg">Geen afspraken vandaag en morgen</div>';
  }
  // Vandaag en morgen altijd tonen; de dagen daarna (de pc synct twee weken vooruit)
  // achter een uitklapknop zodat de lijst kort blijft.
  const dichtbij = items.filter(a => a.dag === 'Vandaag' || a.dag === 'Morgen');
  const later = items.filter(a => a.dag !== 'Vandaag' && a.dag !== 'Morgen');
  let html = agendaKaarten(dichtbij);
  if (later.length) {
    html += `<div class="acties" style="margin:10px 4px">
      <button data-agendalater>📅 ${agendaLater ? 'Latere dagen verbergen'
        : `Latere dagen tonen (${later.length})`}</button></div>`;
    if (agendaLater) {
      html += agendaKaarten(later);
    }
  }
  return html;
}

function agendaKaarten(items) {
  let vorigeDag = '';
  return items.map(a => {
    const kop = a.dag !== vorigeDag ? `<div class="kop">${esc(a.dag)}</div>` : '';
    vorigeDag = a.dag;
    const sleutel = `${a.dag}|${a.titel}`;
    const open = agendaOpen.has(sleutel);
    return kop + `
      <div class="kaart ${a.nu ? 'nu' : ''}">
        <div class="rij">
          <div class="tijd">${esc(a.van)}<small>${esc(a.tot)}</small></div>
          <div class="inhoud">
            <div class="titel">${esc(a.titel)}</div>
            <div class="meta">
              ${a.bron ? `<span class="badge">${esc(a.bron)}</span>` : ''}
              ${a.locatie && routeDoelen(a.locatie).length === 1
                ? `<span>${esc(a.locatie)}</span>` : ''}
              ${a.nu ? '<span class="badge vandaag">bezig</span>' : ''}
            </div>
            ${open && a.omschrijving
              ? `<div class="fragment">${esc(a.omschrijving).replace(/\n/g, '<br>')}</div>` : ''}
            ${!a.boekbaar && !a.locatie && !a.omschrijving ? '' : `<div class="acties">
              ${a.omschrijving ? `<button data-agtekst="${esc(sleutel)}">
                ${open ? 'Tekst verbergen' : '📖 Volledige tekst'}</button>` : ''}
              ${a.locatie ? (() => {
                const doelen = routeDoelen(a.locatie);
                // Kort label (de naam vóór de komma); het volledige adres blijft het
                // navigatiedoel in de link.
                return doelen.map(d => `<a class="belknop" target="_blank" rel="noopener"
                  href="https://www.google.com/maps/dir/?api=1&destination=${encodeURIComponent(d)}"
                  >🧭 ${doelen.length > 1 ? esc(d.split(',')[0]) : 'Route'}</a>`).join(' ');
              })() : ''}
              ${a.boekbaar ? `<button data-boek="${esc(a.titel)}" data-boekmin="${a.minuten}"
                      data-klant="${esc(a.klant)}">Uren boeken</button>` : ''}
            </div>`}
          </div>
        </div>
      </div>`;
  }).join('');
}

function tekenBerichten() {
  const items = (snapshot?.berichten || []).filter(b => !gedaan.has(b.id));
  if (!items.length) {
    return '<div class="leeg">Inbox leeg 🎉</div>';
  }
  return items.map(b => {
    const open = uitgeklapt.has(b.id);
    const wacht = bezig.has(b.id);
    return `
    <div class="kaart">
      <div class="rij">
        <div class="inhoud">
          <div class="titel">${esc(b.van)}</div>
          <div class="meta" style="margin-top:2px">${esc(b.onderwerp)}</div>
          <div class="meta">
            <span class="badge">${esc(b.soort)}</span>
            ${b.urgent ? '<span class="badge laat">urgent</span>' : ''}
            <span>${esc(b.wanneer)}</span>
            ${b.concept ? '<span class="badge vandaag">concept klaar</span>' : ''}
          </div>
          ${!b.bijlagen.length ? '' :
            `<div class="meta"><span class="bijlage">📎 ${b.bijlagen.map(esc).join(', ')}</span></div>`}
          ${open && b.fragment ? `<div class="fragment">${esc(b.fragment)}</div>` : ''}
          <div class="acties">
            ${b.duim ? `<button class="sterk" data-duim="${b.id}" ${wacht ? 'disabled' : ''}>👍</button>` : ''}
            ${b.antwoorden ? `<button class="${b.concept ? 'sterk' : ''}" data-antw="${b.id}">
              ${b.concept ? 'Concept…' : 'Antwoorden…'}</button>` : ''}
            ${b.archiveren ? `<button data-arch="${b.id}" ${wacht ? 'disabled' : ''}>Archiveren</button>` : ''}
            ${b.snoozen ? `<button data-snoozeber="${b.id}" data-wanneer="vanavond">Vanavond</button>
              <button data-snoozeber="${b.id}" data-wanneer="morgen">Morgen</button>` : ''}
            ${b.bijlagen.length ? `<button data-drive="${b.id}" ${wacht ? 'disabled' : ''}>Naar Drive</button>` : ''}
            ${b.fragment ? `<button data-lees="${b.id}">${open ? 'Minder' : 'Lezen'}</button>` : ''}
          </div>
          ${antwoordOp !== b.id ? '' : `
            <div class="antwoord">
              <textarea id="antwoordVak" rows="7" placeholder="Je antwoord…"
                        enterkeyhint="enter">${esc(antwoordTekst)}</textarea>
              <div class="acties">
                <button class="sterk" data-verstuur="${b.id}" ${wacht ? 'disabled' : ''}>Versturen</button>
                <button data-annuleer="1">Annuleren</button>
              </div>
            </div>`}
        </div>
      </div>
    </div>`;
  }).join('');
}

/** De klantdossiers: open punten bovenaan, daaronder het dossier zelf om na te lezen. */
function tekenKlanten() {
  const lijst = snapshot?.dossiers || [];
  if (!lijst.length) return '<div class="leeg">Nog geen klantdossiers</div>';
  return lijst.map(d => {
    const open = openDossier === d.klant;
    return `<div class="kop">${esc(d.klant)} · bijgewerkt ${esc(d.bijgewerkt)}</div>
      ${!d.punten.length ? '' : `<div class="kaart">
        <div class="titel">Openstaand (${d.punten.length})</div>
        ${d.punten.map(p => `<div class="meta" style="margin-top:6px">• ${esc(p)}</div>`).join('')}
      </div>`}
      <div class="kaart">
        <div class="acties">
          <button data-dossier="${esc(d.klant)}">${open ? 'Dossier sluiten' : 'Dossier lezen'}</button>
          ${(d.telefoon || []).map(t =>
            `<a class="belknop" href="tel:${esc(t)}">📞 ${esc(t)}</a>`).join('')}
        </div>
        ${open ? `<div class="dossier">${esc(d.tekst)}</div>` : ''}
      </div>`;
  }).join('');
}

/** Openstaande teamtaken per lid, met een veld om er een bij te maken. */
function tekenTeam() {
  const team = snapshot?.team;
  if (!team) return '<div class="leeg">Geen teamgegevens</div>';
  const leden = team.leden || [];
  teamLid = teamLid || leden[0] || '';
  const formulier = `<div class="kaart">
      <form class="boeking" id="teamForm">
        <select id="tLid">${leden.map(l =>
          `<option value="${esc(l)}"${l === teamLid ? ' selected' : ''}>${esc(l)}</option>`).join('')}</select>
        <input id="tTekst" placeholder="Nieuwe teamtaak…" enterkeyhint="done">
        <button type="submit" class="verstuur">Toevoegen</button>
      </form>
    </div>`;
  const open = (team.taken || []).filter(t => !gedaan.has(t.id));
  // Filterchips: per persoon (alleen wie iets open heeft), alleen-hoog en de sortering.
  const metWerk = leden.filter(l => open.some(t => t.lid === l));
  const chips = `<div class="chips">
      <button class="chip ${!teamFilterLid ? 'aan' : ''}" data-tlid="">Iedereen</button>
      ${metWerk.map(l => `<button class="chip ${teamFilterLid === l ? 'aan' : ''}"
        data-tlid="${esc(l)}">${esc(l)} (${open.filter(t => t.lid === l).length})</button>`).join('')}
      <button class="chip ${teamFilterPrio ? 'aan' : ''}" data-tprio="1">★★★</button>
      <button class="chip" data-tsort="1">${teamSorteer === 'lid' ? '↕ prioriteit' : '↕ per lid'}</button>
    </div>`;
  const zichtbaar = open.filter(t =>
    (!teamFilterLid || t.lid === teamFilterLid) && (!teamFilterPrio || t.prioriteit === 0));
  const kaart = (t, metLid) => `
      <div class="kaart">
        <div class="rij">
          <button class="vink ${bezig.has(t.id) ? 'bezig' : ''}" data-teamaf="${t.id}">✓</button>
          <div class="inhoud">
            <div class="titel">${esc(t.tekst)}</div>
            <div class="meta">${metLid ? esc(t.lid) + ' · ' : ''}${'★'.repeat(3 - (t.prioriteit ?? 1))}` +
              `${t.prioriteit === 0 ? ' <span class="badge hoog">hoog</span>' : ''}</div>
            ${t.subtaken.map(st => `<div class="meta">– ${esc(st)}</div>`).join('')}
          </div>
        </div>
      </div>`;
  let lijst;
  if (teamSorteer === 'prio') {
    // Platte lijst, hoogste prioriteit eerst; het lid staat dan op de kaart zelf.
    lijst = zichtbaar.slice()
      .sort((a, b) => (a.prioriteit ?? 1) - (b.prioriteit ?? 1) || a.lid.localeCompare(b.lid))
      .map(t => kaart(t, true)).join('');
  } else {
    lijst = leden.filter(l => zichtbaar.some(t => t.lid === l)).map(lid => {
      const taken = zichtbaar.filter(t => t.lid === lid);
      return `<div class="kop">${esc(lid)} (${taken.length})</div>` +
        taken.map(t => kaart(t, false)).join('');
    }).join('');
  }
  return formulier + chips +
    (lijst || '<div class="leeg">Niets openstaand binnen dit filter 🎉</div>');
}

/** Wat je op de pc kunt laten opstarten terwijl je onderweg bent. */
function tekenStarten() {
  const lijst = snapshot?.projecten || [];
  if (!lijst.length) return '<div class="leeg">Niets ingesteld</div>';
  let vorige = '';
  return lijst.map(p => {
    const kop = p.klant !== vorige ? `<div class="kop">${esc(p.klant)}</div>` : '';
    vorige = p.klant;
    return kop + `<div class="kaart klikbaar">
      <div class="rij">
        <div class="inhoud"><div class="titel">${esc(p.label)}</div></div>
        <button class="rond" data-start="${esc(p.sleutel)}" title="Starten">▶</button>
      </div>
    </div>`;
  }).join('');
}

function tekenUren() {
  const u = snapshot?.uren;
  if (!u) return '<div class="leeg">Nog geen urengegevens</div>';
  const perKlant = (u.perKlant || []).map(k => `
    <div class="kaart">
      <div class="titel">${esc(k.klant)}</div>
      <div class="meta">${esc(k.tekst)}${k.doorgeboekt ? '' : ' · nog niet doorgeboekt'}</div>
      <div class="balk"><i style="width:${Math.min(100, k.deel)}%"></i></div>
    </div>`).join('');
  const klanten = (snapshot?.klanten || []).map(k =>
    `<option value="${esc(k)}"${k === boeking.klant ? ' selected' : ''}>${esc(k)}</option>`).join('');
  const keuzes = [15, 30, 60, 120].map(m =>
    `<button type="button" data-min="${m}" class="${boeking.minuten === m ? 'aan' : ''}">` +
    `${m < 60 ? m + ' min' : m / 60 + ' u'}</button>`).join('');
  const v = snapshot?.voorstel;
  const voorstelBlok = !v ? '' : `<div class="kop">Voorstel van vandaag</div>` +
    v.regels.map(r => `
      <div class="kaart">
        <div class="rij">
          <button class="vink ${voorstelUit.has(r.id) ? '' : 'klaar'}" data-vst="${r.id}">✓</button>
          <div class="inhoud">
            <div class="titel">${esc(r.tekst)}</div>
            <div class="meta">
              <span class="badge">${esc(r.klant)}</span>
              <span>${r.minuten < 60 ? r.minuten + ' min'
                : Math.floor(r.minuten / 60) + 'u' + String(r.minuten % 60).padStart(2, '0')}</span>
              ${r.van ? `<span>vanaf ${esc(r.van)}</span>` : ''}
            </div>
          </div>
        </div>
      </div>`).join('') + `<div class="kaart">
        <div class="acties">
          <button class="sterk" data-vstboek="1">Aangevinkte boeken</button>
          <button data-vstweg="1">Weggooien</button>
        </div>
      </div>`;
  return `<div class="kop">Vandaag</div>
    <div class="kaart">
      <div class="titel">${esc(u.vandaagTekst)}</div>
      <div class="meta">${esc(u.regels)} regel(s) · ${esc(u.openTekst)}</div>
      <div class="acties">
        <button data-voorstel="1">Voorstel maken…</button>
        <button data-doorboek="1">Doorboeken naar urbanadmin</button>
      </div>
    </div>
    ${voorstelBlok}
    ${!snapshot?.vanHuis ? '' : `<div class="kop">Werkdag</div>
      <div class="kaart"><div class="titel">Van huis vertrokken om ${esc(snapshot.vanHuis)}</div>
      <div class="meta">Ter info — thuis levert nooit een urenregel op.</div></div>`}
    ${!(snapshot?.bezoeken || []).length ? '' : `<div class="kop">Waar je was</div>` +
      snapshot.bezoeken.map(b => `<div class="kaart">
        <div class="rij">
          <div class="tijd">${esc(b.van)}<small>${esc(b.tot || 'bezig')}</small></div>
          <div class="inhoud">
            <div class="titel">${esc(b.plek)}</div>
            ${b.minuten ? `<div class="meta">${b.minuten < 60 ? b.minuten + ' min'
              : Math.floor(b.minuten / 60) + 'u' + String(b.minuten % 60).padStart(2, '0')}</div>` : ''}
          </div>
        </div>
      </div>`).join('')}
    <div class="kop">Deze plek onthouden</div>
    <div class="kaart">
      <form class="boeking" id="plekForm">
        <input id="pNaam" placeholder="Naam van deze plek (bv. Lauryssens)" enterkeyhint="done">
        <button type="submit" class="verstuur">Onthouden</button>
      </form>
      <div class="meta">${snapshot?.plekken?.length
        ? 'Bekend: ' + snapshot.plekken.map(esc).join(', ')
        : 'Nog geen plekken bekend.'}</div>
    </div>
    <div class="kop">Uren boeken</div>
    <div class="kaart">
      <form class="boeking" id="boeking">
        <select id="bKlant">${klanten}</select>
        <div class="keuzes">${keuzes}</div>
        <input id="bMinuten" type="number" min="5" max="720" step="5" inputmode="numeric"
               value="${boeking.minuten}" aria-label="Aantal minuten">
        <input id="bTekst" placeholder="Waaraan gewerkt?" value="${esc(boeking.tekst)}"
               enterkeyhint="done">
        <button type="submit" class="verstuur">Boeken op vandaag</button>
      </form>
    </div>
    <div class="kop">Deze week</div>
    <div class="kaart">
      <div class="titel">${esc(u.weekTekst)}</div>
      <div class="meta">${esc(u.weekOmschrijving)}</div>
    </div>
    ${perKlant ? '<div class="kop">Per klant deze week</div>' + perKlant : ''}`;
}

function teken() {
  tekenTabs();
  document.querySelectorAll('section').forEach(s => s.classList.remove('aan'));
  const el = document.getElementById('s-' + tab);
  el.classList.add('aan');
  const banner = offlineSinds
    ? `<div class="offline">Geen verbinding — dit is de stand van ${esc(offlineSinds)}. ` +
      `Wat je nu aantikt, kan de pc niet bereiken.</div>`
    : '';
  el.innerHTML = banner + (tab === 'plan' ? tekenPlan()
    : tab === 'taken' ? tekenTaken()
    : tab === 'agenda' ? tekenAgenda()
    : tab === 'berichten' ? tekenBerichten()
    : tab === 'klanten' ? tekenKlanten()
    : tab === 'team' ? tekenTeam()
    : tab === 'starten' ? tekenStarten()
    : tekenUren());
  el.querySelectorAll('[data-af]').forEach(b =>
    b.onclick = () => stuur({ soort: 'taak_klaar', id: b.dataset.af }, b.dataset.af, 'Afgevinkt'));
  el.querySelectorAll('[data-agendalater]').forEach(b =>
    b.onclick = () => { agendaLater = !agendaLater; teken(); });
  el.querySelectorAll('[data-agtekst]').forEach(b =>
    b.onclick = () => {
      const sleutel = b.dataset.agtekst;
      if (agendaOpen.has(sleutel)) { agendaOpen.delete(sleutel); } else { agendaOpen.add(sleutel); }
      teken();
    });
  el.querySelectorAll('[data-snooze]').forEach(b =>
    b.onclick = () => stuur(
      { soort: 'taak_snooze', id: b.dataset.snooze, uren: +b.dataset.uren },
      b.dataset.snooze, 'Verzet'));
  el.querySelectorAll('[data-vouw]').forEach(b =>
    b.onclick = () => { laterOpen = !laterOpen; teken(); });
  el.querySelectorAll('[data-arch]').forEach(b =>
    b.onclick = () => stuur(
      { soort: 'bericht_archiveer', id: b.dataset.arch }, b.dataset.arch, 'Gearchiveerd'));
  el.querySelectorAll('[data-duim]').forEach(b =>
    b.onclick = () => stuur({ soort: 'bericht_duim', id: b.dataset.duim }, b.dataset.duim, '👍'));
  el.querySelectorAll('[data-snoozeber]').forEach(b =>
    b.onclick = () => stuur(
      { soort: 'bericht_snooze', id: b.dataset.snoozeber, wanneer: b.dataset.wanneer },
      b.dataset.snoozeber, 'Weggelegd'));
  el.querySelectorAll('[data-antw]').forEach(b =>
    b.onclick = () => {
      const bericht = (snapshot?.berichten || []).find(x => x.id === b.dataset.antw);
      antwoordOp = b.dataset.antw;
      antwoordTekst = bericht?.concept || '';
      teken();
      const vak = document.getElementById('antwoordVak');
      if (vak) { vak.focus(); vak.setSelectionRange(vak.value.length, vak.value.length); }
    });
  el.querySelectorAll('[data-annuleer]').forEach(b =>
    b.onclick = () => { antwoordOp = ''; antwoordTekst = ''; teken(); });
  const vak = el.querySelector('#antwoordVak');
  if (vak) vak.oninput = () => { antwoordTekst = vak.value; };
  el.querySelectorAll('[data-verstuur]').forEach(b =>
    b.onclick = async () => {
      if (antwoordTekst.trim().length < 2) { toast('Het antwoord is nog leeg'); return; }
      const id = b.dataset.verstuur;
      antwoordOp = '';
      await stuur({ soort: 'bericht_antwoord', id, tekst: antwoordTekst.trim() }, id, 'Verstuurd');
      antwoordTekst = '';
    });
  el.querySelectorAll('[data-boek]').forEach(b =>
    b.onclick = () => {
      // Vanuit de agenda naar het urenformulier springen, alles al ingevuld.
      boeking.tekst = b.dataset.boek;
      boeking.minuten = +b.dataset.boekmin || 30;
      if (b.dataset.klant) boeking.klant = b.dataset.klant;
      tab = 'uren';
      teken();
      toast('Nakijken en op "Boeken" duwen');
    });
  el.querySelectorAll('[data-drive]').forEach(b =>
    b.onclick = () => stuur({ soort: 'bericht_drive', id: b.dataset.drive }, null, 'Naar Drive'));
  el.querySelectorAll('[data-timerstart]').forEach(b =>
    b.onclick = () => stuur(
      { soort: 'timer_start', id: b.dataset.timerstart }, null, 'Timer gestart'));
  el.querySelectorAll('[data-timerstop]').forEach(b =>
    b.onclick = () => stuur({ soort: 'timer_stop' }, null, 'Timer gestopt'));
  const zoekVak = el.querySelector('#zoekVak');
  if (zoekVak) {
    zoekVak.oninput = () => {
      zoek = zoekVak.value;
      // Alleen de lijst hertekenen zou het veld de focus kosten, dus enkel de rijen.
      const positie = zoekVak.selectionStart;
      teken();
      const nieuw = document.querySelector('#zoekVak');
      if (nieuw) { nieuw.focus(); nieuw.setSelectionRange(positie, positie); }
    };
  }
  el.querySelectorAll('[data-lees]').forEach(b =>
    b.onclick = () => {
      const id = b.dataset.lees;
      uitgeklapt.has(id) ? uitgeklapt.delete(id) : uitgeklapt.add(id);
      teken();
    });
  el.querySelectorAll('[data-dossier]').forEach(b =>
    b.onclick = () => {
      openDossier = openDossier === b.dataset.dossier ? '' : b.dataset.dossier;
      teken();
    });
  el.querySelectorAll('[data-teamaf]').forEach(b =>
    b.onclick = () => stuur(
      { soort: 'teamtaak_klaar', id: b.dataset.teamaf }, b.dataset.teamaf, 'Afgevinkt'));
  // Teamtab: filterchips (persoon, alleen-hoog) en de sorteerschakelaar.
  el.querySelectorAll('[data-tlid]').forEach(b =>
    b.onclick = () => { teamFilterLid = b.dataset.tlid; teken(); });
  el.querySelectorAll('[data-tprio]').forEach(b =>
    b.onclick = () => { teamFilterPrio = !teamFilterPrio; teken(); });
  el.querySelectorAll('[data-tsort]').forEach(b =>
    b.onclick = () => { teamSorteer = teamSorteer === 'lid' ? 'prio' : 'lid'; teken(); });
  el.querySelectorAll('[data-start]').forEach(b =>
    b.onclick = () => stuur({ soort: 'start_project', sleutel: b.dataset.start }, null, 'Gestart'));
  el.querySelectorAll('[data-voorstel]').forEach(b =>
    b.onclick = () => {
      toast('Claude kijkt je dag na — dit duurt even…');
      stuur({ soort: 'uren_voorstel' }, null, 'Voorstel');
    });
  el.querySelectorAll('[data-doorboek]').forEach(b =>
    b.onclick = () => stuur({ soort: 'uren_doorboek' }, null, 'Doorgeboekt'));
  el.querySelectorAll('[data-vst]').forEach(b =>
    b.onclick = () => {
      const id = b.dataset.vst;
      voorstelUit.has(id) ? voorstelUit.delete(id) : voorstelUit.add(id);
      teken();
    });
  el.querySelectorAll('[data-vstboek]').forEach(b =>
    b.onclick = () => {
      const ids = (snapshot?.voorstel?.regels || [])
        .map(r => r.id).filter(id => !voorstelUit.has(id));
      if (!ids.length) { toast('Alles staat uitgevinkt'); return; }
      stuur({ soort: 'uren_voorstel_boek', ids }, null, 'Geboekt');
    });
  el.querySelectorAll('[data-vstweg]').forEach(b =>
    b.onclick = () => stuur({ soort: 'uren_voorstel_weg' }, null, 'Weggegooid'));
  const plekForm = el.querySelector('#plekForm');
  if (plekForm) {
    plekForm.onsubmit = e => {
      e.preventDefault();
      const veld = el.querySelector('#pNaam');
      if (!veld.value.trim()) return;
      stuur({ soort: 'plek_bewaren', naam: veld.value.trim() }, null, 'Plek onthouden');
      veld.value = '';
    };
  }
  const teamForm = el.querySelector('#teamForm');
  if (teamForm) {
    const lidVeld = el.querySelector('#tLid');
    lidVeld.onchange = () => { teamLid = lidVeld.value; };
    teamForm.onsubmit = e => {
      e.preventDefault();
      const veld = el.querySelector('#tTekst');
      if (!veld.value.trim()) return;
      stuur({ soort: 'teamtaak_nieuw', lid: lidVeld.value, tekst: veld.value.trim() },
        null, 'Teamtaak toegevoegd');
      veld.value = '';
    };
  }
  koppelBoeking(el);
}

/** Het urenformulier: de invoer onthouden zodat hertekenen niets wist. */
function koppelBoeking(el) {
  const form = el.querySelector('#boeking');
  if (!form) return;
  const klant = el.querySelector('#bKlant');
  const minuten = el.querySelector('#bMinuten');
  const tekst = el.querySelector('#bTekst');
  boeking.klant = boeking.klant || klant.value;
  klant.onchange = () => { boeking.klant = klant.value; };
  minuten.oninput = () => { boeking.minuten = +minuten.value || 0; };
  tekst.oninput = () => { boeking.tekst = tekst.value; };
  el.querySelectorAll('[data-min]').forEach(b =>
    b.onclick = () => { boeking.minuten = +b.dataset.min; teken(); });
  form.onsubmit = async e => {
    e.preventDefault();
    if (!boeking.tekst.trim()) { toast('Vul eerst in waaraan je gewerkt hebt'); return; }
    const knop = form.querySelector('.verstuur');
    knop.disabled = true;
    await stuur({
      soort: 'uren_boek', klant: boeking.klant,
      minuten: boeking.minuten, omschrijving: boeking.tekst.trim(),
    }, null, 'Geboekt');
    boeking.tekst = '';
    teken();
  };
}

async function stuur(inhoud, taakId, wat) {
  if (taakId) {
    if (bezig.has(taakId)) return;
    bezig.add(taakId);
    teken();
  }
  try {
    const { id, fout } = await api('actie', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ inhoud }),
    });
    if (!id) throw new Error(fout || 'niet doorgekomen');
    toast(`${wat} — de pc pakt het op…`);
    const melding = await wachtOpPc(id);
    if (taakId) {
      bezig.delete(taakId);
      gedaan.add(taakId);
    }
    toast(melding || `${wat}.`);
    teken();
    laad();
  } catch (e) {
    if (taakId) bezig.delete(taakId);
    toast('Niet gelukt: ' + e.message);
    teken();
  }
}

// De pc pollt elke 30 s; twee minuten wachten is ruim genoeg.
async function wachtOpPc(id) {
  for (let poging = 0; poging < 40; poging++) {
    await new Promise(r => setTimeout(r, 3000));
    const r = await api('status&id=' + encodeURIComponent(id));
    if (r.status === 'verwerkt') return r.melding;
  }
  return 'Staat klaar — de pc verwerkt het zodra hij aan staat.';
}

let offlineSinds = '';

async function laad() {
  try {
    const r = await api('data');
    snapshot = r.snapshot;
    offlineSinds = '';
    // De laatste stand lokaal bewaren: onderweg (kelder, geen bereik) toont de pagina dan
    // nog wat er wél bekend was in plaats van een lege lijst.
    try {
      localStorage.setItem('wm-snapshot',
        JSON.stringify({ snapshot: r.snapshot, bijgewerkt: r.bijgewerkt }));
    } catch { /* opslag vol of geweigerd: dan gewoon zonder */ }
    // Rijen die we lokaal verborgen (net afgevinkt/gearchiveerd) mogen weer meedoen zodra
    // de pc bijgewerkt is: een gesnoozede taak hoort dan onder "Later" te verschijnen.
    const open = new Set([...(snapshot?.taken || []), ...(snapshot?.berichten || []),
      ...(snapshot?.team?.taken || [])].map(x => x.id));
    for (const id of [...gedaan]) {
      if (!open.has(id)) gedaan.delete(id);
    }
    document.getElementById('stand').textContent =
      snapshot ? geleden(r.bijgewerkt) : 'pc heeft nog niets doorgestuurd';
    document.getElementById('hier').textContent = snapshot?.hier ? '📍 ' + snapshot.hier : '';
    teken();
  } catch {
    // Geen verbinding: terugvallen op de laatst bewaarde stand.
    if (!snapshot) {
      try {
        const bewaard = JSON.parse(localStorage.getItem('wm-snapshot') || 'null');
        if (bewaard?.snapshot) {
          snapshot = bewaard.snapshot;
          offlineSinds = geleden(bewaard.bijgewerkt);
          teken();
        }
      } catch { /* niets bewaard */ }
    }
    document.getElementById('stand').textContent =
      snapshot ? 'offline · ' + (offlineSinds || 'oude stand') : 'geen verbinding';
  }
}

document.getElementById('ververs').onclick = laad;
const invoer = document.getElementById('nieuwTekst');
const knop = document.getElementById('nieuwKnop');
async function nieuweTaak() {
  const tekst = invoer.value.trim();
  if (!tekst) return;
  invoer.value = '';
  knop.disabled = true;
  await stuur({ soort: 'taak_nieuw', tekst }, null, 'Taak aangemaakt');
  knop.disabled = false;
}
knop.onclick = nieuweTaak;
invoer.addEventListener('keydown', e => { if (e.key === 'Enter') nieuweTaak(); });

// Inspreken in plaats van typen. Niet elke browser kan dit (en in een PWA op iOS wisselt
// het per versie), dus de knop verschijnt alleen als het echt beschikbaar is — anders doet
// de dicteertoets van het toetsenbord hetzelfde.
const Spraak = window.SpeechRecognition || window.webkitSpeechRecognition;
if (Spraak) {
  const dicteer = document.getElementById('dicteerKnop');
  dicteer.hidden = false;
  let luisteraar = null;
  dicteer.onclick = () => {
    if (luisteraar) { luisteraar.stop(); return; }
    const herkenning = new Spraak();
    herkenning.lang = 'nl-BE';
    herkenning.interimResults = true;
    herkenning.continuous = false;
    const beginTekst = invoer.value ? invoer.value.trim() + ' ' : '';
    herkenning.onresult = e => {
      let tekst = '';
      for (const resultaat of e.results) tekst += resultaat[0].transcript;
      invoer.value = beginTekst + tekst.trim();
    };
    herkenning.onerror = e => {
      toast(e.error === 'not-allowed'
        ? 'Geef de pagina toegang tot de microfoon'
        : 'Inspreken lukte niet (' + e.error + ')');
    };
    herkenning.onend = () => {
      luisteraar = null;
      dicteer.classList.remove('luistert');
      invoer.focus();
    };
    luisteraar = herkenning;
    dicteer.classList.add('luistert');
    herkenning.start();
  };
}

// Grove positie doorgeven: één meting uit wifi/zendmast (geen GPS), met een cache van vijf
// minuten. Dat kost praktisch geen batterij. Het gebeurt alleen terwijl de pagina openstaat —
// voor registratie zonder de app te openen is er de iOS-automatisering (zie DEPLOY.md).
function meldPositie() {
  if (!navigator.geolocation) return;
  navigator.geolocation.getCurrentPosition(
    pos => api('locatie', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        lat: pos.coords.latitude,
        lon: pos.coords.longitude,
        acc: Math.round(pos.coords.accuracy || 0),
      }),
    }).catch(() => {}),
    () => {},                       // geweigerd of niet beschikbaar: stil verder
    { enableHighAccuracy: false, maximumAge: 300000, timeout: 8000 });
}

laad();
meldPositie();
setInterval(() => { if (document.visibilityState === 'visible') laad(); }, 120000);
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') laad();
});
</script>
</body>
</html>
HTML;
}
