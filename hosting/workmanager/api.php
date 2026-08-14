<?php
/**
 * WorkManager voice-API: brug tussen de Siri Shortcut (telefoon) en WorkManager (pc).
 *
 * De telefoon maakt een gesprekssessie aan en pollt op het antwoord; de pc pollt op
 * te verwerken sessies, laat Claude het commando parsen en zet het antwoord terug.
 *
 * Acties (parameter "actie", token via header X-Wm-Token of parameter "token"):
 *   POST commando   {tekst}                    -> {sessie}
 *   GET  ophalen    ?sessie=...&wacht=45       -> {fase, antwoord}   (fase: wacht_pc|beantwoord|klaar;
 *                                                 met "wacht" blijft het verzoek open tot de pc antwoordt)
 *   POST antwoord   {sessie, tekst}            -> {ok}               (vervolg/correctie van de gebruiker)
 *   GET  werk                                  -> {sessies: [{id, historie}]}          (voor de pc)
 *   POST resultaat  {sessie, antwoord, klaar}  -> {ok}               (van de pc)
 *   GET  ping                                  -> {ok, tijd}         (verbindingstest)
 *
 * De tabel wordt automatisch aangemaakt bij het eerste gebruik.
 */

declare(strict_types=1);

header('Content-Type: application/json; charset=utf-8');

$config = require __DIR__ . '/config.php';

function antwoord(array $data, int $status = 200): never
{
    http_response_code($status);
    echo json_encode($data, JSON_UNESCAPED_UNICODE);
    exit;
}

// ---- Token controleren (constant-time vergelijking) ----
$token = $_SERVER['HTTP_X_WM_TOKEN'] ?? $_REQUEST['token'] ?? '';
if (!is_string($token) || !hash_equals($config['token'], $token)) {
    antwoord(['fout' => 'ongeldig token'], 401);
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

$db->exec("CREATE TABLE IF NOT EXISTS wm_voice_sessies (
    id CHAR(36) NOT NULL PRIMARY KEY,
    fase ENUM('wacht_pc','beantwoord','klaar') NOT NULL DEFAULT 'wacht_pc',
    antwoord TEXT NULL,
    historie MEDIUMTEXT NOT NULL,
    aangemaakt DATETIME NOT NULL,
    bijgewerkt DATETIME NOT NULL,
    INDEX idx_fase (fase),
    INDEX idx_bijgewerkt (bijgewerkt)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");

// Oude sessies opruimen (goedkoop genoeg om bij elke aanroep te doen).
$db->prepare('DELETE FROM wm_voice_sessies WHERE bijgewerkt < (NOW() - INTERVAL :uren HOUR)')
    ->execute(['uren' => (int)$config['bewaar_uren']]);

// ---- Invoer ----
$actie = $_REQUEST['actie'] ?? '';
$body = [];
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $raw = file_get_contents('php://input');
    if (is_string($raw) && $raw !== '') {
        $decoded = json_decode($raw, true);
        if (is_array($decoded)) {
            $body = $decoded;
        }
    }
    // Ook gewone formuliervelden toestaan (handig voor Shortcuts).
    $body += $_POST;
}

function veld(array $body, string $naam): string
{
    $waarde = $body[$naam] ?? '';
    return is_string($waarde) ? trim($waarde) : '';
}

function laadSessie(PDO $db, string $id): ?array
{
    if ($id === '' || strlen($id) > 36) {
        return null;
    }
    $stmt = $db->prepare('SELECT * FROM wm_voice_sessies WHERE id = :id');
    $stmt->execute(['id' => $id]);
    $rij = $stmt->fetch();
    return $rij === false ? null : $rij;
}

switch ($actie) {
    case 'ping':
        antwoord(['ok' => true, 'tijd' => date('c')]);

    case 'commando': {
        $tekst = veld($body, 'tekst');
        if ($tekst === '') {
            antwoord(['fout' => 'tekst ontbreekt'], 400);
        }
        $id = sprintf('%s-%s', bin2hex(random_bytes(8)), bin2hex(random_bytes(4)));
        $historie = json_encode([['rol' => 'gebruiker', 'tekst' => $tekst]], JSON_UNESCAPED_UNICODE);
        $db->prepare("INSERT INTO wm_voice_sessies (id, fase, historie, aangemaakt, bijgewerkt)
                      VALUES (:id, 'wacht_pc', :historie, NOW(), NOW())")
            ->execute(['id' => $id, 'historie' => $historie]);
        antwoord(['sessie' => $id]);
    }

    case 'ophalen': {
        $id = veld($_REQUEST, 'sessie');
        $sessie = laadSessie($db, $id);
        if ($sessie === null) {
            antwoord(['fout' => 'sessie onbekend'], 404);
        }
        // Long-poll: met ?wacht=N (max 45 s) blijft het verzoek open tot de pc geantwoord
        // heeft, zodat de Shortcut geen eigen poll-lus met wachtstappen nodig heeft.
        // (sleep() telt op Linux niet mee voor max_execution_time.)
        $tot = time() + min(45, max(0, (int)($_REQUEST['wacht'] ?? 0)));
        while ($sessie['fase'] === 'wacht_pc' && time() < $tot) {
            sleep(2);
            $sessie = laadSessie($db, $id) ?? $sessie;
        }
        antwoord(['fase' => $sessie['fase'], 'antwoord' => $sessie['antwoord'] ?? '']);
    }

    case 'antwoord': {
        $sessie = laadSessie($db, veld($body, 'sessie'));
        $tekst = veld($body, 'tekst');
        if ($sessie === null) {
            antwoord(['fout' => 'sessie onbekend'], 404);
        }
        if ($tekst === '') {
            antwoord(['fout' => 'tekst ontbreekt'], 400);
        }
        $historie = json_decode($sessie['historie'], true) ?: [];
        $historie[] = ['rol' => 'gebruiker', 'tekst' => $tekst];
        $db->prepare("UPDATE wm_voice_sessies
                      SET fase = 'wacht_pc', historie = :historie, bijgewerkt = NOW()
                      WHERE id = :id")
            ->execute([
                'historie' => json_encode($historie, JSON_UNESCAPED_UNICODE),
                'id' => $sessie['id'],
            ]);
        antwoord(['ok' => true]);
    }

    case 'werk': {
        $stmt = $db->query("SELECT id, historie FROM wm_voice_sessies
                            WHERE fase = 'wacht_pc' ORDER BY bijgewerkt ASC LIMIT 10");
        $sessies = [];
        foreach ($stmt as $rij) {
            $sessies[] = ['id' => $rij['id'], 'historie' => json_decode($rij['historie'], true) ?: []];
        }
        antwoord(['sessies' => $sessies]);
    }

    case 'resultaat': {
        $sessie = laadSessie($db, veld($body, 'sessie'));
        $tekstAntwoord = veld($body, 'antwoord');
        $klaar = filter_var($body['klaar'] ?? false, FILTER_VALIDATE_BOOLEAN);
        if ($sessie === null) {
            antwoord(['fout' => 'sessie onbekend'], 404);
        }
        if ($tekstAntwoord === '') {
            antwoord(['fout' => 'antwoord ontbreekt'], 400);
        }
        $historie = json_decode($sessie['historie'], true) ?: [];
        $historie[] = ['rol' => 'assistent', 'tekst' => $tekstAntwoord];
        $db->prepare("UPDATE wm_voice_sessies
                      SET fase = :fase, antwoord = :antwoord, historie = :historie, bijgewerkt = NOW()
                      WHERE id = :id")
            ->execute([
                'fase' => $klaar ? 'klaar' : 'beantwoord',
                'antwoord' => $tekstAntwoord,
                'historie' => json_encode($historie, JSON_UNESCAPED_UNICODE),
                'id' => $sessie['id'],
            ]);
        antwoord(['ok' => true]);
    }

    default:
        antwoord(['fout' => 'onbekende actie'], 400);
}
