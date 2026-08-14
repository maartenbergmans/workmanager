<?php
/**
 * WorkManager AH-bestelpagina: gsm-vriendelijke webpagina + API-brug naar de pc.
 *
 * De pc (AhWebSync in WorkManager) zet periodiek een snapshot van de gerechten,
 * suggesties en rubrieken klaar — met foto's, prijzen en productlinks al opgelost.
 * De pagina toont die, laat gerechten kiezen en ingrediënten aanvinken, en zet de
 * bestelling in een wachtrij. De pc pollt de wachtrij en legt de producten écht in
 * het winkelmandje op ah.be (daar staat de ingelogde AH-sessie).
 *
 * Acties (token = ah_token uit config.php, via header X-Wm-Token of parameter t/token):
 *   GET  (geen actie)                    -> de webpagina zelf (token in de link: ah.php?t=…)
 *   GET  data                            -> {snapshot, bijgewerkt}
 *   POST bestel     {inhoud}             -> {id}          (van de gsm)
 *   GET  status     ?id=...              -> {status, melding}
 *   POST snapshot   {snapshot}           -> {ok}          (van de pc)
 *   GET  ahwerk                          -> {bestellingen: [{id, inhoud}]}   (voor de pc)
 *   POST ahklaar    {id, melding}        -> {ok}          (van de pc)
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
if (!is_string($token) || !hash_equals($config['ah_token'], $token)) {
    if ($actie === '') {
        http_response_code(403);
        header('Content-Type: text/html; charset=utf-8');
        echo '<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
           . '<body style="font-family:system-ui;padding:2em;text-align:center">'
           . '<h2>Deze link klopt niet</h2><p>Vraag de juiste bestel-link opnieuw op.</p>';
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

// ---- Database ----
try {
    $db = new PDO($config['db_dsn'], $config['db_gebruiker'], $config['db_wachtwoord'], [
        PDO::ATTR_ERRMODE => PDO::ERRMODE_EXCEPTION,
        PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
    ]);
} catch (PDOException $e) {
    antwoord(['fout' => 'databaseverbinding mislukt'], 500);
}

$db->exec("CREATE TABLE IF NOT EXISTS wm_ah_snapshot (
    id TINYINT NOT NULL PRIMARY KEY,
    inhoud MEDIUMTEXT NOT NULL,
    bijgewerkt DATETIME NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

$db->exec("CREATE TABLE IF NOT EXISTS wm_ah_bestellingen (
    id CHAR(24) NOT NULL PRIMARY KEY,
    inhoud MEDIUMTEXT NOT NULL,
    status ENUM('wacht','verwerkt') NOT NULL DEFAULT 'wacht',
    melding VARCHAR(300) NOT NULL DEFAULT '',
    aangemaakt DATETIME NOT NULL,
    bijgewerkt DATETIME NOT NULL,
    INDEX idx_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

$db->exec("CREATE TABLE IF NOT EXISTS wm_ah_gerechtwensen (
    id CHAR(24) NOT NULL PRIMARY KEY,
    tekst TEXT NOT NULL,
    status ENUM('wacht','verwerkt') NOT NULL DEFAULT 'wacht',
    melding VARCHAR(300) NOT NULL DEFAULT '',
    aangemaakt DATETIME NOT NULL,
    bijgewerkt DATETIME NOT NULL,
    INDEX idx_status (status)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

// Oude rijen opruimen (goedkoop genoeg om bij elke aanroep te doen).
$db->exec("DELETE FROM wm_ah_bestellingen WHERE aangemaakt < (NOW() - INTERVAL 14 DAY)");
$db->exec("DELETE FROM wm_ah_gerechtwensen WHERE aangemaakt < (NOW() - INTERVAL 14 DAY)");

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
        $rij = $db->query('SELECT inhoud, bijgewerkt FROM wm_ah_snapshot WHERE id = 1')->fetch();
        if ($rij === false) {
            antwoord(['snapshot' => null, 'bijgewerkt' => null]);
        }
        antwoord([
            'snapshot' => json_decode($rij['inhoud'], true),
            'bijgewerkt' => $rij['bijgewerkt'],
        ]);
    }

    case 'snapshot': {
        if (!isset($body['snapshot']) || !is_array($body['snapshot'])) {
            antwoord(['fout' => 'snapshot ontbreekt'], 400);
        }
        $db->prepare("REPLACE INTO wm_ah_snapshot (id, inhoud, bijgewerkt) VALUES (1, :inhoud, NOW())")
            ->execute(['inhoud' => json_encode($body['snapshot'], JSON_UNESCAPED_UNICODE)]);
        antwoord(['ok' => true]);
    }

    case 'bestel': {
        $inhoud = $body['inhoud'] ?? null;
        if (!is_array($inhoud) || !isset($inhoud['producten']) || !is_array($inhoud['producten'])) {
            antwoord(['fout' => 'inhoud ontbreekt'], 400);
        }
        $id = bin2hex(random_bytes(12));
        $db->prepare("INSERT INTO wm_ah_bestellingen (id, inhoud, aangemaakt, bijgewerkt)
                      VALUES (:id, :inhoud, NOW(), NOW())")
            ->execute(['id' => $id, 'inhoud' => json_encode($inhoud, JSON_UNESCAPED_UNICODE)]);
        antwoord(['id' => $id]);
    }

    case 'status': {
        $id = $_REQUEST['id'] ?? '';
        if (!is_string($id) || strlen($id) > 24) {
            antwoord(['fout' => 'id ontbreekt'], 400);
        }
        $stmt = $db->prepare('SELECT status, melding FROM wm_ah_bestellingen WHERE id = :id');
        $stmt->execute(['id' => $id]);
        $rij = $stmt->fetch();
        if ($rij === false) {
            antwoord(['fout' => 'bestelling onbekend'], 404);
        }
        antwoord(['status' => $rij['status'], 'melding' => $rij['melding']]);
    }

    case 'ahwerk': {
        $stmt = $db->query("SELECT id, inhoud FROM wm_ah_bestellingen
                            WHERE status = 'wacht' ORDER BY aangemaakt ASC LIMIT 5");
        $bestellingen = [];
        foreach ($stmt as $rij) {
            $bestellingen[] = ['id' => $rij['id'], 'inhoud' => json_decode($rij['inhoud'], true)];
        }
        antwoord(['bestellingen' => $bestellingen]);
    }

    case 'ahklaar': {
        $id = $body['id'] ?? '';
        $melding = $body['melding'] ?? '';
        if (!is_string($id) || $id === '' || strlen($id) > 24) {
            antwoord(['fout' => 'id ontbreekt'], 400);
        }
        $db->prepare("UPDATE wm_ah_bestellingen
                      SET status = 'verwerkt', melding = :melding, bijgewerkt = NOW()
                      WHERE id = :id")
            ->execute(['melding' => is_string($melding) ? substr($melding, 0, 300) : '', 'id' => $id]);
        antwoord(['ok' => true]);
    }

    case 'gerechtwens': {
        $tekst = $body['tekst'] ?? '';
        if (!is_string($tekst) || trim($tekst) === '') {
            antwoord(['fout' => 'tekst ontbreekt'], 400);
        }
        $id = bin2hex(random_bytes(12));
        $db->prepare("INSERT INTO wm_ah_gerechtwensen (id, tekst, aangemaakt, bijgewerkt)
                      VALUES (:id, :tekst, NOW(), NOW())")
            ->execute(['id' => $id, 'tekst' => substr(trim($tekst), 0, 1000)]);
        antwoord(['id' => $id]);
    }

    case 'wensstatus': {
        $id = $_REQUEST['id'] ?? '';
        if (!is_string($id) || strlen($id) > 24) {
            antwoord(['fout' => 'id ontbreekt'], 400);
        }
        $stmt = $db->prepare('SELECT status, melding FROM wm_ah_gerechtwensen WHERE id = :id');
        $stmt->execute(['id' => $id]);
        $rij = $stmt->fetch();
        if ($rij === false) {
            antwoord(['fout' => 'wens onbekend'], 404);
        }
        antwoord(['status' => $rij['status'], 'melding' => $rij['melding']]);
    }

    case 'wenswerk': {
        $stmt = $db->query("SELECT id, tekst FROM wm_ah_gerechtwensen
                            WHERE status = 'wacht' ORDER BY aangemaakt ASC LIMIT 3");
        $wensen = [];
        foreach ($stmt as $rij) {
            $wensen[] = ['id' => $rij['id'], 'tekst' => $rij['tekst']];
        }
        antwoord(['wensen' => $wensen]);
    }

    case 'wensklaar': {
        $id = $body['id'] ?? '';
        $melding = $body['melding'] ?? '';
        if (!is_string($id) || $id === '' || strlen($id) > 24) {
            antwoord(['fout' => 'id ontbreekt'], 400);
        }
        $db->prepare("UPDATE wm_ah_gerechtwensen
                      SET status = 'verwerkt', melding = :melding, bijgewerkt = NOW()
                      WHERE id = :id")
            ->execute(['melding' => is_string($melding) ? substr($melding, 0, 300) : '', 'id' => $id]);
        antwoord(['ok' => true]);
    }

    default:
        antwoord(['fout' => 'onbekende actie'], 400);
}

// ---- De webpagina (nowdoc: geen PHP-interpolatie, dus JS-template-literals zijn veilig) ----
function pagina(): string
{
    return <<<'HTML'
<!doctype html>
<html lang="nl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#00ade6">
<meta name="apple-mobile-web-app-capable" content="yes">
<title>AH-boodschappen</title>
<style>
  :root {
    --blauw: #00ade6; --blauw-donker: #0089b8; --inkt: #1a2b3c; --grijs: #64748b;
    --lijn: #e2e8f0; --vlak: #f4f7fa; --wit: #fff; --oranje: #e8630c; --rood: #d43f3f;
    --groen: #1a9e60; --radius: 14px;
  }
  * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
  body {
    margin: 0; background: var(--vlak); color: var(--inkt);
    font: 16px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
    padding-bottom: calc(86px + env(safe-area-inset-bottom));
  }
  header {
    background: var(--blauw); color: var(--wit); padding: 14px 16px 12px;
    padding-top: calc(14px + env(safe-area-inset-top));
    position: sticky; top: 0; z-index: 10; box-shadow: 0 2px 8px rgba(0,0,0,.12);
  }
  header h1 { margin: 0; font-size: 20px; font-weight: 700; }
  header .sub { font-size: 12.5px; opacity: .85; margin-top: 2px; }
  main { padding: 14px 12px 0; max-width: 720px; margin: 0 auto; }
  h2 { font-size: 15px; text-transform: uppercase; letter-spacing: .4px;
       color: var(--grijs); margin: 20px 4px 10px; }
  .melding { text-align: center; color: var(--grijs); padding: 40px 20px; }

  /* Personen-teller */
  .personen { display: flex; align-items: center; gap: 12px; background: var(--wit);
    border-radius: var(--radius); padding: 10px 14px; border: 1px solid var(--lijn); }
  .personen span { flex: 1; }
  .teller { display: flex; align-items: center; gap: 14px; }
  .teller button { width: 38px; height: 38px; border-radius: 50%; border: 1px solid var(--lijn);
    background: var(--vlak); font-size: 20px; color: var(--blauw-donker); font-weight: 700; }
  .teller b { min-width: 20px; text-align: center; font-size: 18px; }

  /* Gerechtkaarten */
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 10px; }
  .kaart { background: var(--wit); border-radius: var(--radius); overflow: hidden;
    border: 2px solid transparent; box-shadow: 0 1px 3px rgba(15,40,80,.08);
    position: relative; cursor: pointer; }
  .kaart.aan { border-color: var(--blauw); }
  .kaart .foto { height: 108px; background: linear-gradient(135deg,#d9f1fb,#eef6fa);
    background-size: cover; background-position: center; display: flex;
    align-items: center; justify-content: center; font-size: 34px; }
  .kaart .tekst { padding: 8px 10px 10px; }
  .kaart .naam { font-size: 14px; font-weight: 600; line-height: 1.25;
    display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden;
    min-height: 2.5em; }
  .kaart .meta { font-size: 12px; color: var(--grijs); margin-top: 4px; }
  .kaart .vink { position: absolute; top: 8px; right: 8px; width: 26px; height: 26px;
    border-radius: 50%; background: rgba(255,255,255,.9); border: 1.5px solid var(--lijn);
    display: flex; align-items: center; justify-content: center; color: transparent;
    font-size: 15px; font-weight: 800; }
  .kaart.aan .vink { background: var(--blauw); border-color: var(--blauw); color: var(--wit); }
  .kaart .bonus { position: absolute; top: 8px; left: 8px; background: var(--oranje);
    color: var(--wit); font-size: 11px; font-weight: 700; border-radius: 6px; padding: 2px 6px; }

  /* Rubrieken: compacte rijen */
  .rubriek { display: flex; align-items: center; gap: 10px; background: var(--wit);
    border: 2px solid transparent; border-radius: var(--radius); padding: 12px 14px;
    margin-bottom: 8px; cursor: pointer; box-shadow: 0 1px 3px rgba(15,40,80,.08); }
  .rubriek.aan { border-color: var(--blauw); }
  .rubriek .icoon { font-size: 22px; }
  .rubriek .info { flex: 1; }
  .rubriek .info b { font-size: 15px; }
  .rubriek .info small { display: block; color: var(--grijs); font-size: 12.5px; }
  .rubriek .vink { width: 26px; height: 26px; border-radius: 50%; border: 1.5px solid var(--lijn);
    display: flex; align-items: center; justify-content: center; color: transparent;
    font-weight: 800; flex: none; }
  .rubriek.aan .vink { background: var(--blauw); border-color: var(--blauw); color: var(--wit); }

  /* Stap 2: productregels */
  .regel { display: flex; align-items: center; gap: 10px; background: var(--wit);
    border-radius: var(--radius); padding: 9px 10px; margin-bottom: 7px;
    border: 1px solid var(--lijn); }
  .regel.uit { opacity: .45; }
  .regel .box { width: 26px; height: 26px; border-radius: 8px; border: 1.5px solid var(--lijn);
    display: flex; align-items: center; justify-content: center; color: transparent;
    font-weight: 800; flex: none; cursor: pointer; }
  .regel.aan .box { background: var(--blauw); border-color: var(--blauw); color: var(--wit); }
  .regel img { width: 44px; height: 44px; object-fit: contain; border-radius: 8px;
    background: var(--vlak); flex: none; }
  .regel .wat { flex: 1; min-width: 0; }
  .regel .wat .n { font-size: 14.5px; font-weight: 600; }
  .regel .wat .t { font-size: 12.5px; color: var(--grijs); white-space: nowrap;
    overflow: hidden; text-overflow: ellipsis; }
  .regel .wat .waarschuw { color: var(--rood); font-weight: 600; }
  .regel .wat .bonustxt { color: var(--oranje); font-weight: 600; }
  .regel .prijs { font-size: 13.5px; text-align: right; white-space: nowrap; }
  .regel .prijs .b { color: var(--oranje); font-weight: 700; }
  .regel .stap { display: flex; align-items: center; gap: 8px; flex: none; }
  .regel .stap button { width: 30px; height: 30px; border-radius: 50%;
    border: 1px solid var(--lijn); background: var(--vlak); font-size: 16px;
    color: var(--blauw-donker); font-weight: 700; }
  .regel .stap b { min-width: 16px; text-align: center; }

  .veld { width: 100%; border: 1px solid var(--lijn); border-radius: var(--radius);
    padding: 12px 14px; font-size: 16px; background: var(--wit); margin-top: 6px; }

  /* Stap 3: gerechten inplannen */
  .plan { background: var(--wit); border: 1px solid var(--lijn); border-radius: var(--radius);
    padding: 12px 14px; margin-bottom: 8px; }
  .plan > b { display: block; font-size: 15px; margin-bottom: 8px; }
  .plan .rij { display: flex; gap: 10px; align-items: center; }
  .plan select { flex: 1; min-width: 0; border: 1px solid var(--lijn); border-radius: 10px;
    padding: 10px; font-size: 15px; background: var(--vlak); color: var(--inkt); }
  .plan label { display: flex; gap: 6px; align-items: center; font-size: 13.5px;
    color: var(--grijs); white-space: nowrap; }
  .plan input[type=checkbox] { width: 18px; height: 18px; accent-color: var(--blauw); }

  /* Onderbalk */
  .balk { position: fixed; left: 0; right: 0; bottom: 0; background: var(--wit);
    border-top: 1px solid var(--lijn); padding: 12px 14px calc(12px + env(safe-area-inset-bottom));
    display: flex; align-items: center; gap: 12px; z-index: 20; }
  .balk .som { flex: 1; font-size: 14px; color: var(--grijs); }
  .balk .som b { display: block; color: var(--inkt); font-size: 17px; }
  .knop { border: 0; border-radius: 999px; background: var(--blauw); color: var(--wit);
    font-size: 16px; font-weight: 700; padding: 13px 22px; cursor: pointer; }
  .knop:disabled { background: #b6c3cf; }
  .knop.stil { background: var(--vlak); color: var(--blauw-donker);
    border: 1px solid var(--lijn); font-weight: 600; }

  /* Stap 3 */
  .klaarkaart { background: var(--wit); border-radius: var(--radius); padding: 28px 20px;
    text-align: center; border: 1px solid var(--lijn); margin-top: 24px; }
  .klaarkaart .bol { width: 64px; height: 64px; border-radius: 50%; margin: 0 auto 14px;
    display: flex; align-items: center; justify-content: center; font-size: 30px;
    background: #e5f7ee; }
  .spin { width: 26px; height: 26px; border: 3px solid var(--lijn);
    border-top-color: var(--blauw); border-radius: 50%; margin: 0 auto 14px;
    animation: dr .8s linear infinite; }
  @keyframes dr { to { transform: rotate(360deg); } }
</style>
</head>
<body>
<header>
  <h1>🛒 AH-boodschappen</h1>
  <div class="sub" id="subkop">Laden…</div>
</header>
<main>
  <div id="stap1" hidden>
    <div class="personen">
      <span>Koken voor</span>
      <div class="teller">
        <button type="button" onclick="personen(-1)">−</button>
        <b id="personenGetal">4</b>
        <button type="button" onclick="personen(1)">+</button>
      </div>
      <span style="flex:none;color:var(--grijs)">personen</span>
    </div>
    <div id="secties"></div>
    <h2>Zelf een gerecht voorstellen</h2>
    <div class="wens">
      <p style="color:var(--grijs);font-size:13.5px;margin:0 0 8px">
        Beschrijf wat je graag zou eten — de pc maakt er een gerecht met ingrediënten en
        recept van, en Maarten kijkt het na.</p>
      <textarea class="veld" id="wensTekst" rows="3" maxlength="500"
        placeholder="Bv. lasagne met spinazie en zalm"></textarea>
      <div style="display:flex;justify-content:flex-end;margin-top:8px">
        <button type="button" class="knop" id="wensKnop" onclick="stuurWens()">Voorstellen</button>
      </div>
      <p id="wensStatus" style="color:var(--grijs);font-size:13.5px;margin:8px 0 0"></p>
    </div>
  </div>

  <div id="stap2" hidden>
    <h2>Boodschappen nakijken</h2>
    <p style="color:var(--grijs);font-size:13.5px;margin:0 4px 12px">
      Vink af wat níét mee moet; aantallen kun je aanpassen.</p>
    <div id="regels"></div>
    <div id="handmatigBlok" hidden>
      <h2>Zelf zoeken op ah.be</h2>
      <p style="color:var(--grijs);font-size:13.5px;margin:0 4px 12px">
        Hier vonden we geen vast product bij — deze komen als notitie mee.</p>
      <div id="handmatigRegels"></div>
    </div>
  </div>

  <div id="stap3" hidden>
    <h2>Wanneer eten jullie wat?</h2>
    <p style="color:var(--grijs);font-size:13.5px;margin:0 4px 12px">
      Kies per gerecht een dag (mag ook leeg blijven) — de afspraak komt met het recept
      in de agenda. Avonden waarop al iets gepland staat zijn gemarkeerd.</p>
    <div id="planRegels"></div>
  </div>

  <div id="stap4" hidden>
    <div class="klaarkaart" id="klaarkaart">
      <div class="spin" id="klaarSpin"></div>
      <div class="bol" id="klaarBol" hidden>✓</div>
      <h3 id="klaarTitel" style="margin:0 0 6px">Bestelling doorgestuurd</h3>
      <p id="klaarTekst" style="color:var(--grijs);margin:0">
        Zodra de pc thuis aanstaat, worden de boodschappen in het AH-mandje gelegd.</p>
    </div>
  </div>

  <div id="laadMelding" class="melding">Boodschappenlijst laden…</div>
</main>

<div class="balk" id="balk" hidden>
  <button type="button" class="knop stil" id="terugKnop" hidden>‹ Terug</button>
  <div class="som"><span id="somLabel"></span><b id="somBedrag"></b></div>
  <button type="button" class="knop" id="verderKnop"></button>
</div>

<script>
const TOKEN = '__TOKEN__';
const api = (actie, opties) =>
  fetch(`ah.php?actie=${actie}&token=${encodeURIComponent(TOKEN)}`, opties).then(r => r.json());

let snap = null;           // snapshot van de pc
let gekozen = new Set();   // gekozen gerecht-/rubrieknamen
let regels = [];           // stap 2: [{naam, url, aantal, aan, titel, beeld, prijs, ...}]
let handmatigRegels = [];  // stap 2: namen zonder product
let stap = 1;
let aantalPersonen = parseInt(localStorage.getItem('ahPersonen') || '4', 10);

const euro = b => '€ ' + b.toFixed(2).replace('.', ',');
const el = id => document.getElementById(id);

function personen(d) {
  aantalPersonen = Math.min(12, Math.max(1, aantalPersonen + d));
  localStorage.setItem('ahPersonen', String(aantalPersonen));
  el('personenGetal').textContent = aantalPersonen;
}

// ---- Stap 1: kaarten ----

function gerechtPrijs(g) {
  let som = 0;
  for (const i of g.ingredienten) {
    if (i.standaard && i.prijs) som += i.prijs * (i.aantal || 1);
  }
  return som;
}

function kaartHtml(g, sectie) {
  const key = `${sectie}::${g.naam}`;
  const aan = gekozen.has(key) ? ' aan' : '';
  const foto = g.foto ? ` style="background-image:url('${g.foto.replace(/'/g, '%27')}')"` : '';
  const bonus = g.ingredienten.filter(i => i.bonus).length;
  const meta = [];
  if (g.minuten) meta.push(`⏱ ${g.minuten} min`);
  const p = gerechtPrijs(g);
  if (p > 0) meta.push('≈ ' + euro(p));
  return `<div class="kaart${aan}" data-key="${key}" onclick="wissel(this)">
    <div class="foto"${foto}>${g.foto ? '' : '🍽️'}</div>
    ${bonus ? `<span class="bonus">🏷 ${bonus} bonus</span>` : ''}
    <span class="vink">✓</span>
    <div class="tekst"><div class="naam">${g.naam}</div><div class="meta">${meta.join(' · ')}</div></div>
  </div>`;
}

function rubriekHtml(g) {
  const key = `rubrieken::${g.naam}`;
  const aan = gekozen.has(key) ? ' aan' : '';
  const iconen = { 'Boterhambeleg': '🥪', 'Groenten': '🥕', 'Fruit': '🍎', 'Snacks': '🧀',
                   'Koekjes': '🍪', 'Non-food': '🧻' };
  const std = g.ingredienten.filter(i => i.standaard).length;
  return `<div class="rubriek${aan}" data-key="${key}" onclick="wissel(this)">
    <span class="icoon">${iconen[g.naam] || '🛍️'}</span>
    <span class="info"><b>${g.naam}</b>
      <small>${g.ingredienten.length} producten, ${std} standaard aangevinkt</small></span>
    <span class="vink">✓</span>
  </div>`;
}

function toonStap1() {
  const s = el('secties');
  let html = '';
  if (snap.gerechten.length) {
    html += '<h2>Gerechten</h2><div class="grid">'
          + snap.gerechten.map(g => kaartHtml(g, 'gerechten')).join('') + '</div>';
  }
  if (snap.suggesties.length) {
    html += '<h2>Suggesties van de week</h2><div class="grid">'
          + snap.suggesties.map(g => kaartHtml(g, 'suggesties')).join('') + '</div>';
  }
  if (snap.rubrieken.length) {
    html += '<h2>Vaste rubrieken</h2>'
          + snap.rubrieken.map(rubriekHtml).join('');
  }
  s.innerHTML = html;
  el('personenGetal').textContent = aantalPersonen;
}

function wissel(kaart) {
  const key = kaart.dataset.key;
  if (gekozen.has(key)) { gekozen.delete(key); kaart.classList.remove('aan'); }
  else { gekozen.add(key); kaart.classList.add('aan'); }
  werkBalkBij();
}

// ---- Stap 2: samenvoegen zoals op de pc ----

function vindGroep(key) {
  const [sectie, ...rest] = key.split('::');
  const naam = rest.join('::');
  return { sectie, groep: (snap[sectie] || []).find(g => g.naam === naam) };
}

function bouwRegels() {
  const perNaam = new Map();
  for (const key of gekozen) {
    const { sectie, groep } = vindGroep(key);
    if (!groep) continue;
    // Rubrieken schalen niet mee met het aantal eters (één pak wasmiddel blijft één pak).
    const factor = sectie === 'rubrieken' ? 1
      : Math.max(1, Math.ceil(aantalPersonen / (groep.personen || 4)));
    for (const i of groep.ingredienten) {
      const sleutel = i.naam.toLowerCase();
      const bestaand = perNaam.get(sleutel);
      if (bestaand) {
        bestaand.aantal += factor * (i.aantal || 1);
        bestaand.aan = bestaand.aan || i.standaard;
        if (!bestaand.url && i.url) Object.assign(bestaand,
          { url: i.url, titel: i.titel, beeld: i.beeld, prijs: i.prijs,
            prijsVoorBonus: i.prijsVoorBonus, bonus: i.bonus, gluten: i.gluten, gok: i.gok });
        if (!bestaand.herkomst.includes(groep.naam)) bestaand.herkomst.push(groep.naam);
      } else {
        perNaam.set(sleutel, {
          naam: i.naam, url: i.url, titel: i.titel, beeld: i.beeld, prijs: i.prijs,
          prijsVoorBonus: i.prijsVoorBonus, bonus: i.bonus, gluten: i.gluten, gok: i.gok,
          aantal: factor * (i.aantal || 1), aan: i.standaard, herkomst: [groep.naam],
        });
      }
    }
  }
  const alles = [...perNaam.values()];
  regels = alles.filter(r => r.url);
  handmatigRegels = alles.filter(r => !r.url);
}

function regelHtml(r, i) {
  const delen = [];
  if (r.titel && r.titel.toLowerCase() !== r.naam.toLowerCase()) delen.push((r.gok ? '≈ ' : '') + r.titel);
  if (r.herkomst.length) delen.push(r.herkomst.join(', '));
  const waarschuw = r.gluten ? ' <span class="waarschuw">⚠ gluten</span>' : '';
  const bonustxt = r.bonus && r.prijsVoorBonus && r.prijs < r.prijsVoorBonus
    ? ` <span class="bonustxt">bonus</span>` : '';
  const prijs = r.prijs
    ? `<span class="${r.bonus ? 'b' : ''}">${euro(r.prijs * r.aantal)}</span>` : '';
  return `<div class="regel${r.aan ? ' aan' : ' uit'}" id="regel${i}">
    <span class="box" onclick="regelWissel(${i})">✓</span>
    ${r.beeld ? `<img src="${r.beeld}" alt="" loading="lazy" onerror="this.style.visibility='hidden'">`
              : '<img alt="" style="visibility:hidden">'}
    <span class="wat" onclick="regelWissel(${i})">
      <span class="n">${r.naam}${waarschuw}${bonustxt}</span>
      <span class="t">${delen.join(' · ')}</span>
    </span>
    <span class="stap">
      <button type="button" onclick="aantal(${i},-1)">−</button><b>${r.aantal}</b>
      <button type="button" onclick="aantal(${i},1)">+</button>
    </span>
    <span class="prijs">${prijs}</span>
  </div>`;
}

function toonStap2() {
  el('regels').innerHTML = regels.map(regelHtml).join('');
  el('handmatigBlok').hidden = handmatigRegels.length === 0;
  el('handmatigRegels').innerHTML = handmatigRegels.map((r, i) => `
    <div class="regel${r.aan ? ' aan' : ' uit'}" id="hand${i}">
      <span class="box" onclick="handWissel(${i})">✓</span>
      <span class="wat" onclick="handWissel(${i})"><span class="n">${r.naam}</span>
        <span class="t">${r.herkomst.join(', ')}</span></span>
    </div>`).join('');
}

function regelWissel(i) {
  regels[i].aan = !regels[i].aan;
  document.getElementById('regel' + i).className = 'regel' + (regels[i].aan ? ' aan' : ' uit');
  werkBalkBij();
}

function handWissel(i) {
  handmatigRegels[i].aan = !handmatigRegels[i].aan;
  document.getElementById('hand' + i).className = 'regel' + (handmatigRegels[i].aan ? ' aan' : ' uit');
}

function aantal(i, d) {
  const r = regels[i];
  r.aantal = Math.min(99, Math.max(1, r.aantal + d));
  document.getElementById('regel' + i).outerHTML = regelHtml(r, i);
  werkBalkBij();
}

// ---- Stap 3: gerechten op een dag plannen ----

let agendaKeuzes = [];

const maaltijden = () => [...gekozen]
  .filter(k => k.startsWith('gerechten::') || k.startsWith('suggesties::'))
  .map(k => k.split('::').slice(1).join('::'));

function dagenLijst() {
  const bezet = new Set(snap.agendaBezet || []);
  const dagen = [];
  for (let i = 1; i <= 14; i++) {
    const d = new Date(); d.setDate(d.getDate() + i);
    const iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-` +
                String(d.getDate()).padStart(2, '0');
    const label = (i === 1 ? 'morgen · ' : '') +
      d.toLocaleDateString('nl-BE', { weekday: 'short', day: 'numeric', month: 'short' });
    dagen.push({ iso, label, bezet: bezet.has(iso) });
  }
  return dagen;
}

function toonStap3() {
  const dagen = dagenLijst();
  const gebruikt = new Set();
  // Standaard elk gerecht op de eerstvolgende vríje avond (zoals de planner op de pc).
  agendaKeuzes = maaltijden().map(naam => {
    let iso = '';
    for (const dag of dagen) {
      if (!dag.bezet && !gebruikt.has(dag.iso)) { iso = dag.iso; gebruikt.add(dag.iso); break; }
    }
    return { gerecht: naam, datum: iso, middag: false };
  });
  el('planRegels').innerHTML = agendaKeuzes.map((k, i) => `
    <div class="plan"><b>${k.gerecht}</b>
      <div class="rij">
        <select onchange="planDatum(${i}, this.value)">
          <option value="">Niet inplannen</option>
          ${dagen.map(d => `<option value="${d.iso}"${d.iso === k.datum ? ' selected' : ''}>` +
            `${d.label}${d.bezet ? ' — avond bezet' : ''}</option>`).join('')}
        </select>
        <label><input type="checkbox" onchange="planMiddag(${i}, this.checked)"> ’s middags</label>
      </div>
    </div>`).join('');
}

function planDatum(i, v) { agendaKeuzes[i].datum = v; werkBalkBij(); }
function planMiddag(i, v) { agendaKeuzes[i].middag = v; }

// ---- Navigatie + onderbalk ----

function naarStap(n) {
  stap = n;
  el('stap1').hidden = n !== 1;
  el('stap2').hidden = n !== 2;
  el('stap3').hidden = n !== 3;
  el('stap4').hidden = n !== 4;
  el('terugKnop').hidden = n !== 2 && n !== 3;
  el('terugKnop').onclick = () => naarStap(n - 1);
  el('balk').hidden = n === 4;
  if (n === 2) { bouwRegels(); toonStap2(); }
  if (n === 3) { toonStap3(); }
  window.scrollTo(0, 0);
  werkBalkBij();
}

function werkBalkBij() {
  const knop = el('verderKnop');
  if (stap === 1) {
    el('somLabel').textContent = 'gekozen';
    el('somBedrag').textContent = gekozen.size + (gekozen.size === 1 ? ' item' : ' items');
    knop.textContent = 'Verder ›';
    knop.disabled = gekozen.size === 0;
    knop.onclick = () => naarStap(2);
  } else if (stap === 2) {
    const aan = regels.filter(r => r.aan);
    const som = aan.reduce((s, r) => s + (r.prijs || 0) * r.aantal, 0);
    const bonus = aan.reduce((s, r) => s + (r.bonus && r.prijsVoorBonus && r.prijs < r.prijsVoorBonus
      ? (r.prijsVoorBonus - r.prijs) * r.aantal : 0), 0);
    el('somLabel').textContent = `${aan.length} producten` +
      (bonus > 0 ? ` · ${euro(bonus)} bonusvoordeel` : '');
    el('somBedrag').textContent = '≈ ' + euro(som);
    const verder = maaltijden().length > 0;
    knop.textContent = verder ? 'Verder ›' : 'Doorsturen 🛒';
    knop.disabled = aan.length === 0 && !handmatigRegels.some(r => r.aan);
    knop.onclick = verder ? () => naarStap(3) : verstuur;
  } else if (stap === 3) {
    const n = agendaKeuzes.filter(k => k.datum).length;
    el('somLabel').textContent = 'in de agenda';
    el('somBedrag').textContent = n + (n === 1 ? ' gerecht' : ' gerechten');
    knop.textContent = 'Doorsturen 🛒';
    knop.disabled = false;
    knop.onclick = verstuur;
  }
}

// ---- Gerecht voorstellen ----

async function stuurWens() {
  const tekst = el('wensTekst').value.trim();
  if (!tekst) return;
  const knop = el('wensKnop');
  knop.disabled = true;
  el('wensStatus').textContent = 'Versturen…';
  try {
    const res = await api('gerechtwens', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ tekst }),
    });
    if (!res.id) throw new Error(res.fout || 'onbekende fout');
    el('wensTekst').value = '';
    el('wensStatus').textContent = 'Doorgestuurd — zodra de pc thuis aanstaat wordt er een gerecht van gemaakt…';
    volgWens(res.id);
  } catch (e) {
    el('wensStatus').textContent = 'Versturen mislukte: ' + e.message;
  }
  knop.disabled = false;
}

async function volgWens(id) {
  for (let i = 0; i < 60; i++) {          // ± 10 minuten volgen
    await new Promise(r => setTimeout(r, 10000));
    try {
      const res = await api(`wensstatus&id=${id}`);
      if (res.status === 'verwerkt') {
        el('wensStatus').textContent = res.melding || 'Gerecht aangemaakt!';
        return;
      }
    } catch (e) { /* even geen bereik: gewoon opnieuw */ }
  }
}

// ---- Versturen + status ----

async function verstuur() {
  const knop = el('verderKnop');
  knop.disabled = true;
  knop.textContent = 'Versturen…';
  const namen = new Set(maaltijden());
  const inhoud = {
    wie: 'gsm',
    personen: aantalPersonen,
    gerechten: [...gekozen].map(k => k.split('::').slice(1).join('::')),
    producten: regels.filter(r => r.aan).map(r => ({ naam: r.naam, url: r.url, aantal: r.aantal })),
    handmatig: handmatigRegels.filter(r => r.aan).map(r => r.naam),
    // Alleen plannen wat nu nog gekozen is (terugbladeren kan keuzes geschrapt hebben).
    agenda: agendaKeuzes.filter(k => k.datum && namen.has(k.gerecht))
      .map(k => ({ gerecht: k.gerecht, datum: k.datum, middag: k.middag })),
  };
  try {
    const res = await api('bestel', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ inhoud }),
    });
    if (!res.id) throw new Error(res.fout || 'onbekende fout');
    naarStap(4);
    volgStatus(res.id);
  } catch (e) {
    alert('Versturen mislukte: ' + e.message);
    knop.disabled = false;
    werkBalkBij();
  }
}

async function volgStatus(id) {
  for (let i = 0; i < 75; i++) {           // ± 10 minuten volgen
    await new Promise(r => setTimeout(r, 8000));
    try {
      const res = await api(`status&id=${id}`);
      if (res.status === 'verwerkt') {
        el('klaarSpin').hidden = true;
        el('klaarBol').hidden = false;
        el('klaarTitel').textContent = 'In het AH-mandje gelegd!';
        el('klaarTekst').textContent = res.melding || 'De pc heeft de boodschappen verwerkt.';
        return;
      }
    } catch (e) { /* even geen bereik: gewoon opnieuw */ }
  }
  el('klaarSpin').hidden = true;
  el('klaarTekst').textContent =
    'De pc heeft de bestelling nog niet opgepikt — dat gebeurt zodra hij thuis aanstaat.';
}

// ---- Start ----

(async () => {
  try {
    const res = await api('data');
    if (!res.snapshot) {
      el('laadMelding').textContent =
        'Nog geen boodschappenlijst beschikbaar — de pc thuis moet eerst één keer synchroniseren.';
      return;
    }
    snap = res.snapshot;
    const wanneer = res.bijgewerkt ? new Date(res.bijgewerkt.replace(' ', 'T')) : null;
    el('subkop').textContent = wanneer
      ? `Prijzen en gerechten van ${wanneer.toLocaleDateString('nl-BE', { weekday: 'long' })} ` +
        wanneer.toLocaleTimeString('nl-BE', { hour: '2-digit', minute: '2-digit' })
      : 'Kies de gerechten voor deze week';
    el('laadMelding').hidden = true;
    el('balk').hidden = false;
    toonStap1();
    naarStap(1);
  } catch (e) {
    el('laadMelding').textContent = 'Laden mislukte — controleer de internetverbinding.';
  }
})();
</script>
</body>
</html>
HTML;
}
