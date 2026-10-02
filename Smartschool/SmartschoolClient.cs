using System.IO.Compression;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Verborgen Smartschool-sessie (gibomariaburg.smartschool.be) van het ouderaccount:
/// leest per kind (via "Mijn kinderen" wisselt het actieve co-account) het Postvak IN
/// van de berichtenmodule, mét volledige tekst en bijlagenamen in een lokale cache
/// (smartschool-berichten.json). Berichten kunnen ook gearchiveerd worden — dan
/// verdwijnen ze uit het Postvak IN op Smartschool zelf én uit de cockpit.
/// De aanmelding verloopt volledig stil (<see cref="SmartschoolLogin"/>, geen MFA).
/// Let op: een bericht openen om de tekst te lezen markeert het op Smartschool als
/// gelezen — zelfde afweging als bij de CED-Outlookmails.
/// </summary>
public sealed class SmartschoolClient : IDisposable
{
    public static SmartschoolClient Instance { get; } = new();

    private const string Basis = "https://gibomariaburg.smartschool.be";

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string BerichtenFile =
        Path.Combine(DataDir, "smartschool-berichten.json");

    private Form? _venster;
    private WebView2? _web;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly SemaphoreSlim _initSlot = new(1, 1);
    private volatile bool _gecrasht;
    private DateTimeOffset _laatstOpgehaald = DateTimeOffset.MinValue;
    private List<SmartschoolBericht>? _cache;

    /// <summary>Eén bericht uit een Postvak IN van een kind, met inhoud uit de cache.</summary>
    public sealed record SmartschoolBericht(
        string Sleutel, string Kind, string MsgId, string Van, string Onderwerp,
        string Tekst, string Html, DateTimeOffset Datum, string Bijlagen = "",
        int Pogingen = 0)
    {
        /// <summary>Profielfoto van de afzender (data-URL), leeg = geen.</summary>
        public string Avatar { get; init; } = "";
        /// <summary>Ontvangersregel zoals Smartschool die toont ("Maarten Bergmans, vader van …").</summary>
        public string Ontvangers { get; init; } = "";
        /// <summary>Aantal overige ontvangers ("Toon overige (681)").</summary>
        public int Overige { get; init; }
    }

    /// <summary>Laat de volgende beurt écht bij Smartschool kijken (bv. na een meldingsmail).</summary>
    public void ForceerVerversing() => _laatstOpgehaald = DateTimeOffset.MinValue;

    /// <summary>Nachtelijk onderhoud: sessie bij de volgende beurt vers opbouwen.</summary>
    public void MarkeerVoorVerseStart() => _gecrasht = true;

    /// <summary>
    /// Alle berichten uit de cache; alleen als <paramref name="magVerversen"/> waar is
    /// (meldingsmail gezien of het uur is om) wordt er echt bij Smartschool gekeken.
    /// De webview blijft zo doorgaans koud — school berichten wijzigen zelden.
    /// </summary>
    public async Task<List<SmartschoolBericht>> BerichtenAsync(
        bool magVerversen, CancellationToken ct)
    {
        // Per beurt vers: anders bleef de "dubbels gearchiveerd"-melding van een eerdere
        // beurt bij elke cache-ronde opnieuw in de cockpit opduiken.
        LaatsteAutoGearchiveerd = Array.Empty<string>();
        var cache = LaadCache();
        var uurOm = DateTimeOffset.Now - _laatstOpgehaald > TimeSpan.FromMinutes(60);
        if (!magVerversen && !uurOm)
        {
            return cache;
        }
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Smartschool-aanmelding mislukt — controleer smartschool-login.json.");
            }
            var vers = new List<SmartschoolBericht>();
            var meldingen = new List<SmartschoolMelding>();
            var kinderen = await KinderenAsync(ct);
            foreach (var (kindNaam, accountId) in kinderen)
            {
                ct.ThrowIfCancellationRequested();
                await NavigeerAsync($"{Basis}/Studentcard/Chain/gotourl/accountID/{accountId}", ct);
                var postvak = await LeesPostvakAsync(kindNaam, cache, ct);
                vers.AddRange(postvak);
                try
                {
                    // Belletje: meldingen van al afgehandelde berichten wissen, de rest
                    // (agenda, resultaten, …) voor de cockpit meenemen.
                    meldingen.AddRange(await VerwerkMeldingenAsync(kindNaam,
                        postvak.Select(b => b.MsgId).ToHashSet(), ct));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _debugStappen.Add($"meldingen {kindNaam}: {ex.Message}");
                }
            }
            await ArchiveerDubbeleAsync(vers, kinderen, ct);
            BewaarMeldingen(meldingen);
            try
            {
                // Diagnose: als de lijst leeg blijft wil je zien of het aan de kinderen
                // (wissellinks) of aan de berichtenlijst lag.
                File.WriteAllText(Path.Combine(DataDir, "smartschool-debug.json"),
                    JsonSerializer.Serialize(new
                    {
                        moment = DateTimeOffset.Now,
                        kinderen = kinderen.Select(k => k.Naam).ToList(),
                        berichten = vers.Count,
                        metTekst = vers.Count(b => b.Tekst.Length > 0),
                        autoGearchiveerd = LaatsteAutoGearchiveerd,
                        stappen = _debugStappen,
                    }));
                _debugStappen.Clear();
            }
            catch
            {
                // Alleen diagnose.
            }
            _laatstOpgehaald = DateTimeOffset.Now;
            BewaarCache(vers);
            return vers;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Bij welk kind een dubbel schoolbreed bericht automatisch weg mag.</summary>
    private const string DubbelArchiefKind = "Emilia";

    /// <summary>Onderwerpen die de laatste pollronde automatisch als dubbel archiveerde.</summary>
    public IReadOnlyList<string> LaatsteAutoGearchiveerd { get; private set; } =
        Array.Empty<string>();

    /// <summary>
    /// Schoolbrede berichten komen dubbel binnen: één per kind. De kopie bij
    /// <see cref="DubbelArchiefKind"/> wordt automatisch gearchiveerd — op Smartschool
    /// én uit de lijst — zodat alleen het exemplaar van het andere kind overblijft.
    /// Alleen bij écht identieke inhoud (zelfde afzender, onderwerp én tekst na
    /// witruimte-normalisatie): berichten met kindspecifieke inhoud, zoals de
    /// klasindeling in "Overgang volgend schooljaar", blijven allebei staan.
    /// Aanroeper houdt het slot vast.
    /// </summary>
    private async Task ArchiveerDubbeleAsync(
        List<SmartschoolBericht> vers,
        List<(string Naam, string AccountId)> kinderen, CancellationToken ct)
    {
        var gearchiveerd = new List<string>();
        try
        {
            var accountId = kinderen.FirstOrDefault(k =>
                k.Naam.Contains(DubbelArchiefKind, StringComparison.OrdinalIgnoreCase))
                .AccountId ?? "";
            if (accountId.Length == 0)
            {
                return;
            }
            // Alleen de berichtinhoud vergelijken, niet de kop: die bevat per kind de
            // adresregel ("… vader van Emilia Bergmans - 2KA") en zou identieke berichten
            // altijd verschillend maken. De kop eindigt op het verzendmoment
            // (yyyy-MM-dd HH:mm) — alles daarná is de echte tekst.
            static string Kern(string s)
            {
                var n = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
                var m = System.Text.RegularExpressions.Regex.Match(
                    n, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}");
                return m.Success ? n[(m.Index + m.Length)..].Trim() : n;
            }
            var dubbel = vers.Where(b =>
                b.Kind.Contains(DubbelArchiefKind, StringComparison.OrdinalIgnoreCase) &&
                b.Tekst.Length > 0 &&
                vers.Any(o =>
                    !o.Kind.Contains(DubbelArchiefKind, StringComparison.OrdinalIgnoreCase) &&
                    o.Van == b.Van && o.Onderwerp == b.Onderwerp &&
                    Kern(o.Tekst) == Kern(b.Tekst))).ToList();
            foreach (var b in dubbel)
            {
                ct.ThrowIfCancellationRequested();
                if (await ArchiveerKernAsync(accountId, b.Kind, b.MsgId, ct))
                {
                    vers.Remove(b);
                    gearchiveerd.Add(b.Onderwerp);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best effort: een mislukte opruimbeurt mag de berichtenlijst nooit breken;
            // de volgende ronde probeert het gewoon opnieuw.
            _debugStappen.Add($"dubbel-archivering brak af: {ex.Message}");
        }
        finally
        {
            LaatsteAutoGearchiveerd = gearchiveerd;
        }
    }

    /// <summary>
    /// Archiveert één bericht (verplaatst het op Smartschool naar "Berichten archief").
    /// Resultaat: true als de rij daarna echt uit het Postvak IN verdwenen is.
    /// </summary>
    public async Task<bool> ArchiveerAsync(string kind, string msgId, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                return false;
            }
            var kinderen = await KinderenAsync(ct);
            var accountId = kinderen.FirstOrDefault(k =>
                k.Naam.Equals(kind, StringComparison.OrdinalIgnoreCase)).AccountId ?? "";
            if (accountId.Length == 0)
            {
                return false;
            }
            if (msgId.StartsWith("melding:", StringComparison.Ordinal))
            {
                // Geen bericht maar een melding uit het belletje: die wissen.
                await NavigeerAsync($"{Basis}/Studentcard/Chain/gotourl/accountID/{accountId}", ct);
                await NavigeerAsync($"{Basis}/index.php?module=Messages", ct);
                var sleutel = msgId["melding:".Length..];
                var gewist = await WisMeldingAsync(sleutel, ct);
                var lijst = Meldingen();
                if (lijst.RemoveAll(m => m.Kind == kind && m.Sleutel == sleutel) > 0)
                {
                    BewaarMeldingen(lijst);
                }
                return gewist || true; // uit de cockpit is hij sowieso weg; Smartschool volgt bij de volgende ronde
            }
            return await ArchiveerKernAsync(accountId, kind, msgId, ct);
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Archiveert één bericht in het postvak van het opgegeven kind. Aanroeper houdt
    /// zelf het slot vast (ook de dubbel-detectie in de pollronde gebruikt dit).
    /// </summary>
    private async Task<bool> ArchiveerKernAsync(
        string accountId, string kind, string msgId, CancellationToken ct)
    {
        await NavigeerAsync($"{Basis}/Studentcard/Chain/gotourl/accountID/{accountId}", ct);
        await NavigeerAsync($"{Basis}/index.php?module=Messages", ct);
        await WachtOpAsync("#msglist .modern-message", ct);
        var msgIdJs = JsonSerializer.Serialize("row_" + msgId);
        // Rij selecteren en archiveren. Niet via oTriggers.archiveMessages: die leunt
        // intern op een jQuery-":hover"-selector en gooit bij een synthetische aanroep
        // "unsupported pseudo: hover". In plaats daarvan de archiveerknop van de rij
        // zelf (hover-actie) aanklikken, met het contextmenu als terugval; een
        // eventuele bevestigingsdialoog wordt in de volgende rondes bevestigd.
        var gestart = await JsAsync(
            $$"""
            (function () {
                const rij = document.getElementById({{msgIdJs}});
                if (!rij) return 'rij-niet-gevonden (' +
                    document.querySelectorAll('#msglist .modern-message').length + ' rijen)';
                rij.click();
                const zoekKnop = wortel => [...wortel.querySelectorAll(
                    '[title], [aria-label], button, .modern-message__action')]
                    .find(k => /archiv/i.test((k.getAttribute('title') || '') + ' ' +
                        (k.getAttribute('aria-label') || '') + ' ' + (k.className || '')));
                let knop = zoekKnop(rij);
                if (!knop) {
                    // Geen knop op de rij: het contextmenu openen en daar zoeken (op
                    // tekst, want menu-items hebben geen title).
                    rij.dispatchEvent(new MouseEvent('contextmenu',
                        { bubbles: true, cancelable: true, view: window }));
                    knop = [...document.querySelectorAll(
                        '[class*="context"] li, [class*="context"] a, [role="menu"] *, .menu li')]
                        .find(k => k.offsetParent !== null &&
                            /archiv/i.test(k.textContent || '') &&
                            (k.textContent || '').trim().length < 40);
                }
                if (knop) { knop.click(); return 'ok'; }
                return 'geen-archiefknop; rij-titles: ' +
                    [...rij.querySelectorAll('[title]')]
                        .map(e => e.getAttribute('title')).join(',').slice(0, 120);
            })()
            """);
        if (!gestart.Contains("ok"))
        {
            _debugStappen.Add($"archief {kind}/{msgId}: {gestart}");
            return false;
        }
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(600, ct);
            // Bevestigingsknop in een zichtbare dialoog aanklikken (NL-platform).
            await JsAsync(
                """
                (function () {
                    const knop = [...document.querySelectorAll(
                        'button, input[type=button], input[type=submit], a.button')]
                        .find(b => b.offsetParent !== null &&
                            /^(ja|ok|archiveer|archiveren|bevestig)/i.test(
                                ((b.value || '') + ' ' + (b.textContent || '')).trim()));
                    if (knop) knop.click();
                    return true;
                })()
                """);
            if (await JsAsync("!!document.getElementById(" + msgIdJs + ")") == "false")
            {
                // Rij weg uit de lijst: ook uit de cache halen, en de melding in het
                // belletje wissen (anders blijft de teller in Smartschool staan).
                var cache = LaadCache();
                if (cache.RemoveAll(b => b.Kind == kind && b.MsgId == msgId) > 0)
                {
                    BewaarCache(cache);
                }
                try
                {
                    await WisMeldingAsync($"/?module=Messages&msgID={msgId}", ct);
                }
                catch
                {
                    // Best effort; de volgende ophaalbeurt wist hem alsnog.
                }
                return true;
            }
        }
        _debugStappen.Add($"archief {kind}/{msgId}: gestart maar rij bleef staan " +
            "(bevestigingsdialoog niet herkend?)");
        return false;
    }

    /// <summary>De map waarin de bijlagen van één bericht lokaal bewaard worden.</summary>
    private static string BijlagenMap(string msgId) =>
        Path.Combine(DataDir, "smartschool-bijlagen", msgId);

    /// <summary>
    /// De eerder (proactief) gedownloade bijlagen van een bericht — leeg als er (nog)
    /// niets lokaal staat. Puur een schijfcheck, geen webview nodig: de cockpit opent
    /// hiermee een bijlage-chip meteen, zonder wachten.
    /// </summary>
    public static List<string> LokaleBijlagen(string msgId)
    {
        try
        {
            var map = BijlagenMap(msgId);
            return Directory.Exists(map)
                ? Directory.GetFiles(map).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Downloadt de bijlagen van één bericht naar de lokale bijlagenmap en retourneert de
    /// bestandspaden; staat er al iets lokaal, dan komt dat meteen terug. De bijlagen in
    /// de berichtweergave zijn geen links: het bericht wordt echt in de ingelogde sessie
    /// geopend en de downloadknoppen worden aangeklikt.
    /// </summary>
    public async Task<List<string>> DownloadBijlagenAsync(
        string kind, string msgId, CancellationToken ct)
    {
        if (LokaleBijlagen(msgId) is { Count: > 0 } lokaal)
        {
            return lokaal;
        }
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Smartschool-aanmelding mislukt — controleer smartschool-login.json.");
            }
            var kinderen = await KinderenAsync(ct);
            var accountId = kinderen.FirstOrDefault(k =>
                k.Naam.Equals(kind, StringComparison.OrdinalIgnoreCase)).AccountId ?? "";
            if (accountId.Length == 0)
            {
                throw new InvalidOperationException($"Kind \"{kind}\" niet gevonden op Smartschool.");
            }
            await NavigeerAsync($"{Basis}/Studentcard/Chain/gotourl/accountID/{accountId}", ct);
            await NavigeerAsync($"{Basis}/index.php?module=Messages", ct);
            await WachtOpAsync("#msglist .modern-message", ct);
            if (!await OpenBerichtRijAsync(msgId, ct))
            {
                _debugStappen.Add($"bijlagen {kind}/{msgId} rij niet gevonden: " + Ontdubbel(
                    await JsAsync(
                        """
                        JSON.stringify({
                            url: location.pathname + location.search,
                            rijen: [...document.querySelectorAll('#msglist .modern-message')]
                                .map(r => r.id).slice(0, 20),
                            wie: (document.querySelector('.topnav')?.innerText || '')
                                .replace(/\s+/g, ' ').slice(0, 80),
                        })
                        """)));
                throw new InvalidOperationException(
                    "Bericht niet gevonden in het Postvak IN (al gearchiveerd?).");
            }
            return await DownloadGeopendeBijlagenAsync(msgId, ct);
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Klikt de rij van een bericht aan en wacht tot de bijlagenlijst er staat (die laadt
    /// asynchroon ná de berichttekst). Aanroeper houdt het slot vast en staat al in het
    /// Postvak IN van het juiste kind. False als de rij niet (meer) bestaat.
    /// </summary>
    private async Task<bool> OpenBerichtRijAsync(string msgId, CancellationToken ct)
    {
        var idJs = JsonSerializer.Serialize("row_" + msgId);
        if (await JsAsync(
            $$"""
            (function () {
                const rij = document.getElementById({{idJs}});
                if (!rij) return 'weg';
                rij.click();
                return 'ok';
            })()
            """) is not "\"ok\"")
        {
            return false;
        }
        for (var i = 0; i < 16 && await JsAsync(
            "!!document.querySelector('#msgdetail .attachment')") != "true"; i++)
        {
            await Task.Delay(500, ct);
        }
        return true;
    }

    /// <summary>
    /// Downloadt de bijlagen van het nu geopende bericht naar de lokale bijlagenmap.
    /// Aanroeper houdt het slot vast; de downloads worden met DownloadStarting
    /// opgevangen en een eventuele ZIP wordt meteen uitgepakt tot losse bestanden.
    /// </summary>
    private async Task<List<string>> DownloadGeopendeBijlagenAsync(
        string msgId, CancellationToken ct)
    {
        var doelMap = BijlagenMap(msgId);
        Directory.CreateDirectory(doelMap);
        var core = _web!.CoreWebView2!;
        var downloads = new List<(string Pad, TaskCompletionSource<bool> Klaar)>();
        void OpDownload(object? _, CoreWebView2DownloadStartingEventArgs e)
        {
            var naam = string.Concat(Path.GetFileName(e.ResultFilePath)
                .Split(Path.GetInvalidFileNameChars())).Trim();
            var pad = UniekPad(doelMap, naam.Length > 0 ? naam : "bijlage");
            e.ResultFilePath = pad;
            e.Handled = true; // geen downloadbalk in het verborgen venster
            var klaar = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operatie = e.DownloadOperation;
            operatie.StateChanged += (_, _) =>
            {
                switch (operatie.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        klaar.TrySetResult(true);
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        klaar.TrySetResult(false);
                        break;
                }
            };
            downloads.Add((pad, klaar));
        }
        core.DownloadStarting += OpDownload;
        try
        {
            // De downloadknop per bijlage ("Download dit bestand") — één voor één,
            // want twee kliks vlak na elkaar onderbreken elkaars download. De
            // ZIP-knop reageert niet op synthetische kliks en is dus geen optie.
            const string KnopSelector =
                "#msgdetail .attachment .attachment__action.download";
            var aantalJson = await JsAsync(
                $"document.querySelectorAll('{KnopSelector}').length");
            var gestart = "los";
            if (!int.TryParse(aantalJson, out var aantal) || aantal == 0)
            {
                // Geen losse knoppen: de ZIP-knop als terugval tóch proberen.
                gestart = Ontdubbel(await JsAsync(
                    """
                    (function () {
                        const d = document.querySelector('#msgdetail');
                        if (!d) return 'geen-detail';
                        const zip = [...d.querySelectorAll('a, button')].find(e =>
                            /download/i.test((e.textContent || '') + (e.getAttribute('title') || '')) &&
                            /zip/i.test((e.textContent || '') + (e.getAttribute('title') || '')));
                        if (zip) { zip.click(); return 'zip'; }
                        return 'geen-downloadknop';
                    })()
                    """));
                if (gestart != "zip")
                {
                    throw new InvalidOperationException(
                        $"Geen downloadknop gevonden in het bericht ({gestart}).");
                }
                aantal = 1;
            }
            else
            {
                for (var i = 0; i < aantal; i++)
                {
                    await JsAsync(
                        $"document.querySelectorAll('{KnopSelector}')[{i}]?.click()");
                    for (var w = 0; w < 20 && downloads.Count <= i; w++)
                    {
                        await Task.Delay(500, ct);
                    }
                    _debugStappen.Add($"bijlagen {msgId}: knop {i + 1}/{aantal} " +
                        $"geklikt, {downloads.Count} download(s) gestart");
                }
            }
            for (var i = 0; i < 20 && downloads.Count < aantal; i++)
            {
                await Task.Delay(500, ct);
            }
            if (downloads.Count == 0)
            {
                _debugStappen.Add("bijlagen: geen DownloadStarting na klik; url = " +
                    Ontdubbel(await JsAsync("location.href")));
                throw new InvalidOperationException("De download kwam niet op gang.");
            }
            var wachter = Task.WhenAll(downloads.Select(d => d.Klaar.Task));
            if (await Task.WhenAny(wachter, Task.Delay(120_000, ct)) != wachter)
            {
                throw new InvalidOperationException("De download duurde te lang (> 2 min).");
            }
            var paden = downloads.Where(d => d.Klaar.Task.Result).Select(d => d.Pad).ToList();
            if (gestart == "zip" && paden is [var zipPad] &&
                zipPad.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var uitgepakt = new List<string>();
                using (var zip = ZipFile.OpenRead(zipPad))
                {
                    foreach (var entry in zip.Entries.Where(e => e.Name.Length > 0))
                    {
                        var pad = UniekPad(doelMap, string.Concat(
                            entry.Name.Split(Path.GetInvalidFileNameChars())).Trim());
                        entry.ExtractToFile(pad);
                        uitgepakt.Add(pad);
                    }
                }
                File.Delete(zipPad);
                return uitgepakt;
            }
            return paden;
        }
        finally
        {
            core.DownloadStarting -= OpDownload;
        }
    }

    /// <summary>Zelfde volgnummer-truc als bij de Gmail-bijlagen: nooit overschrijven.</summary>
    private static string UniekPad(string map, string naam)
    {
        var pad = Path.Combine(map, naam);
        var basis = Path.GetFileNameWithoutExtension(naam);
        var extensie = Path.GetExtension(naam);
        for (var i = 2; File.Exists(pad); i++)
        {
            pad = Path.Combine(map, $"{basis} ({i}){extensie}");
        }
        return pad;
    }

    /// <summary>
    /// Meldingen (het belletje rechtsboven) van het actieve co-account: korte regels
    /// "titel — info". Best effort; leeg als het paneel er niet staat.
    /// </summary>
    public async Task<List<string>> MeldingenAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                return new List<string>();
            }
            var json = await JsAsync(
                """
                JSON.stringify([...document.querySelectorAll('.js-notifs-list .notification')]
                    .slice(0, 15).map(n => ({
                        titel: (n.querySelector('.notification__title')?.textContent || '')
                            .replace(/\s+/g, ' ').trim().slice(0, 120),
                        info: [...n.querySelectorAll('.notification__info')]
                            .map(i => (i.textContent || '').replace(/\s+/g, ' ').trim())
                            .filter(Boolean).join(' · ').slice(0, 160),
                    })))
                """);
            using var doc = JsonDocument.Parse(Ontdubbel(json));
            return doc.RootElement.EnumerateArray()
                .Select(e => $"{e.GetProperty("titel").GetString()} — {e.GetProperty("info").GetString()}")
                .Where(t => t.Length > 3)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Diagnose (CLI --smsdiag): naar een pagina navigeren (pad relatief aan de school-URL,
    /// of "-" = blijven staan), een script draaien (mag een Promise opleveren) en optioneel
    /// een screenshot maken van hoe Smartschool het zelf toont.
    /// </summary>
    public async Task<string> DiagnoseInAsync(string pad, string script, string screenshotPad,
        CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 45))
            {
                return "(niet aangemeld)";
            }
            if (pad.Length > 0 && pad != "-")
            {
                await NavigeerAsync(pad.StartsWith("http") ? pad : Basis + "/" + pad.TrimStart('/'), ct);
                await Task.Delay(1500, ct);
            }
            var uit = "";
            if (script.Length > 0)
            {
                await JsAsync(
                    "window.__wmDiag = null; Promise.resolve(eval(" + JsonSerializer.Serialize(script) +
                    ")).then(v => { window.__wmDiag = String(v); }, e => { window.__wmDiag = 'FOUT: ' + e; }); true");
                for (var i = 0; i < 100; i++)
                {
                    await Task.Delay(300, ct);
                    var klaar = await JsAsync("window.__wmDiag");
                    if (klaar != "null")
                    {
                        uit = Ontdubbel(klaar);
                        break;
                    }
                }
            }
            if (screenshotPad.Length > 0)
            {
                await Task.Delay(500, ct);
                using var beeld = new MemoryStream();
                await _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld);
                File.WriteAllBytes(screenshotPad, beeld.ToArray());
            }
            return uit;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Diagnose (CLI --smslees): opent één bericht via zijn msgID (ook uit het archief) en
    /// leest het met de echte leescode; geeft het resultaat terug als bericht-record.
    /// </summary>
    public async Task<SmartschoolBericht?> DiagnoseLeesAsync(string msgId, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 45))
            {
                return null;
            }
            await NavigeerAsync($"{Basis}/?module=Messages&msgID={msgId}", ct);
            await WachtOpAsync("#msglist .modern-message", ct);
            await Task.Delay(800, ct);
            var rij = Ontdubbel(await JsAsync(
                $$"""
                (function () {
                    const r = document.getElementById({{JsonSerializer.Serialize("row_" + msgId)}});
                    if (!r) return '';
                    return JSON.stringify({
                        van: (r.querySelector('.modern-message__name')?.textContent || '').replace(/\s+/g, ' ').trim(),
                        onderwerp: (r.querySelector('.modern-message__subject')?.textContent || '').replace(/\s+/g, ' ').trim(),
                        datum: (r.querySelector('.modern-message__date')?.textContent || '').trim(),
                    });
                })()
                """));
            var van = "";
            var onderwerp = "";
            var datum = DateTimeOffset.Now;
            if (rij.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(rij);
                van = doc.RootElement.GetProperty("van").GetString() ?? "";
                onderwerp = doc.RootElement.GetProperty("onderwerp").GetString() ?? "";
                datum = ParseMoment(doc.RootElement.GetProperty("datum").GetString() ?? "");
            }
            var g = await LeesBerichtAsync(msgId, ct);
            return new SmartschoolBericht($"smartschool:diag:{msgId}", "diag", msgId, van, onderwerp,
                g.Tekst, g.Html, datum, g.Bijlagen)
            {
                Avatar = g.Avatar, Ontvangers = g.Ontvangers, Overige = g.Overige,
            };
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Eén stuk JavaScript in de sessie draaien (CLI-schakelaar --smsjs).</summary>
    public async Task<string> DiagnoseJsAsync(string script, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            var gestart = await StartAsync(ct);
            return (gestart ? "" : "(niet aangemeld) ") + await JsAsync(script);
        }
        finally
        {
            _slot.Release();
        }
    }

    // ---------- Sessie ----------

    private async Task<bool> StartAsync(CancellationToken ct, int wachtSeconden = 30)
    {
        await _initSlot.WaitAsync(ct);
        try
        {
            if (_gecrasht)
            {
                try { _web?.Dispose(); } catch { /* al kapot */ }
                try { _venster?.Dispose(); } catch { /* al kapot */ }
                _web = null;
                _venster = null;
                _gecrasht = false;
            }
            if (_web?.CoreWebView2 is null)
            {
                _venster = new StilVenster
                {
                    Text = "Smartschool (GIBO Mariaburg)",
                    Size = new Size(1280, 1400),
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-4000, -4000),
                    ShowInTaskbar = false,
                };
                _venster.FormClosing += (_, e) =>
                {
                    e.Cancel = true; // sessie blijft op de achtergrond leven
                    _venster!.Location = new Point(-4000, -4000);
                };
                _web = new WebView2 { Dock = DockStyle.Fill };
                _venster.Controls.Add(_web);
                _venster.Show();
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(DataDir, "webview2-smartschool"),
                    new CoreWebView2EnvironmentOptions(
                        "--disable-background-timer-throttling " +
                        "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding"));
                _web = await WebViewOpruimer.InitMetHerstelAsync(_venster, _web, env,
                    Path.Combine(DataDir, "webview2-smartschool"), "Smartschool", ct);
                var webNu = _web;
                _web.CoreWebView2!.ProcessFailed += (_, _) => _gecrasht = true;
                _web.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    webNu.CoreWebView2!.Navigate(e.Uri);
                };
                // Meerdere bijlagen na elkaar downloaden telt als "automatic downloads";
                // Chromium blokkeert de tweede stilletjes zonder deze permissie.
                _web.CoreWebView2.PermissionRequested += (_, e) =>
                {
                    if (e.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads)
                    {
                        e.State = CoreWebView2PermissionState.Allow;
                    }
                };
                _web.CoreWebView2.Navigate(Basis + "/");
            }
        }
        finally
        {
            _initSlot.Release();
        }

        return await ZorgIngelogdAsync(ct, wachtSeconden);
    }

    /// <summary>
    /// Wacht tot de sessie ingelogd is en vult onderweg zelf de aanmeld- en
    /// verificatieschermen in. Ook nodig ná het starten: Smartschool gooit de sessie
    /// soms halverwege terug naar /login (bv. na de kindwissel via gotourl), en dan
    /// moet elke navigatie opnieuw kunnen aanmelden — niet alleen StartAsync.
    /// </summary>
    private async Task<bool> ZorgIngelogdAsync(CancellationToken ct, int rondes = 15)
    {
        var gebPogingen = 0;
        for (var i = 0; i < rondes; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsIngelogdAsync())
            {
                return true;
            }
            // Verificatievraag: "wat is de geboortedatum van het kind waarvoor je wilt
            // aanmelden?" — die duikt niet alleen bij de eerste aanmelding op, maar ook
            // bij de kindwissel, en dan hoort er de datum van dát kind. Welk kind actief
            // is weten we hier niet, dus alle bewaarde data één voor één proberen
            // (pogingsteller als index); na de lijst stoppen we — nooit blijven hameren.
            var geboortedata = SmartschoolLogin.Load().Geboortedata;
            if (gebPogingen < geboortedata.Count && await JsAsync(
                $$"""
                (function () {
                    const geb = document.querySelector(
                        'input[name*="security_question" i], input[type=date]');
                    if (!geb || geb.offsetParent === null) return 'geen';
                    if (window.__wmSsGeb) return 'wacht';
                    window.__wmSsGeb = true;
                    geb.value = {{JsonSerializer.Serialize(geboortedata[gebPogingen])}};
                    geb.dispatchEvent(new Event('input', { bubbles: true }));
                    geb.dispatchEvent(new Event('change', { bubbles: true }));
                    (document.querySelector('button[type=submit], input[type=submit]'))?.click();
                    return 'ingevuld';
                })()
                """) == "\"ingevuld\"")
            {
                gebPogingen++;
                await Task.Delay(1500, ct);
                continue;
            }
            // Loginpagina: gebruikersnaam + wachtwoord invullen en insturen. Nooit vaker
            // dan één keer per paginalaad (guard in de pagina) — een fout wachtwoord mag
            // niet tot een reeks mislukte pogingen leiden.
            await JsAsync(
                $$"""
                (function () {
                    if (window.__wmSsLogin) return 'al-geprobeerd';
                    const gebruiker = document.querySelector(
                        'input[name*="username" i], input[name*="login" i]:not([type=hidden]), ' +
                        'input[type=text][id*="user" i]');
                    const wachtwoord = document.querySelector('input[type=password]');
                    if (!gebruiker || !wachtwoord ||
                        gebruiker.offsetParent === null) return 'geen-loginform';
                    window.__wmSsLogin = true;
                    const vuur = el => {
                        el.dispatchEvent(new Event('input', { bubbles: true }));
                        el.dispatchEvent(new Event('change', { bubbles: true }));
                    };
                    gebruiker.value = {{JsonSerializer.Serialize(SmartschoolLogin.Gebruikersnaam())}};
                    vuur(gebruiker);
                    wachtwoord.value = {{JsonSerializer.Serialize(SmartschoolLogin.Wachtwoord())}};
                    vuur(wachtwoord);
                    const knop = document.querySelector(
                        'button[type=submit], input[type=submit]') ||
                        [...document.querySelectorAll('button')].find(b =>
                            /aanmelden|log ?in/i.test(b.textContent || ''));
                    if (knop) { knop.click(); } else { wachtwoord.form?.submit(); }
                    return 'ingestuurd';
                })()
                """);
            await Task.Delay(1000, ct);
        }
        return false;
    }

    private async Task<bool> IsIngelogdAsync()
    {
        try
        {
            return await JsAsync(
                """
                (location.hostname.endsWith('smartschool.be') &&
                 !!document.querySelector('.topnav') &&
                 !document.querySelector('input[type=password]'))
                """) == "true";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>De kinderen (naam + accountID) uit de "Mijn kinderen"-pagina.</summary>
    private async Task<List<(string Naam, string AccountId)>> KinderenAsync(CancellationToken ct)
    {
        await NavigeerAsync($"{Basis}/Studentcard", ct);
        // Op de wissellinks zelf wachten — de topnav staat er al vóór de kaart rendert.
        await WachtOpAsync("a[href*=\"/Studentcard/Chain/gotourl/accountID/\"]", ct);
        var json = await JsAsync(
            """
            JSON.stringify([...document.querySelectorAll('a')]
                .filter(a => /\/Studentcard\/Chain\/gotourl\/accountID\/\d+/.test(a.href))
                .map(a => ({
                    naam: (a.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40),
                    id: (a.href.match(/accountID\/(\d+)/) || [])[1] || '',
                }))
                .filter(k => k.naam && k.id))
            """);
        var kinderen = new List<(string, string)>();
        using (var doc = JsonDocument.Parse(Ontdubbel(json)))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var naam = e.GetProperty("naam").GetString() ?? "";
                var id = e.GetProperty("id").GetString() ?? "";
                if (naam.Length > 0 && id.Length > 0 && kinderen.All(k => k.Item2 != id))
                {
                    kinderen.Add((naam, id));
                }
            }
        }
        if (kinderen.Count == 1)
        {
            // De pagina toont soms alleen de wissellink naar het ándere kind (het actieve
            // kind heeft geen link). Eén keer wisselen en opnieuw kijken levert dan de
            // volledige lijst — inclusief het kind waar we net vandaan kwamen.
            await NavigeerAsync(
                $"{Basis}/Studentcard/Chain/gotourl/accountID/{kinderen[0].Item2}", ct);
            await NavigeerAsync($"{Basis}/Studentcard", ct);
            await WachtOpAsync(".topnav", ct);
            var json2 = await JsAsync(
                """
                JSON.stringify([...document.querySelectorAll('a')]
                    .filter(a => /\/Studentcard\/Chain\/gotourl\/accountID\/\d+/.test(a.href))
                    .map(a => ({
                        naam: (a.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40),
                        id: (a.href.match(/accountID\/(\d+)/) || [])[1] || '',
                    }))
                    .filter(k => k.naam && k.id))
                """);
            using var doc2 = JsonDocument.Parse(Ontdubbel(json2));
            foreach (var e in doc2.RootElement.EnumerateArray())
            {
                var naam = e.GetProperty("naam").GetString() ?? "";
                var id = e.GetProperty("id").GetString() ?? "";
                if (naam.Length > 0 && id.Length > 0 && kinderen.All(k => k.Item2 != id))
                {
                    kinderen.Add((naam, id));
                }
            }
        }
        return kinderen;
    }

    /// <summary>
    /// Leest het Postvak IN van het nu actieve kind: de lijst scrapen, en elk bericht dat
    /// nog niet (volledig) in de cache zit openen voor de volledige tekst en bijlagen.
    /// </summary>
    /// <summary>Momentopnames per stap voor smartschool-debug.json.</summary>
    private readonly List<string> _debugStappen = new();

    /// <summary>Dezelfde stappen, uitleesbaar voor de --smsarchief-diagnose.</summary>
    public IReadOnlyList<string> DebugStappen => _debugStappen;

    private async Task<List<SmartschoolBericht>> LeesPostvakAsync(
        string kind, List<SmartschoolBericht> cache, CancellationToken ct)
    {
        await NavigeerAsync($"{Basis}/index.php?module=Messages", ct);
        // Op échte rijen wachten, niet enkel op de lege lijstcontainer; een leeg Postvak IN
        // levert nooit rijen en valt na de time-out gewoon door naar een lege lijst.
        await WachtOpAsync("#msglist .modern-message", ct);
        await Task.Delay(800, ct); // lijst laten renderen
        try
        {
            _debugStappen.Add($"{kind}: " + Ontdubbel(await JsAsync(
                """
                JSON.stringify({
                    url: location.pathname + location.search,
                    titel: document.title.slice(0, 40),
                    lijst: !!document.getElementById('msglist'),
                    rijen: document.querySelectorAll('#msglist .modern-message').length,
                    losseRijen: document.querySelectorAll('.modern-message').length,
                    tekst: (document.body?.innerText || '').replace(/\s+/g, ' ').slice(0, 120),
                })
                """)));
        }
        catch
        {
            // Alleen diagnose.
        }
        var json = await JsAsync(
            """
            JSON.stringify([...document.querySelectorAll('#msglist .modern-message')]
                .map(r => ({
                    id: (r.id || '').replace(/^row_/, ''),
                    van: (r.querySelector('.modern-message__name')?.textContent || '')
                        .replace(/\s+/g, ' ').trim().slice(0, 60),
                    onderwerp: (r.querySelector('.modern-message__subject')?.textContent || '')
                        .replace(/\s+/g, ' ').trim().slice(0, 150),
                    datum: (r.querySelector('.modern-message__date')?.textContent || '').trim(),
                })).filter(r => r.id))
            """);
        var resultaat = new List<SmartschoolBericht>();
        using var doc = JsonDocument.Parse(Ontdubbel(json));
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var msgId = e.GetProperty("id").GetString() ?? "";
            var van = e.GetProperty("van").GetString() ?? "";
            var onderwerp = e.GetProperty("onderwerp").GetString() ?? "";
            var datum = ParseMoment(e.GetProperty("datum").GetString() ?? "");
            var sleutel = $"smartschool:{kind}:{msgId}";
            var bekend = cache.FirstOrDefault(b => b.Sleutel == sleutel);
            if (bekend is null || (bekend.Tekst.Length == 0 && bekend.Pogingen < 3))
            {
                var g = await LeesBerichtAsync(msgId, ct);
                bekend = new SmartschoolBericht(sleutel, kind, msgId, van, onderwerp,
                    g.Tekst, g.Html, datum, g.Bijlagen,
                    Pogingen: g.Tekst.Length > 0 ? 0 : (bekend?.Pogingen ?? 0) + 1)
                {
                    Avatar = g.Avatar, Ontvangers = g.Ontvangers, Overige = g.Overige,
                };
            }
            if (bekend.Bijlagen.Length > 0 && LokaleBijlagen(msgId).Count == 0)
            {
                // Bijlagen meteen proactief meepakken: de klik op een 📎-chip in de
                // cockpit opent dan direct het lokale bestand, zonder wachten.
                try
                {
                    if (await OpenBerichtRijAsync(msgId, ct))
                    {
                        await DownloadGeopendeBijlagenAsync(msgId, ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Best effort: dan downloadt de chip-klik het later alsnog zelf.
                    _debugStappen.Add($"bijlagen vooraf {kind}/{msgId}: {ex.Message}");
                }
            }
            resultaat.Add(bekend);
        }
        return resultaat;
    }

    /// <summary>Wat het leesvenster van één bericht oplevert.</summary>
    private sealed record GelezenBericht(
        string Tekst, string Html, string Bijlagen, string Avatar, string Ontvangers, int Overige)
    {
        public static readonly GelezenBericht Leeg = new("", "", "", "", "", 0);
    }

    /// <summary>
    /// Opent één bericht (klik op de rij) en leest de inhoud zoals Smartschool die toont:
    /// alleen de berichttekst (niet de kop, die staat apart in afzender/onderwerp/ontvangers),
    /// met afbeeldingen verkleind ingebed (de schoolserver geeft ze buiten de sessie niet
    /// af), de profielfoto van de afzender en de ontvangersregel.
    /// </summary>
    private async Task<GelezenBericht> LeesBerichtAsync(string msgId, CancellationToken ct)
    {
        var idJs = JsonSerializer.Serialize("row_" + msgId);
        if (await JsAsync(
            $$"""
            (function () {
                const rij = document.getElementById({{idJs}});
                if (!rij) return 'weg';
                rij.click();
                return 'ok';
            })()
            """) is not "\"ok\"")
        {
            return GelezenBericht.Leeg;
        }
        // De tekst staat er snel; de bijlagenlijst (#attachlist) laadt asynchroon ná het
        // bericht (aparte mustache-XHR) — maximaal ~3 s extra wachten op bijlagen.
        var klaarVanaf = -1;
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(500, ct);
            var stand = await JsAsync(
                """
                (function () {
                    const d = document.querySelector('#msgdetail');
                    if (!d || (d.innerText || '').trim().length < 5) return 'leeg';
                    return d.querySelectorAll('.attachment .attachment__title__label, .attachment__title[title]').length > 0
                        ? 'bijlagen' : 'tekst';
                })()
                """);
            if (stand.Contains("leeg"))
            {
                continue;
            }
            if (klaarVanaf < 0)
            {
                klaarVanaf = i;
            }
            if (stand.Contains("bijlagen") || i - klaarVanaf >= 6)
            {
                break;
            }
        }
        if (klaarVanaf < 0)
        {
            return GelezenBericht.Leeg;
        }
        await JsAsync(
            """
            (function () {
                window.__wmSms = null;
                (async () => {
                    try {
                        const d = document.querySelector('#msgdetail');
                        if (!d) { window.__wmSms = 'leeg'; return; }
                        const bijlagen = [...new Set(
                            [...d.querySelectorAll('.attachment .attachment__title__label, .attachment__title[title]')]
                            .map(a => ((a.textContent || '').trim() || a.getAttribute('title') || '')
                                .replace(/\s+/g, ' ').trim()).filter(Boolean))];
                        const body = d.querySelector('.msgContentVal') || d.querySelector('.msgContent') || d;
                        const kloon = body.cloneNode(true);
                        for (const el of kloon.querySelectorAll('[src], [href]')) {
                            for (const at of ['src', 'href']) {
                                const v = el.getAttribute(at);
                                if (v && v.startsWith('/')) el.setAttribute(at, location.origin + v);
                            }
                        }
                        // Afbeelding → verkleinde data-URL (fetch mét sessiecookies).
                        const naarData = async (src, maxBreed, kwaliteit) => {
                            try {
                                const blob = await (await fetch(src, { credentials: 'include' })).blob();
                                if (!/^image\//.test(blob.type)) return '';
                                const bmp = await createImageBitmap(blob);
                                const schaal = Math.min(1, maxBreed / (bmp.width || maxBreed));
                                const cv = document.createElement('canvas');
                                cv.width = Math.max(1, Math.round(bmp.width * schaal));
                                cv.height = Math.max(1, Math.round(bmp.height * schaal));
                                cv.getContext('2d').drawImage(bmp, 0, 0, cv.width, cv.height);
                                const png = blob.type === 'image/png' && bmp.width * bmp.height < 200000;
                                let uit = png ? cv.toDataURL('image/png') : cv.toDataURL('image/jpeg', kwaliteit);
                                if (png && uit.length > 60000) uit = cv.toDataURL('image/jpeg', kwaliteit);
                                return uit;
                            } catch { return ''; }
                        };
                        let budget = 120000;
                        const orig = [...body.querySelectorAll('img')];
                        const kop = [...kloon.querySelectorAll('img')];
                        for (let i = 0; i < kop.length; i++) {
                            const src = orig[i]?.currentSrc || orig[i]?.src || kop[i].getAttribute('src') || '';
                            if (!src || src.startsWith('data:')) continue;
                            const w = orig[i]?.naturalWidth || 0, h = orig[i]?.naturalHeight || 0;
                            if (w && w < 24 && h < 24) { kop[i].remove(); continue; }
                            const data = await naarData(src, 900, 0.75);
                            if (data && data.length <= budget) {
                                kop[i].src = data;
                                budget -= data.length;
                                kop[i].removeAttribute('width');
                                kop[i].removeAttribute('height');
                                kop[i].style.maxWidth = '100%';
                                kop[i].style.height = 'auto';
                            } else {
                                kop[i].remove();
                            }
                        }
                        // Profielfoto: staat op een aparte host (userpicture*.smartschool.be) zonder
                        // CORS, dus fetchen lukt niet — de URL is wél publiek (hash-gebaseerd),
                        // dus de cockpit laadt hem rechtstreeks.
                        const foto = d.querySelector('.msgHeaderImg img');
                        const fotoSrc = foto ? (foto.currentSrc || foto.src || '') : '';
                        const avatar = fotoSrc ? (await naarData(fotoSrc, 64, 0.8) || fotoSrc) : '';
                        const ontvEl = [...d.querySelectorAll('.msgHeaderVal')]
                            .find(e => /ontvangers/i.test(e.textContent || ''));
                        let ontvangers = '', overige = 0;
                        if (ontvEl) {
                            const t = (ontvEl.textContent || '').replace(/\s+/g, ' ')
                                .replace(/^\s*Ontvangers:\s*/i, '').trim();
                            const m = t.match(/Toon overige \((\d+)\)/i);
                            overige = m ? parseInt(m[1], 10) : 0;
                            ontvangers = t.replace(/,?\s*Toon overige.*$/i, '').trim();
                        }
                        window.__wmSms = JSON.stringify({
                            tekst: (body.innerText || '').trim().slice(0, 12000),
                            html: kloon.innerHTML.slice(0, 160000),
                            bijlagen: bijlagen.slice(0, 12).join('; '),
                            avatar, ontvangers, overige,
                        });
                    } catch (e) { window.__wmSms = 'FOUT: ' + e; }
                })();
                return true;
            })()
            """);
        for (var i = 0; i < 40; i++) // afbeeldingen inbedden kan even duren
        {
            await Task.Delay(300, ct);
            var klaar = await JsAsync("window.__wmSms");
            if (klaar == "null")
            {
                continue;
            }
            var json = Ontdubbel(klaar);
            if (!json.StartsWith('{'))
            {
                _debugStappen.Add($"lezen {msgId}: {json}");
                return GelezenBericht.Leeg;
            }
            using var doc = JsonDocument.Parse(json);
            string S(string naam) => doc.RootElement.TryGetProperty(naam, out var v) ? v.GetString() ?? "" : "";
            return new GelezenBericht(S("tekst"), S("html"), S("bijlagen"), S("avatar"), S("ontvangers"),
                doc.RootElement.TryGetProperty("overige", out var o) && o.ValueKind == JsonValueKind.Number
                    ? o.GetInt32() : 0);
        }
        return GelezenBericht.Leeg;
    }

    // ---------- Meldingen (het belletje) ----------

    /// <summary>
    /// Eén melding uit het belletje van een co-account. Module "messages" = melding van een
    /// bericht (Url bevat msgID); andere modules (agenda, resultaten, …) tonen we als eigen
    /// rij in de cockpit.
    /// </summary>
    public sealed record SmartschoolMelding(
        string Kind, string Sleutel, string Module, string Titel, string Info, string Datum,
        string Url, bool Ongelezen)
    {
        public string MsgId => System.Text.RegularExpressions.Regex.Match(Url, @"msgID=(\d+)").Groups[1].Value;
    }

    private static readonly string MeldingenFile = Path.Combine(DataDir, "smartschool-meldingen.json");

    /// <summary>De niet-berichtmeldingen van de laatste ophaalbeurt (uit de cache).</summary>
    public static List<SmartschoolMelding> Meldingen()
    {
        try
        {
            if (File.Exists(MeldingenFile) &&
                JsonSerializer.Deserialize<List<SmartschoolMelding>>(File.ReadAllText(MeldingenFile)) is { } lijst)
            {
                return lijst;
            }
        }
        catch
        {
            // Onleesbaar: dan even geen meldingen.
        }
        return new List<SmartschoolMelding>();
    }

    private static void BewaarMeldingen(List<SmartschoolMelding> meldingen)
    {
        try
        {
            File.WriteAllText(MeldingenFile, JsonSerializer.Serialize(meldingen));
        }
        catch
        {
            // Cache is comfort.
        }
    }

    /// <summary>Leest het belletje van het actieve co-account (staat in de DOM, ook dicht).</summary>
    private async Task<List<SmartschoolMelding>> LeesMeldingenAsync(string kind)
    {
        var json = Ontdubbel(await JsAsync(
            """
            JSON.stringify([...document.querySelectorAll('.js-notifs-list .notification')].map(n => {
                const a = n.querySelector('a.js-notif-btn, a');
                const infos = [...n.querySelectorAll('.notification__info')]
                    .map(i => (i.textContent || '').replace(/\s+/g, ' ').trim()).filter(Boolean);
                const mod = ((n.querySelector('.notification__image')?.className) || '')
                    .match(/module-([a-z0-9_]+)--/i);
                return {
                    href: a ? (a.getAttribute('href') || '') : '',
                    titel: (n.querySelector('.notification__title')?.textContent || '').replace(/\s+/g, ' ').trim(),
                    info: infos.filter(t => !/^\d{4}-\d{2}-\d{2}/.test(t)).join(' · '),
                    datum: infos.find(t => /^\d{4}-\d{2}-\d{2}/.test(t)) || '',
                    module: mod ? mod[1].toLowerCase() : '',
                    ongelezen: !!(a && a.classList.contains('notification__btn--unread')),
                };
            }))
            """));
        var uit = new List<SmartschoolMelding>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                string S(string naam) => e.TryGetProperty(naam, out var v) ? v.GetString() ?? "" : "";
                var href = S("href");
                var url = href.Length > 0 ? (href.StartsWith("http") ? href : Basis + href) : "";
                var sleutel = href.Length > 0 ? href : $"{S("titel")}|{S("info")}|{S("datum")}";
                uit.Add(new SmartschoolMelding(kind, sleutel, S("module"), S("titel"), S("info"), S("datum"), url,
                    e.TryGetProperty("ongelezen", out var o) && o.ValueKind == JsonValueKind.True));
            }
        }
        catch
        {
            _debugStappen.Add($"meldingen {kind}: onleesbaar");
        }
        return uit;
    }

    /// <summary>Wist één melding uit het belletje (het kruisje van die regel). Zelfde pagina.</summary>
    private async Task<bool> WisMeldingAsync(string sleutel, CancellationToken ct)
    {
        var uit = await JsAsync(
            $$"""
            (function () {
                const sleutel = {{JsonSerializer.Serialize(sleutel)}};
                for (const n of document.querySelectorAll('.js-notifs-list .notification')) {
                    const a = n.querySelector('a.js-notif-btn, a');
                    const href = a ? (a.getAttribute('href') || '') : '';
                    const infos = [...n.querySelectorAll('.notification__info')]
                        .map(i => (i.textContent || '').replace(/\s+/g, ' ').trim()).filter(Boolean);
                    const titel = (n.querySelector('.notification__title')?.textContent || '').replace(/\s+/g, ' ').trim();
                    const alt = titel + '|' + infos.filter(t => !/^\d{4}-\d{2}-\d{2}/.test(t)).join(' · ') + '|' +
                        (infos.find(t => /^\d{4}-\d{2}-\d{2}/.test(t)) || '');
                    if (href === sleutel || alt === sleutel) {
                        const x = n.querySelector('.js-notif-close');
                        if (!x) return 'geen-kruisje';
                        x.click();
                        return 'ok';
                    }
                }
                return 'niet-gevonden';
            })()
            """);
        await Task.Delay(400, ct);
        return uit.Contains("ok");
    }

    /// <summary>
    /// Meldingen van dit kind verwerken: berichtmeldingen waarvan het bericht al uit het
    /// Postvak IN is (in de cockpit gearchiveerd) worden gewist — anders blijft het
    /// belletje in Smartschool eeuwig een teller tonen; overige meldingen komen terug
    /// als cockpitrijen. Aanroeper staat op de berichtenpagina van het kind.
    /// </summary>
    private async Task<List<SmartschoolMelding>> VerwerkMeldingenAsync(
        string kind, HashSet<string> inboxIds, CancellationToken ct)
    {
        var meldingen = await LeesMeldingenAsync(kind);
        var overige = new List<SmartschoolMelding>();
        foreach (var m in meldingen)
        {
            ct.ThrowIfCancellationRequested();
            if (m.MsgId.Length > 0)
            {
                if (!inboxIds.Contains(m.MsgId))
                {
                    var gewist = await WisMeldingAsync(m.Sleutel, ct);
                    _debugStappen.Add($"melding {kind} bericht {m.MsgId} (al afgehandeld): " +
                        (gewist ? "gewist" : "niet gewist"));
                }
                continue; // berichtmeldingen staan al als bericht in de cockpit
            }
            overige.Add(m);
        }
        return overige;
    }

    // ---------- Hulpjes ----------

    /// <summary>
    /// De wizard "Berichtenonderhoud" (jQuery-dialoog) verschijnt af en toe bovenop de
    /// berichtenmodule en blokkeert de lijst. Sluiten via het kruisje — nooit via OK, dat
    /// zou berichten verplaatsen/verwijderen — en anders de dialoog client-side weghalen.
    /// </summary>
    private async Task SluitWizardAsync()
    {
        try
        {
            await JsAsync(
                """
                (function () {
                    const dlg = document.querySelector('.ui-dialog.rulesDialogue');
                    if (!dlg) return 'geen';
                    dlg.querySelector('.ui-dialog-titlebar-close')?.click();
                    setTimeout(() => {
                        document.querySelectorAll('.ui-dialog.rulesDialogue, .ui-widget-overlay').forEach(e => e.remove());
                    }, 300);
                    return 'gesloten';
                })()
                """);
        }
        catch
        {
            // Best effort.
        }
    }


    private async Task NavigeerAsync(string url, CancellationToken ct)
    {
        _web!.CoreWebView2!.Navigate(url);
        await Task.Delay(1200, ct);
        await SluitWizardAsync();
        // Terug op /login of de verificatievraag beland (Smartschool laat de sessie
        // geregeld vallen, o.a. na de kindwissel): opnieuw aanmelden en de doelpagina
        // nog één keer laden.
        if (await JsAsync(
                "(location.pathname.startsWith('/login') || " +
                "location.pathname.startsWith('/account-verification')) ? 'ja' : 'nee'")
                is "\"ja\"" &&
            await ZorgIngelogdAsync(ct, rondes: 12))
        {
            _web.CoreWebView2!.Navigate(url);
            await Task.Delay(1200, ct);
            await SluitWizardAsync();
        }
    }

    private async Task WachtOpAsync(string selector, CancellationToken ct)
    {
        var selJs = JsonSerializer.Serialize(selector);
        for (var i = 0; i < 20; i++)
        {
            if (await JsAsync($"!!document.querySelector({selJs})") == "true")
            {
                return;
            }
            await Task.Delay(500, ct);
        }
    }

    private async Task<string> JsAsync(string script)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("Smartschool-sessie is niet gestart.");
        }
        return await core.ExecuteScriptAsync(script);
    }

    /// <summary>ExecuteScriptAsync geeft JSON terug; een string-resultaat zit in quotes.</summary>
    private static string Ontdubbel(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? json;
        }
        catch
        {
            return json;
        }
    }

    /// <summary>"2026-06-30 16:00" uit de berichtenlijst.</summary>
    private static DateTimeOffset ParseMoment(string tekst)
    {
        return DateTimeOffset.TryParse(tekst, out var d) ? d : DateTimeOffset.Now;
    }

    private List<SmartschoolBericht> LaadCache()
    {
        if (_cache is not null)
        {
            return _cache;
        }
        try
        {
            if (File.Exists(BerichtenFile) &&
                JsonSerializer.Deserialize<List<SmartschoolBericht>>(
                    File.ReadAllText(BerichtenFile)) is { } berichten)
            {
                return _cache = berichten;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen; berichten worden gewoon opnieuw opgehaald.
        }
        return _cache = new List<SmartschoolBericht>();
    }

    private void BewaarCache(List<SmartschoolBericht> berichten)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            _cache = berichten;
            File.WriteAllText(BerichtenFile, JsonSerializer.Serialize(berichten));
        }
        catch
        {
            // Cache is best effort.
        }
        try
        {
            // Lokale bijlagen van berichten die niet meer in de lijst staan
            // (gearchiveerd) opruimen — de map groeit anders eindeloos aan.
            var basis = Path.Combine(DataDir, "smartschool-bijlagen");
            if (Directory.Exists(basis))
            {
                var actueel = berichten.Select(b => b.MsgId).ToHashSet();
                foreach (var map in Directory.GetDirectories(basis)
                    .Where(m => !actueel.Contains(Path.GetFileName(m))))
                {
                    Directory.Delete(map, recursive: true);
                }
            }
        }
        catch
        {
            // Opruimen is best effort.
        }
    }

    public void Dispose()
    {
        _web?.Dispose();
        _venster?.Dispose();
    }
}
