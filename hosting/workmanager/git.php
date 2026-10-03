<?php
/**
 * Online git-tabel: per project (solution) wat er op Maartens pc nog ongecommit openstaat,
 * met hoe lang elk bestand er al ligt. Bedoeld om te laten meevolgen — de link mag naar een
 * collega, vandaar een eigen token (git_token), los van wm_token en ah_token. De pagina kan
 * alleen lezen: er gaat niets van hier terug naar de pc.
 *
 * WorkManager (GitWebSync) zet elk uur een snapshot klaar. Elk snapshot wordt ook per uur
 * weggeschreven in een historiek, zodat de tabel niet alleen "nu" toont maar ook het verloop
 * van de laatste dagen (het staafje achter elk project).
 *
 * Acties (token = git_token uit config.php, via header X-Wm-Token of parameter t/token):
 *   GET  (geen actie)            -> de webpagina zelf (token in de link: git.php?t=…)
 *   GET  data                    -> {snapshot, bijgewerkt, historiek}
 *   POST snapshot   {snapshot}   -> {ok}                                   (van de pc)
 *   GET  versie                  -> {versie}   (welke code draait er echt; zie DEPLOY.md
 *                                               over de opcache van een minuut)
 */

declare(strict_types=1);

const GIT_VERSIE = '2026-10-02.2';

/** Hoe lang de historiek bewaard blijft. */
const HISTORIEK_DAGEN = 30;

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
if (!is_string($token) || !hash_equals($config['git_token'] ?? '', $token)) {
    if ($actie === '') {
        http_response_code(403);
        header('Content-Type: text/html; charset=utf-8');
        echo '<!doctype html><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
           . '<body style="font-family:system-ui;padding:2em;text-align:center">'
           . '<h2>Deze link klopt niet</h2><p>Vraag de juiste link opnieuw op bij Maarten.</p>';
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

if ($actie === 'versie') {
    antwoord(['versie' => GIT_VERSIE]);
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

$db->exec("CREATE TABLE IF NOT EXISTS wm_git_snapshot (
    id TINYINT NOT NULL PRIMARY KEY,
    inhoud MEDIUMTEXT NOT NULL,
    bijgewerkt DATETIME NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

// Eén regel per project per uur: het verloop, zonder dat de tabel ooit groot wordt.
$db->exec("CREATE TABLE IF NOT EXISTS wm_git_historiek (
    uur CHAR(13) NOT NULL,
    project VARCHAR(120) NOT NULL,
    aantal SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    staged SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    achter SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    moment DATETIME NOT NULL,
    PRIMARY KEY (uur, project),
    INDEX idx_moment (moment)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

$body = [];
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $ruw = file_get_contents('php://input');
    $body = is_string($ruw) && $ruw !== '' ? (json_decode($ruw, true) ?: []) : [];
}

switch ($actie) {
    case 'data': {
        $rij = $db->query('SELECT inhoud, bijgewerkt FROM wm_git_snapshot WHERE id = 1')->fetch();
        if ($rij === false) {
            antwoord(['snapshot' => null, 'bijgewerkt' => null, 'historiek' => []]);
        }
        // Het verloop van de laatste twee dagen, per project een reeks uurwaarden. Alleen
        // het aantal (meer heeft het staafje in de tabel niet nodig) en bewust niet de hele
        // bewaarde historiek: dat zijn duizenden regels die de pagina toch niet tekent.
        $stmt = $db->query("SELECT project, uur, aantal FROM wm_git_historiek
                            WHERE moment >= (NOW() - INTERVAL 48 HOUR)
                            ORDER BY uur ASC");
        $historiek = [];
        foreach ($stmt as $h) {
            $historiek[$h['project']][] = ['u' => $h['uur'], 'n' => (int)$h['aantal']];
        }
        antwoord([
            'snapshot' => json_decode($rij['inhoud'], true),
            // ISO-8601 mét tijdzone: anders moet elke lezer raden of dit UTC of lokale tijd is.
            'bijgewerkt' => date('c', strtotime($rij['bijgewerkt'])),
            'historiek' => $historiek,
            'versie' => GIT_VERSIE,
        ]);
    }

    case 'snapshot': {
        $snapshot = $body['snapshot'] ?? null;
        if (!is_array($snapshot)) {
            antwoord(['fout' => 'snapshot ontbreekt'], 400);
        }
        $db->prepare('REPLACE INTO wm_git_snapshot (id, inhoud, bijgewerkt) VALUES (1, :inhoud, NOW())')
            ->execute(['inhoud' => json_encode($snapshot, JSON_UNESCAPED_UNICODE)]);

        // Historiek bijschrijven: één regel per project per uur. Komt er binnen hetzelfde uur
        // nog een snapshot (bijvoorbeeld na "Nu online zetten"), dan overschrijft die de regel
        // van dat uur — zo blijft er precies één meting per uur staan.
        $uur = date('Y-m-d H');
        $invoegen = $db->prepare("REPLACE INTO wm_git_historiek
            (uur, project, aantal, staged, achter, moment)
            VALUES (:uur, :project, :aantal, :staged, :achter, NOW())");
        foreach (($snapshot['projecten'] ?? []) as $project) {
            if (!is_array($project) || !isset($project['naam'])) {
                continue;
            }
            $invoegen->execute([
                'uur' => $uur,
                'project' => mb_substr((string)$project['naam'], 0, 120),
                'aantal' => (int)($project['aantal'] ?? 0),
                'staged' => (int)($project['staged'] ?? 0),
                'achter' => (int)($project['achter'] ?? 0),
            ]);
        }
        $db->exec('DELETE FROM wm_git_historiek WHERE moment < (NOW() - INTERVAL '
            . HISTORIEK_DAGEN . ' DAY)');
        antwoord(['ok' => true, 'versie' => GIT_VERSIE]);
    }

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
<meta name="robots" content="noindex,nofollow">
<title>Git-stand — WorkManager</title>
<style>
:root {
  --bg: #0f0f16; --kaart: #1a1a24; --kaart2: #22222f; --rand: #2f2f3d;
  --tekst: #eceaf3; --grijs: #9b98ad; --accent: #6c8cff;
  --rood: #e8734c; --groen: #79c08a; --geel: #e8b54b;
}
* { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
html, body { max-width: 100%; }
body {
  margin: 0; background: var(--bg); color: var(--tekst);
  font: 16px/1.45 system-ui, -apple-system, sans-serif;
  padding: 0 0 env(safe-area-inset-bottom);
  --marge: max(14px, calc((100vw - 860px) / 2));
}
/* Paden hebben geen spaties; zonder dit duwen ze de pagina breder dan het scherm. */
.pad, .titel, .meta { overflow-wrap: anywhere; }
header {
  position: sticky; top: 0; z-index: 5; background: rgba(15,15,22,.94);
  backdrop-filter: blur(8px); border-bottom: 1px solid var(--rand);
  padding: calc(10px + env(safe-area-inset-top)) var(--marge) 9px;
}
header h1 { font-size: 17px; margin: 0; font-weight: 650; letter-spacing: .2px; }
header .stand { font-size: 12px; color: var(--grijs); margin-top: 2px; }
main { padding: 12px var(--marge) 60px; }
.balk {
  background: #3a2a1c; border: 1px solid #6b4a2a; color: #f0d6b8;
  border-radius: 12px; padding: 10px 13px; font-size: 13.5px; margin-bottom: 11px;
}
.tegels { display: flex; gap: 8px; margin-bottom: 13px; }
.tegel {
  flex: 1; background: var(--kaart); border: 1px solid var(--rand); border-radius: 13px;
  padding: 11px 12px; min-width: 0;
}
.tegel .n { font-size: 25px; font-weight: 650; line-height: 1.1; }
.tegel .l { font-size: 11.5px; color: var(--grijs); margin-top: 3px; }
nav { display: flex; gap: 7px; margin-bottom: 12px; }
nav button {
  font: inherit; color: var(--grijs); background: var(--kaart); border: 1px solid var(--rand);
  padding: 6px 14px; border-radius: 999px; font-size: 14px; cursor: pointer; white-space: nowrap;
}
nav button.aan { background: var(--accent); border-color: var(--accent); color: #0d1020; font-weight: 600; }
.kaart {
  background: var(--kaart); border: 1px solid var(--rand); border-radius: 14px;
  margin-bottom: 9px; overflow: hidden;
}
.kop {
  display: flex; align-items: center; gap: 10px; padding: 11px 13px; cursor: pointer;
  user-select: none;
}
.kop:active { background: var(--kaart2); }
.kop .naam { flex: 1; font-weight: 600; min-width: 0; overflow-wrap: anywhere; }
.kop .naam small { display: block; font-weight: 400; color: var(--grijs); font-size: 11.5px; }
.chip {
  flex: none; font-size: 11.5px; padding: 2px 8px; border-radius: 999px;
  background: var(--kaart2); border: 1px solid var(--rand); color: var(--grijs);
}
.chip.werk { background: rgba(232,115,76,.16); border-color: #6b3a28; color: #f0a98c; }
.chip.oud { background: rgba(232,115,76,.3); border-color: #8a4328; color: #ffcdb8; }
.chip.schoon { background: rgba(121,192,138,.14); border-color: #2f5c3c; color: #a7d9b4; }
.chip.remote { background: rgba(108,140,255,.14); border-color: #39436e; color: #b3c2ff; }
.verloop {
  flex: none; font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 15px;
  letter-spacing: 1px; color: var(--grijs); line-height: 1;
}
.pijl { flex: none; color: var(--grijs); font-size: 12px; width: 14px; text-align: center; }
.bestanden { border-top: 1px solid var(--rand); display: none; }
.kaart.open .bestanden { display: block; }
.bestand {
  display: flex; align-items: baseline; gap: 9px; padding: 7px 13px;
  border-bottom: 1px solid rgba(47,47,61,.5); font-size: 13.5px;
}
.bestand:last-child { border-bottom: none; }
.bestand .status { flex: none; width: 118px; color: var(--grijs); font-size: 11.5px; }
.bestand .pad { flex: 1; min-width: 0; font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 12.5px; }
.bestand .dagen { flex: none; color: var(--grijs); font-size: 11.5px; white-space: nowrap; }
.bestand .dagen.oud { color: #f0a98c; }
.leeg { color: var(--grijs); text-align: center; padding: 40px 10px; }
footer { color: var(--grijs); font-size: 11.5px; text-align: center; padding: 0 var(--marge) 30px; }
</style>
</head>
<body>
<header>
  <h1>Git-stand</h1>
  <div class="stand" id="stand">laden…</div>
</header>
<main>
  <div id="balk"></div>
  <div class="tegels">
    <div class="tegel"><div class="n" id="t-open">–</div><div class="l">ongecommit</div></div>
    <div class="tegel"><div class="n" id="t-projecten">–</div><div class="l">projecten met werk</div></div>
    <div class="tegel"><div class="n" id="t-achter">–</div><div class="l">achter op de remote</div></div>
  </div>
  <nav>
    <button id="f-werk" class="aan">Met werk</button>
    <button id="f-alles">Alle projecten</button>
  </nav>
  <div id="lijst"><div class="leeg">laden…</div></div>
</main>
<footer>
  WorkManager zet deze stand elk uur online · <span id="versie"></span>
</footer>
<script>
const TOKEN = '__TOKEN__';
const BLOKJES = ['▁', '▂', '▃', '▅', '▆', '▇'];
let data = null;
let filter = 'werk';

function geleden(iso) {
  const min = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (min < 2) return 'net';
  if (min < 60) return min + ' min geleden';
  const u = Math.round(min / 60);
  if (u < 24) return u + ' uur geleden';
  return Math.round(u / 24) + ' dag(en) geleden';
}

function dagen(n) {
  if (n === null || n === undefined || n < 0) return '';
  if (n === 0) return 'vandaag';
  if (n === 1) return '1 dag';
  if (n < 14) return n + ' dagen';
  if (n < 60) return Math.round(n / 7) + ' weken';
  return Math.round(n / 30) + ' maanden';
}

/* Het verloop als staafjes: de laatste 24 metingen, geschaald op de hoogste waarde. */
function verloop(reeks) {
  if (!reeks || reeks.length < 2) return '';
  const laatste = reeks.slice(-24).map(p => p.n);
  const max = Math.max(...laatste, 1);
  return laatste.map(n => BLOKJES[Math.min(BLOKJES.length - 1,
    Math.round((n / max) * (BLOKJES.length - 1)))]).join('');
}

function teken() {
  const lijst = document.getElementById('lijst');
  if (!data || !data.snapshot) {
    lijst.innerHTML = '<div class="leeg">Er staat nog geen stand online.</div>';
    return;
  }
  const s = data.snapshot;
  document.getElementById('t-open').textContent = s.totaal ?? 0;
  document.getElementById('t-projecten').textContent = s.metWerk ?? 0;
  document.getElementById('t-achter').textContent = s.achter ?? 0;
  document.getElementById('stand').textContent =
    'bijgewerkt ' + geleden(data.bijgewerkt) + ' · ' + (s.projecten || []).length + ' projecten'
    + (s.pc ? ' · ' + s.pc : '');
  document.getElementById('versie').textContent = data.versie || '';

  /* Staat de pc uit, dan is de stand oud: dat hoort erboven te staan en niet stil te blijven. */
  const oudMin = (Date.now() - new Date(data.bijgewerkt).getTime()) / 60000;
  document.getElementById('balk').innerHTML = oudMin > 150
    ? '<div class="balk">Dit is de stand van ' + geleden(data.bijgewerkt)
      + '. De pc stond sindsdien uit of had geen verbinding.</div>'
    : '';

  const projecten = (s.projecten || [])
    .filter(p => filter === 'alles' || p.aantal > 0 || p.achter > 0);
  if (projecten.length === 0) {
    lijst.innerHTML = '<div class="leeg">Niets ongecommit — alles staat in git. \u{1F389}</div>';
    return;
  }

  lijst.innerHTML = projecten.map((p, i) => {
    const oud = p.oudsteDagen >= 7;
    const chips = [];
    if (p.fout) {
      chips.push('<span class="chip">git?</span>');
    } else if (p.aantal > 0) {
      chips.push('<span class="chip ' + (oud ? 'oud' : 'werk') + '">' + p.aantal + ' open</span>');
      if (p.staged > 0) chips.push('<span class="chip">' + p.staged + ' staged</span>');
    } else {
      chips.push('<span class="chip schoon">schoon</span>');
    }
    if (p.achter > 0) chips.push('<span class="chip remote">↓ ' + p.achter + '</span>');
    if (p.voor > 0) chips.push('<span class="chip remote">↑ ' + p.voor + '</span>');

    const staafjes = verloop((data.historiek || {})[p.naam]);
    const bestanden = (p.bestanden || []).map(b =>
      '<div class="bestand"><span class="status">' + esc(b.status) + (b.gestaged ? ' · staged' : '')
      + '</span><span class="pad">' + esc(b.pad) + '</span>'
      + '<span class="dagen' + (b.dagen >= 7 ? ' oud' : '') + '">' + dagen(b.dagen) + '</span></div>').join('');

    return '<div class="kaart" data-i="' + i + '">'
      + '<div class="kop"><span class="pijl">' + (bestanden ? '▸' : '') + '</span>'
      + '<span class="naam">' + esc(p.naam)
      + '<small>' + esc(p.branch || '') + (p.aantal > 0 && !p.fout ? ' · oudste ' + dagen(p.oudsteDagen) : '')
      + (p.fout ? ' · ' + esc(p.fout) : '') + '</small></span>'
      + (staafjes ? '<span class="verloop" title="verloop van de laatste 24 uur">' + staafjes + '</span>' : '')
      + chips.join('') + '</div>'
      + (bestanden ? '<div class="bestanden">' + bestanden + '</div>' : '')
      + '</div>';
  }).join('');

  lijst.querySelectorAll('.kop').forEach(kop => {
    kop.addEventListener('click', () => {
      const kaart = kop.parentElement;
      kaart.classList.toggle('open');
      const pijl = kop.querySelector('.pijl');
      if (pijl.textContent) pijl.textContent = kaart.classList.contains('open') ? '▾' : '▸';
    });
  });
}

function esc(t) {
  return String(t ?? '').replace(/[&<>"]/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
}

async function laad() {
  try {
    const r = await fetch('git.php?actie=data&t=' + encodeURIComponent(TOKEN),
      { cache: 'no-store' });
    data = await r.json();
    teken();
  } catch (e) {
    document.getElementById('stand').textContent = 'geen verbinding — probeer te verversen';
  }
}

document.getElementById('f-werk').addEventListener('click', () => zetFilter('werk'));
document.getElementById('f-alles').addEventListener('click', () => zetFilter('alles'));
function zetFilter(f) {
  filter = f;
  document.getElementById('f-werk').classList.toggle('aan', f === 'werk');
  document.getElementById('f-alles').classList.toggle('aan', f === 'alles');
  teken();
}

laad();
/* De pc stuurt elk uur bij; vijf minuten pollen houdt de "geleden"-tekst eerlijk. */
setInterval(laad, 5 * 60 * 1000);
document.addEventListener('visibilitychange', () => { if (!document.hidden) laad(); });
</script>
</body>
</html>
HTML;
}
