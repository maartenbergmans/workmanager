using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// WhatsApp Web in een (meestal onzichtbaar) WebView2-venster: chats met ongelezen
/// berichten uitlezen en antwoorden versturen via de DOM van web.whatsapp.com — de
/// officiële webclient dus, geen nagemaakt protocol. De koppeling (QR-scan) gebeurt
/// één keer in het zichtbare venster en blijft bewaard in een eigen WebView2-profiel.
/// Kanttekening: WhatsApp wijzigt de DOM geregeld; de selectors hier zijn op de meest
/// stabiele ankers gebouwd (#pane-side, #main, data-pre-plain-text) en fouten worden
/// netjes gelogd in plaats van stil te mislukken.
/// </summary>
public sealed class WhatsAppClient : IDisposable
{
    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string MarkerFile = Path.Combine(DataDir, "whatsapp-linked.txt");

    /// <summary>
    /// Eén gedeelde sessie voor de hele app (mailvenster én cockpit): het WebView2-profiel
    /// kan maar door één instantie tegelijk gebruikt worden.
    /// </summary>
    public static WhatsAppClient Instance { get; } = new();

    private Form? _venster;
    private WebView2? _web;
    private readonly SemaphoreSlim _slot = new(1, 1); // fetch/verstuur delen één DOM
    private volatile bool _gecrasht; // browserproces weg → bij de volgende beurt vers opbouwen

    /// <summary>Is er ooit met succes gekoppeld? Zo niet, dan slaat de fetch WhatsApp over.</summary>
    public static bool OoitGekoppeld => File.Exists(MarkerFile);

    /// <summary>
    /// Nachtelijk onderhoud: de sessie bij de eerstvolgende beurt volledig vers opbouwen
    /// (zelfde route als het crash-herstel; het profiel met de QR-koppeling blijft staan).
    /// </summary>
    public void MarkeerVoorVerseStart() => _gecrasht = true;

    // ---------- Venster en sessie ----------

    /// <summary>
    /// Start de ingebedde WhatsApp Web-sessie (verborgen: het venster staat buiten beeld,
    /// zodat WebView2 betrouwbaar initialiseert). Retourneert true zodra de chatlijst er
    /// staat (= ingelogd); false als er een QR-scan nodig is.
    /// </summary>
    public async Task<bool> StartAsync(CancellationToken ct, int wachtSeconden = 30)
    {
        if (_gecrasht)
        {
            // Na een browsercrash (of het nachtelijk onderhoud) is de oude CoreWebView2
            // niet meer te vertrouwen: weggooien zodat hieronder een verse sessie start
            // (het profiel met de QR-koppeling blijft staan).
            try { _web?.Dispose(); } catch { /* al kapot */ }
            try { _venster?.Dispose(); } catch { /* al kapot */ }
            _web = null;
            _venster = null;
            _gecrasht = false;
        }
        if (_web?.CoreWebView2 is null)
        {
            _venster = new Form
            {
                Text = "WhatsApp koppelen – scan de QR-code met je telefoon",
                // Bewust hoog (buiten beeld): meer gerenderde chatrijen in de lijst.
                Size = new Size(900, 1500),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, -4000), // draaien buiten beeld
                ShowInTaskbar = false,
            };
            _venster.FormClosing += (_, e) =>
            {
                // Niet echt sluiten: de sessie blijft op de achtergrond beschikbaar.
                e.Cancel = true;
                Verberg();
            };
            _web = new WebView2 { Dock = DockStyle.Fill };
            _venster.Controls.Add(_web);
            _venster.Show();

            // Zonder deze vlaggen bevriest de browser de (onzichtbare) pagina na een
            // tijdje en komen nieuwe berichten niet meer binnen.
            var env = await CoreWebView2Environment.CreateAsync(null,
                Path.Combine(DataDir, "webview2-whatsapp"),
                new CoreWebView2EnvironmentOptions(
                    "--disable-background-timer-throttling " +
                    "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding"));
            // Met tijdslimiet én zelfherstel: hangt de init op een vergrendeld profiel
            // (achtergebleven webview-processen), dan worden die opgeruimd en volgt
            // één nieuwe poging met een verse control.
            _web = await WebViewOpruimer.InitMetHerstelAsync(_venster, _web, env,
                Path.Combine(DataDir, "webview2-whatsapp"), "WhatsApp", ct);
            // Crasht het browserproces, dan is deze CoreWebView2 blijvend ongeldig:
            // markeren zodat de volgende beurt de sessie automatisch vers opbouwt.
            // Alleen een renderer-crash is ter plekke te herstellen met een reload.
            var webNu = _web;
            _web.CoreWebView2!.ProcessFailed += (_, e) =>
            {
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                    or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                {
                    try
                    {
                        webNu.CoreWebView2!.Reload();
                    }
                    catch
                    {
                        _gecrasht = true;
                    }
                }
                else
                {
                    _gecrasht = true;
                }
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "wa-crash-log.txt"),
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ProcessFailed: " +
                        $"{e.ProcessFailedKind} (herstel: {(_gecrasht ? "herstart bij volgende poll" : "reload")})\r\n");
                }
                catch
                {
                    // Alleen diagnose.
                }
            };
            // Het venster is verborgen en heeft dus nooit focus; WhatsApp Web verstuurt
            // leesbevestigingen alleen bij focus én een zichtbaar document, dus beide
            // melden we altijd als aanwezig.
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                """
                document.hasFocus = () => true;
                try {
                    Object.defineProperty(document, 'visibilityState', { get: () => 'visible' });
                    Object.defineProperty(document, 'hidden', { get: () => false });
                } catch { }
                """);
            _web.CoreWebView2.Navigate("https://web.whatsapp.com");
        }

        // Wachten tot de chatlijst geladen is (ingelogd) of opgeven (QR nodig / traag).
        for (var i = 0; i < wachtSeconden * 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsIngelogdAsync())
            {
                File.WriteAllText(MarkerFile, DateTimeOffset.Now.ToString("O"));
                return true;
            }
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>Toont het venster (voor de QR-scan) en verbergt het weer zodra de login rond is.</summary>
    public async Task KoppelAsync(CancellationToken ct)
    {
        // Kort checken of we al ingelogd zijn; zo niet, meteen het venster tonen.
        var ingelogd = await StartAsync(ct, wachtSeconden: 4);
        if (ingelogd || _venster is null)
        {
            return;
        }
        _venster.Size = new Size(560, 660); // schermvriendelijk voor de QR-scan
        _venster.Location = new Point(
            (Screen.PrimaryScreen!.WorkingArea.Width - _venster.Width) / 2,
            (Screen.PrimaryScreen.WorkingArea.Height - _venster.Height) / 2);
        _venster.Activate();

        for (var i = 0; i < 600; i++) // max. 5 minuten op de scan wachten
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
            if (await IsIngelogdAsync())
            {
                File.WriteAllText(MarkerFile, DateTimeOffset.Now.ToString("O"));
                await Task.Delay(1500, ct); // chatlijst even laten laden
                _venster!.Size = new Size(900, 1500); // terug naar de hoge leesstand
                Verberg();
                return;
            }
        }
        _venster!.Size = new Size(900, 1500);
        Verberg();
        throw new TimeoutException("De QR-code werd niet (op tijd) gescand.");
    }

    private void Verberg() => _venster!.Location = new Point(-4000, -4000);

    private async Task<bool> IsIngelogdAsync() =>
        await JsAsync("document.querySelector('#pane-side') !== null") == "true";

    // ---------- Chats ophalen ----------

    /// <summary>
    /// Leest de chats met ongelezen berichten uit: per chat wordt het gesprek geopend en
    /// worden de recente berichten als transcript meegegeven. Let op: WhatsApp kan het
    /// openen als "gelezen" registreren, zoals wanneer je zelf de webclient gebruikt.
    /// </summary>
    public async Task<List<MailBericht>> FetchAsync(Action<string> log, CancellationToken ct)
    {
        // Met tijdslimiet op het slot: hing er een vorige beurt vast, dan blokkeerde elke
        // volgende poll daarop — en leek WhatsApp "dood" terwijl alleen de wachtrij vastzat.
        if (!await _slot.WaitAsync(TimeSpan.FromSeconds(90), ct))
        {
            _gecrasht = true;
            throw new TimeoutException(
                "Een vorige WhatsApp-actie hangt nog; de sessie wordt vers opgestart.");
        }
        try
        {
            try
            {
                return await FetchKernAsync(log, ct);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException &&
                                       !ct.IsCancellationRequested)
            {
                // Eén automatische herkansing met een verse sessie: de meeste storingen zijn
                // een vastgelopen of gecrashte browser, en die is met opnieuw opbouwen weg.
                log($"WhatsApp hapert ({ex.Message}) — sessie wordt vers opgestart en opnieuw geprobeerd.");
                _gecrasht = true;
                await Task.Delay(1500, ct);
                return await FetchKernAsync(log, ct);
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    private async Task<List<MailBericht>> FetchKernAsync(Action<string> log, CancellationToken ct)
    {
        if (!await StartAsync(ct))
        {
            throw new InvalidOperationException(
                "WhatsApp Web is niet ingelogd — koppel opnieuw via 'WhatsApp koppelen…'.");
        }

        // Rijen: nieuwere builds gebruiken role="listitem", oudere role="row". Ongelezen:
        // een badge-span met een cijfer, of een aria-label met "ongelezen"/"unread".
        const string LijstScript =
            """
            (function () {
                const rows = [...document.querySelectorAll(
                    '#pane-side [role="listitem"], #pane-side [role="row"]')];
                const res = [];
                for (const r of rows) {
                    const naam = r.querySelector('span[title]')?.getAttribute('title') || '';
                    if (!naam) continue;
                    const badge = [...r.querySelectorAll('span')].find(s =>
                        (/^\d+$/.test(s.textContent.trim()) && s.getAttribute('aria-label')) ||
                        /ongelezen|unread|non lu/i.test(s.getAttribute('aria-label') || ''));
                    res.push({ naam, ongelezen: !!badge });
                }
                return res;
            })()
            """;
        var lijstJson = await JsAsync(LijstScript);
        if (lijstJson == "[]")
        {
            await Task.Delay(3000, ct); // chatlijst laadt soms nog na het inloggen
            lijstJson = await JsAsync(LijstScript);
        }
        using var lijst = JsonDocument.Parse(lijstJson);
        var alle = lijst.RootElement.EnumerateArray()
            .Select(r => (Naam: r.GetProperty("naam").GetString() ?? "",
                          Ongelezen: r.GetProperty("ongelezen").GetBoolean()))
            .Where(r => r.Naam.Length > 0)
            .ToList();
        var namen = alle.Where(r => r.Ongelezen).Select(r => r.Naam).Distinct().ToList();
        log($"WhatsApp: {alle.Count} chats in de lijst, {namen.Count} met ongelezen berichten.");

        var resultaat = new List<MailBericht>();
        foreach (var naam in namen)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var berichten = await OpenEnLeesAsync(naam, ct);
                if (berichten.Count == 0)
                {
                    continue;
                }
                var laatste = berichten[^1];
                resultaat.Add(new MailBericht
                {
                    WhatsAppChat = naam,
                    MessageId = "wa:" + naam + ":" + laatste.Pre + Kort(laatste.Tekst, 40),
                    Van = naam,
                    VanAdres = "whatsapp",
                    Onderwerp = laatste.Tekst.Length > 0 ? Kort(laatste.Tekst, 80) : "📷 foto",
                    Datum = DateTimeOffset.Now,
                    Tekst = string.Join("\n", berichten.Select(b =>
                        (b.Uitgaand ? "[eerder] Maarten (ikzelf): " : b.Pre.Length > 0 ? b.Pre : $"{naam}: ") +
                        (b.Tekst.Length > 0 ? b.Tekst : b.Beeld.Length > 0 ? "📷 (foto)" : "") +
                        (b.Reacties.Length > 0 ? $" [reactie: {b.Reacties}]" : ""))),
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Eén chat die niet lukt (bv. DOM-wijziging) mag de rest niet blokkeren.
                log($"WhatsApp-chat \"{naam}\" uitlezen mislukt: {ex.Message}");
            }
        }
        return resultaat;
    }

    public sealed record WaChat(string Naam, string Preview);

    /// <summary>
    /// Chats met ongelezen berichten uit de zijbalk (voor de cockpit-poll), mét de preview
    /// van het laatste bericht — zonder gesprekken te openen, dus zonder blauwe vinkjes.
    /// </summary>
    public async Task<(int Totaal, List<WaChat> Chats)> OngelezenChatsAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException("WhatsApp Web is niet ingelogd.");
            }
            // Betrouwbaarste route: WhatsApps eigen "Ongelezen"-filterknop aanklikken en
            // de gefilterde lijst lezen (dekt ook gemiste oproepen en badge-loze gevallen).
            var viaFilter = await OngelezenViaFilterAsync(ct);
            if (viaFilter is { } resultaatViaFilter)
            {
                return resultaatViaFilter;
            }
            // Terugval: de oude badge-detectie op de volledige lijst.
            var json = await JsAsync(
                """
                (function () {
                    const rows = [...document.querySelectorAll(
                        '#pane-side [role="listitem"], #pane-side [role="row"]')];
                    const res = [];
                    let totaal = 0;
                    for (const r of rows) {
                        const spans = [...r.querySelectorAll('span[title]')];
                        const naam = spans[0]?.getAttribute('title') || '';
                        if (!naam) continue;
                        totaal++;
                        const badge = [...r.querySelectorAll('span')].find(s =>
                            (/^\d+$/.test(s.textContent.trim()) && s.getAttribute('aria-label')) ||
                            /ongelezen|unread|non lu/i.test(s.getAttribute('aria-label') || ''));
                        if (!badge) continue;
                        // Preview van het laatste bericht: een tweede title-span als die er is,
                        // anders de rijtekst zonder naam, tijdstip en badgecijfer.
                        let preview = spans[1]?.getAttribute('title') || '';
                        if (!preview) {
                            let t = (r.textContent || '').replace(/\s+/g, ' ').trim();
                            if (t.startsWith(naam)) t = t.slice(naam.length);
                            t = t.replace(/\b\d{1,2}:\d{2}\b/, '');
                            const cijfer = badge.textContent.trim();
                            if (cijfer && t.endsWith(cijfer)) t = t.slice(0, -cijfer.length);
                            preview = t.trim();
                        }
                        res.push({ naam, preview: preview.slice(0, 300) });
                    }
                    return { totaal, chats: res };
                })()
                """);
            try
            {
                // Diagnose: wat ziet de zijbalk-lezer werkelijk? Plus een screenshot van
                // de verborgen pagina om de echte weergave te kunnen beoordelen.
                File.WriteAllText(Path.Combine(DataDir, "wa-debug.json"), json);
                using var beeld = new MemoryStream();
                await _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld);
                File.WriteAllBytes(Path.Combine(DataDir, "wa-screen.png"), beeld.ToArray());
            }
            catch
            {
                // Alleen diagnose.
            }
            using var doc = JsonDocument.Parse(json);
            return (
                doc.RootElement.GetProperty("totaal").GetInt32(),
                doc.RootElement.GetProperty("chats").EnumerateArray()
                    .Select(e => new WaChat(
                        e.GetProperty("naam").GetString() ?? "",
                        e.GetProperty("preview").GetString() ?? ""))
                    .Where(c => c.Naam.Length > 0)
                    .ToList());
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Beeld = foto uit de bubbel als data-URL (leeg als het bericht er geen heeft);
    /// Reacties = emoji-reacties op het bericht, bv. "❤️" of "👍 3" (leeg zonder reacties).
    /// </summary>
    public sealed record WaBericht(
        string Tijd, string Afzender, bool Uitgaand, string Tekst, string Beeld = "",
        string Reacties = "");

    /// <summary>
    /// De laatste berichten uit een chat, gestructureerd (tijd, afzender, richting, tekst)
    /// voor de bubbelweergave. Let op: hiervoor wordt de chat geopend, dus WhatsApp
    /// markeert hem als gelezen (blauwe vinkjes).
    /// </summary>
    public async Task<(List<WaBericht> Berichten, string AvatarDataUrl)> LaatsteBerichtenAsync(
        string naam, int max, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException("WhatsApp Web is niet ingelogd.");
            }
            var berichten = (await OpenEnLeesAsync(naam, ct))
                .TakeLast(max)
                .Select(r =>
                {
                    // Pre-formaat: "[21:20, 19/6/2026] Els Jaspers: ".
                    var tijd = "";
                    var afzender = r.Uitgaand ? "Ik" : naam;
                    var m = System.Text.RegularExpressions.Regex.Match(
                        r.Pre, @"^\[(?<tijd>[^\]]+)\]\s*(?<wie>[^:]*):\s*$");
                    if (m.Success)
                    {
                        tijd = m.Groups["tijd"].Value.Trim();
                        if (m.Groups["wie"].Value.Trim() is { Length: > 0 } wie)
                        {
                            afzender = wie;
                        }
                    }
                    return new WaBericht(tijd, afzender, r.Uitgaand, r.Tekst, r.Beeld, r.Reacties);
                })
                .ToList();
            return (berichten, await AvatarDataUrlAsync(ct));
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// De profielfoto van de nu geopende chat als data-URL (voor de kop van de
    /// bubbelweergave); leeg als er geen foto is of de conversie mislukt.
    /// </summary>
    private async Task<string> AvatarDataUrlAsync(CancellationToken ct)
    {
        try
        {
            // De foto is een blob-URL in de chatkop: in de paginacontext ophalen en als
            // data-URL teruggeven (een asynchrone job, dus via window.__wmAvatar pollen).
            await JsAsync(
                """
                (function () {
                    window.__wmAvatar = null;
                    (async () => {
                        try {
                            const img = document.querySelector('#main header img');
                            if (!img || !img.src) { window.__wmAvatar = ''; return; }
                            const r = await fetch(img.src);
                            const b = await r.blob();
                            const fr = new FileReader();
                            fr.onload = () => { window.__wmAvatar = String(fr.result); };
                            fr.onerror = () => { window.__wmAvatar = ''; };
                            fr.readAsDataURL(b);
                        } catch { window.__wmAvatar = ''; }
                    })();
                    return true;
                })()
                """);
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(300, ct);
                var klaar = await JsAsync("window.__wmAvatar");
                if (klaar is not ("null" or "\"null\""))
                {
                    var url = JsonSerializer.Deserialize<string>(klaar) ?? "";
                    return url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) ? url : "";
                }
            }
        }
        catch
        {
            // Geen foto is geen ramp; dan toont de kop een initiaal.
        }
        return "";
    }

    /// <summary>
    /// Zet een chat in WhatsApp als gelezen (voor "Archiveren" in de cockpit) via het
    /// rijmenu → "Als gelezen markeren" — dat werkt ook zonder vensterfocus. De rij wordt
    /// zo nodig scrollend gezocht (de chatlijst is gevirtualiseerd). Terugvaloptie: de chat
    /// kort openen (met gespoofde focus) en weer sluiten.
    /// </summary>
    public async Task MarkeerGelezenAsync(string naam, CancellationToken ct)
    {
        void Log(string melding)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "wa-gelezen-debug.txt"),
                    $"{DateTime.Now:HH:mm:ss} {naam}: {melding}\r\n");
            }
            catch
            {
                // Alleen diagnose.
            }
        }

        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                Log("niet ingelogd");
                throw new InvalidOperationException("WhatsApp Web is niet ingelogd.");
            }
            await JsAsync(
                $$"""
                (function () {
                    window.__wmWaGelezen = null;
                    (async () => {
                        const naam = {{JsonSerializer.Serialize(naam)}};
                        const res = { stap: 'start' };
                        const wacht = ms => new Promise(r => setTimeout(r, ms));
                        const vindRij = () => {
                            for (const r of document.querySelectorAll(
                                '#pane-side [role="listitem"], #pane-side [role="row"]')) {
                                const t = r.querySelector('span[title]');
                                if (t && t.getAttribute('title') === naam) return r;
                            }
                            return null;
                        };
                        const events = (el, types, extra) => {
                            const b = el.getBoundingClientRect();
                            const opts = { bubbles: true, cancelable: true, view: window,
                                clientX: b.x + b.width / 2, clientY: b.y + b.height / 2, ...extra };
                            for (const t of types) {
                                el.dispatchEvent(t.startsWith('pointer')
                                    ? new PointerEvent(t, opts) : new MouseEvent(t, opts));
                            }
                        };
                        const klik = el => events(el, ['pointerover', 'mouseover', 'pointerdown',
                            'mousedown', 'pointerup', 'mouseup', 'click'], { buttons: 1 });
                        const hover = el => events(el, ['pointerover', 'pointerenter', 'mouseover',
                            'mouseenter', 'pointermove', 'mousemove'], {});
                        let rij = vindRij();
                        const pane = document.querySelector('#pane-side');
                        if (!rij && pane) {
                            // Gevirtualiseerde lijst: scrollend zoeken tot de rij bestaat.
                            pane.scrollTop = 0;
                            for (let i = 0; i < 30 && !rij; i++) {
                                pane.scrollTop += pane.clientHeight * 0.8;
                                await wacht(250);
                                rij = vindRij();
                                if (pane.scrollTop + pane.clientHeight >=
                                    pane.scrollHeight - 5) break;
                            }
                        }
                        if (!rij) { res.stap = 'rij-niet-gevonden'; window.__wmWaGelezen = res; return; }
                        rij.scrollIntoView({ block: 'center' });
                        const escape = () => document.body.dispatchEvent(new KeyboardEvent('keydown',
                            { key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true }));
                        // Let op: "Als ongelezen markeren" bevat "gelezen markeren", dus
                        // expliciet uitsluiten.
                        const vindItem = () => [...document.querySelectorAll(
                            '[role="menuitem"], [role="option"], li, [role="button"]')]
                            .find(el => {
                                const t = (el.textContent || '').trim();
                                return /als gelezen markeren|gelezen markeren|mark as read|marquer comme lu/i.test(t) &&
                                    !/ongelezen|unread|non lu/i.test(t);
                            });
                        // Route 1: rechtsklik op de rij → contextmenu → "Als gelezen markeren".
                        const b = rij.getBoundingClientRect();
                        const rkOpts = { bubbles: true, cancelable: true, view: window,
                            button: 2, buttons: 2,
                            clientX: b.x + b.width / 2, clientY: b.y + b.height / 2 };
                        rij.dispatchEvent(new PointerEvent('pointerdown', rkOpts));
                        rij.dispatchEvent(new MouseEvent('mousedown', rkOpts));
                        rij.dispatchEvent(new MouseEvent('contextmenu', rkOpts));
                        await wacht(700);
                        let item = vindItem();
                        res.contextMenu = !!item;
                        if (!item) {
                            // Route 2: hover toont de chevron van de rij → menu → idem.
                            escape();
                            await wacht(300);
                            hover(rij);
                            await wacht(500);
                            const knop = rij.querySelector(
                                '[data-icon*="down"], [data-icon*="chevron"],' +
                                'button[aria-haspopup], [aria-label*="menu" i]');
                            res.menuKnop = !!knop;
                            if (knop) {
                                klik(knop);
                                await wacht(700);
                                item = vindItem();
                                res.menuTeksten = [...document.querySelectorAll('[role="menuitem"], li')]
                                    .map(e => (e.textContent || '').trim().slice(0, 40))
                                    .filter(t => t).slice(0, 12);
                            }
                        }
                        if (item) {
                            klik(item);
                            res.stap = 'menu-geklikt';
                        } else {
                            // Menu sluiten en terugvallen op de chat kort openen: door de
                            // gespoofde focus verstuurt WhatsApp dan alsnog de leesbevestiging.
                            document.body.dispatchEvent(new KeyboardEvent('keydown',
                                { key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true }));
                            await wacht(300);
                            klik(rij);
                            res.stap = 'chat-geopend';
                            await wacht(2000);
                            window.dispatchEvent(new Event('focus'));
                            await wacht(1500);
                            document.body.dispatchEvent(new KeyboardEvent('keydown',
                                { key: 'Escape', code: 'Escape', keyCode: 27, which: 27, bubbles: true }));
                        }
                        await wacht(1500);
                        const rij2 = vindRij();
                        res.nogOngelezen = !!(rij2 && [...rij2.querySelectorAll('span')].some(s =>
                            s.offsetParent !== null &&
                            (/^\d+$/.test((s.textContent || '').trim()) ||
                             /ongelezen|unread|non lu/i.test(s.getAttribute('aria-label') || ''))));
                        window.__wmWaGelezen = res;
                    })();
                    return true;
                })()
                """);
            var stand = "null";
            for (var i = 0; i < 60; i++) // de job scrolt en wacht zelf: ruim de tijd geven
            {
                await Task.Delay(300, ct);
                var klaar = await JsAsync("JSON.stringify(window.__wmWaGelezen)");
                if (klaar is not ("null" or "\"null\""))
                {
                    stand = klaar;
                    break;
                }
            }
            await Task.Delay(1000, ct); // de leesbevestiging nog even laten vertrekken
            Log($"resultaat: {stand}");
            if (stand.Contains("rij-niet-gevonden"))
            {
                throw new InvalidOperationException($"Chat \"{naam}\" niet gevonden in de WhatsApp-lijst.");
            }
            // Eerlijk falen: als de rij na afloop nog een ongelezen-badge toont, is de
            // leesbevestiging niet vertrokken — dan moet de aanroeper dat ook zo melden.
            if (stand.Replace("\\\"", "\"").Contains("\"nogOngelezen\":true"))
            {
                throw new InvalidOperationException(
                    $"Chat \"{naam}\" staat na de poging nog als ongelezen in WhatsApp.");
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Leest de ongelezen chats via WhatsApps eigen filterknop: chip "Ongelezen" aanklikken,
    /// de gefilterde rijen lezen en de chip "Alles" terugzetten. Null als de chips niet
    /// gevonden worden (dan valt de aanroeper terug op badge-detectie).
    /// </summary>
    private async Task<(int Totaal, List<WaChat> Chats)?> OngelezenViaFilterAsync(CancellationToken ct)
    {
        var totaalJson = await JsAsync(
            """
            (function () {
                const rows = [...document.querySelectorAll(
                    '#pane-side [role="listitem"], #pane-side [role="row"]')]
                    .filter(r => r.querySelector('span[title]'));
                return rows.length;
            })()
            """);
        if (!int.TryParse(totaalJson, out var totaal) || totaal == 0)
        {
            return null; // lijst (nog) niet gerenderd
        }
        var chipGeklikt = await JsAsync(
            """
            (function () {
                const chip = [...document.querySelectorAll('button, [role="tab"]')]
                    .find(b => /^ongelezen|^unread|^non lus?/i.test((b.textContent || '').trim()));
                if (!chip) return false;
                chip.click();
                return true;
            })()
            """);
        if (chipGeklikt != "true")
        {
            return null;
        }
        await Task.Delay(800, ct);
        var json = await JsAsync(
            """
            (function () {
                const res = [];
                for (const r of document.querySelectorAll(
                    '#pane-side [role="listitem"], #pane-side [role="row"]')) {
                    const spans = [...r.querySelectorAll('span[title]')];
                    const naam = spans[0]?.getAttribute('title') || '';
                    if (!naam) continue;
                    let preview = spans[1]?.getAttribute('title') || '';
                    if (!preview) {
                        let t = (r.textContent || '').replace(/\s+/g, ' ').trim();
                        if (t.startsWith(naam)) t = t.slice(naam.length);
                        t = t.replace(/\b\d{1,2}:\d{2}\b/, '');
                        preview = t.trim();
                    }
                    res.push({ naam, preview: preview.slice(0, 300) });
                }
                return res;
            })()
            """);
        await JsAsync(
            """
            (function () {
                const chip = [...document.querySelectorAll('button, [role="tab"]')]
                    .find(b => /^alles|^all\b|^tou(s|tes)/i.test((b.textContent || '').trim()));
                if (chip) chip.click();
            })()
            """);
        using var doc = JsonDocument.Parse(json);
        var chats = doc.RootElement.EnumerateArray()
            .Select(e => new WaChat(
                e.GetProperty("naam").GetString() ?? "",
                e.GetProperty("preview").GetString() ?? ""))
            .Where(c => c.Naam.Length > 0)
            .ToList();
        try
        {
            File.WriteAllText(Path.Combine(DataDir, "wa-debug.json"),
                JsonSerializer.Serialize(new { totaal, viaFilter = true, chats }));
        }
        catch
        {
            // Alleen diagnose.
        }
        return (totaal, chats);
    }

    private sealed record Regel(
        string Pre, bool Uitgaand, string Tekst, string Beeld = "", string Reacties = "");

    private async Task<List<Regel>> OpenEnLeesAsync(string naam, CancellationToken ct)
    {
        await OpenChatAsync(naam, ct);

        // Asynchrone verzamel-job in de pagina: foto's in bubbels zijn blob-URL's die
        // per stuk opgehaald en naar data-URL's omgezet worden — dat kost even, dus het
        // resultaat komt in window.__wmWaMsgs en wordt hieronder gepolld.
        await JsAsync(
            """
            (function () {
                window.__wmWaMsgs = null;
                (async () => {
                  try {
                    let rijen = [...document.querySelectorAll('#main .message-in, #main .message-out')];
                    if (rijen.length === 0) {
                        // Fallback voor nieuwere WhatsApp-DOM's: bubbels herkennen aan de
                        // copyable-text met het pre-plain-text-attribuut.
                        rijen = [...document.querySelectorAll('#main [data-pre-plain-text]')]
                            .map(c => c.closest('[role="row"], [data-id]') || c);
                    }
                    const mainRect = document.querySelector('#main').getBoundingClientRect();
                    const msgs = [];
                    let fotoBudget = 12; // niet eindeloos blobben bij een fotoreeks
                    // Totaalcap: de uiteindelijke HTML gaat via NavigateToString (limiet
                    // ±1,5 MB); daarboven zou de hele bubbelweergave wegvallen.
                    let fotoTekens = 0;
                    for (const m of rijen.slice(-25)) {
                        const c = m.querySelector('[data-pre-plain-text]') ||
                            (m.hasAttribute && m.hasAttribute('data-pre-plain-text') ? m : null);
                        // Geneste treffers ontdubbelen: anders staat elke tekst er dubbel in.
                        let delen = [...m.querySelectorAll('span.selectable-text')];
                        if (delen.length === 0) delen = [...m.querySelectorAll('.copyable-text span')];
                        delen = delen.filter(s => !delen.some(o => o !== s && o.contains(s)));
                        // Richting, van sterk naar zwak signaal: de message-in/out-class,
                        // het data-id (begint bij eigen berichten met "true_", inkomend
                        // "false_"), en anders de positie — eigen bubbels staan rechts van
                        // het midden van het gesprek, wat ook nieuwe DOM's overleeft.
                        const dataId = (m.getAttribute && m.getAttribute('data-id')) ||
                            m.closest('[data-id]')?.getAttribute('data-id') ||
                            m.querySelector('[data-id]')?.getAttribute('data-id') || '';
                        let uit;
                        if (m.matches('.message-out, .message-out *') ||
                            m.querySelector('.message-out')) uit = true;
                        else if (m.matches('.message-in, .message-in *') ||
                            m.querySelector('.message-in')) uit = false;
                        else if (/^(true|false)_/.test(dataId)) uit = dataId.startsWith('true_');
                        else {
                            const rect = (c || delen[0] || m).getBoundingClientRect();
                            uit = rect.width > 0 &&
                                rect.left + rect.width / 2 > mainRect.left + mainRect.width / 2;
                        }
                        // Foto in de bubbel. WhatsApp gebruikt afwisselend blob:-URL's,
                        // ingebedde data:-thumbnails en https-media; alle drie meenemen.
                        // Emoji's en pictogrammen vallen af op formaat.
                        let beeld = '';
                        const kandidaten = [...m.querySelectorAll('img')].filter(i => {
                            const src = i.src || i.currentSrc || '';
                            if (!/^(blob:|data:image|https:)/.test(src)) return false;
                            const b = i.getBoundingClientRect();
                            const breed = i.naturalWidth || i.clientWidth || b.width;
                            const hoog = i.naturalHeight || i.clientHeight || b.height;
                            return breed >= 50 && hoog >= 50;
                        });
                        // De grootste kandidaat: bij een bubbel met thumbnail + volle foto
                        // levert dat de scherpste.
                        const img = kandidaten.sort((a, b) =>
                            (b.naturalWidth || b.clientWidth) - (a.naturalWidth || a.clientWidth))[0];
                        if (img && fotoBudget > 0) {
                            try {
                                // In beeld brengen: WhatsApp laadt afbeeldingen pas als de
                                // bubbel zichtbaar is (lazy loading), anders blijft src leeg.
                                m.scrollIntoView({ block: 'center' });
                                await new Promise(r => setTimeout(r, 120));
                                if (!img.complete || !img.naturalWidth) {
                                    await new Promise(r => {
                                        const klaar = () => r();
                                        img.addEventListener('load', klaar, { once: true });
                                        img.addEventListener('error', klaar, { once: true });
                                        setTimeout(klaar, 1200);
                                    });
                                }
                                // Via canvas: meteen verkleinen naar maximaal 900 px breed en
                                // als JPEG opslaan. Zo passen grote foto's alsnog binnen de
                                // limiet in plaats van dat ze wegvallen.
                                const bron = img.currentSrc || img.src;
                                const bitmap = await new Promise((res, rej) => {
                                    const el = new Image();
                                    el.crossOrigin = 'anonymous';
                                    el.onload = () => res(el);
                                    el.onerror = rej;
                                    el.src = bron;
                                });
                                const schaal = Math.min(1, 900 / (bitmap.naturalWidth || 900));
                                const canvas = document.createElement('canvas');
                                canvas.width = Math.max(1, Math.round((bitmap.naturalWidth || 1) * schaal));
                                canvas.height = Math.max(1, Math.round((bitmap.naturalHeight || 1) * schaal));
                                canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
                                const uit = canvas.toDataURL('image/jpeg', 0.72);
                                if (uit.length > 200 && fotoTekens + uit.length <= 2500000) {
                                    beeld = uit;
                                    fotoTekens += uit.length;
                                    fotoBudget--;
                                }
                            } catch {
                                // Canvas geblokkeerd (cross-origin) of laden mislukt: dan
                                // alsnog de ruwe blob proberen, dat werkt voor eigen media.
                                try {
                                    const resp = await fetch(img.currentSrc || img.src);
                                    const blob = await resp.blob();
                                    if (blob.size <= 900000) {
                                        const dataUrl = await new Promise(res => {
                                            const fr = new FileReader();
                                            fr.onload = () => res(String(fr.result));
                                            fr.onerror = () => res('');
                                            fr.readAsDataURL(blob);
                                        });
                                        if (dataUrl && fotoTekens + dataUrl.length <= 2500000) {
                                            beeld = dataUrl;
                                            fotoTekens += dataUrl.length;
                                            fotoBudget--;
                                        }
                                    }
                                } catch { /* geen foto: de tekst volstaat */ }
                            }
                        }
                        // Emoji-reacties (❤️ 👍 …) onder de bubbel: WhatsApp toont ze als een
                        // knopje met een aria-label ("2 reacties in totaal, …") en de emoji's
                        // zelf als <img alt="❤️">. De hover-knop "Reageren"/"React" matcht
                        // hier bewust niet (die bevat "reactie"/"reaction" niet).
                        let reacties = '';
                        const rEl = m.querySelector(
                            '[aria-label*="reactie" i], [aria-label*="reaction" i], ' +
                            '[aria-label*="réaction" i], [data-testid*="reaction"]');
                        if (rEl) {
                            reacties = [...rEl.querySelectorAll('img')]
                                .map(i => i.alt || '').filter(Boolean).join('');
                            if (!reacties) {
                                // Nieuwere DOM zonder emoji-img's: pak de pictogrammen uit
                                // het label of de tekst van het knopje zelf.
                                const bron = (rEl.getAttribute('aria-label') || '') + ' ' +
                                    (rEl.textContent || '');
                                reacties = [...bron.matchAll(/\p{Extended_Pictographic}/gu)]
                                    .map(x => x[0]).join('');
                            }
                            // Totaal (bv. "❤️ 3") erbij zodra er meer reacties dan emoji's zijn.
                            const totaal = parseInt((rEl.getAttribute('aria-label') || '')
                                .match(/\d+/)?.[0] || '', 10);
                            if (reacties && totaal > 1) { reacties += ' ' + totaal; }
                        }
                        msgs.push({
                            pre: c ? c.getAttribute('data-pre-plain-text') : '',
                            uit,
                            txt: delen.map(s => s.innerText).join(' ').trim(),
                            beeld,
                            reacties,
                        });
                    }
                    const gevuld = msgs.filter(m => m.txt || m.beeld);
                    window.__wmWaMsgs = gevuld.length > 0 ? gevuld : { leeg: true, diag: {
                        msgIn: document.querySelectorAll('#main .message-in').length,
                        prePlain: document.querySelectorAll('#main [data-pre-plain-text]').length,
                        rows: document.querySelectorAll('#main [role="row"]').length,
                        main: !!document.querySelector('#main'),
                        copyable: document.querySelectorAll('#main .copyable-text').length,
                        titel: document.querySelector('#main header span[title]')?.getAttribute('title') || '',
                    } };
                  } catch (e) {
                    window.__wmWaMsgs = { leeg: true, diag: { fout: String(e).slice(0, 200) } };
                  }
                })();
                return true;
            })()
            """);
        var json = "null";
        for (var i = 0; i < 40; i++) // foto's omzetten kan even duren (max. ~12 s)
        {
            await Task.Delay(300, ct);
            var klaar = await JsAsync("JSON.stringify(window.__wmWaMsgs)");
            if (klaar is not ("null" or "\"null\""))
            {
                json = JsonSerializer.Deserialize<string>(klaar) ?? "null";
                break;
            }
        }
        if (json == "null")
        {
            throw new InvalidOperationException("Berichten uitlezen bleef hangen (geen resultaat).");
        }
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            // Diagnose-object: geen berichten gevonden — meld wát er dan wél in de DOM staat.
            throw new InvalidOperationException(
                $"0 berichten; DOM-stand: {doc.RootElement.GetProperty("diag").GetRawText()}");
        }
        return doc.RootElement.EnumerateArray()
            .Select(m => new Regel(
                m.GetProperty("pre").GetString() ?? "",
                m.GetProperty("uit").GetBoolean(),
                m.GetProperty("txt").GetString() ?? "",
                m.TryGetProperty("beeld", out var b) ? b.GetString() ?? "" : "",
                m.TryGetProperty("reacties", out var re) ? re.GetString() ?? "" : ""))
            .ToList();
    }

    private async Task OpenChatAsync(string naam, CancellationToken ct)
    {
        // Drie klikstrategieën na elkaar: WhatsApp verandert geregeld welke events het
        // accepteert (rij-klik, klik op de titel, of toetsenbord-Enter op de rij).
        for (var strategie = 0; strategie < 3; strategie++)
        {
            var geklikt = await JsAsync(
                $$"""
                (function () {
                    const naam = {{JsonSerializer.Serialize(naam)}};
                    const strategie = {{strategie}};
                    const rows = [...document.querySelectorAll(
                        '#pane-side [role="listitem"], #pane-side [role="row"]')];
                    for (const r of rows) {
                        const t = r.querySelector('span[title]');
                        if (!t || t.getAttribute('title') !== naam) continue;
                        const rij = t.closest('[role="listitem"], [role="row"]') || t;
                        rij.scrollIntoView({ block: 'center' });
                        const doel = strategie === 1 ? t : rij;
                        if (strategie === 2) {
                            rij.setAttribute('tabindex', '0');
                            rij.focus();
                            for (const type of ['keydown', 'keyup']) {
                                rij.dispatchEvent(new KeyboardEvent(type, { key: 'Enter',
                                    code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                            }
                            return 'ok';
                        }
                        const b = doel.getBoundingClientRect();
                        const opts = { bubbles: true, cancelable: true, view: window,
                            clientX: b.x + b.width / 2, clientY: b.y + b.height / 2, buttons: 1 };
                        for (const type of ['pointerover', 'mouseover', 'pointerdown', 'mousedown',
                                            'pointerup', 'mouseup', 'click']) {
                            doel.dispatchEvent(type.startsWith('pointer')
                                ? new PointerEvent(type, opts) : new MouseEvent(type, opts));
                        }
                        return 'ok';
                    }
                    return 'niet gevonden';
                })()
                """);
            if (geklikt != "\"ok\"")
            {
                throw new InvalidOperationException($"Chat \"{naam}\" niet gevonden in de lijst.");
            }

            for (var i = 0; i < 10; i++) // wachten tot het júiste gesprek geladen is
            {
                await Task.Delay(400, ct);
                // Niet alleen "er staat een chat open" checken maar ook de kop: anders lezen
                // we bij een mislukte klik gewoon de vorige (verkeerde) chat uit.
                if (await JsAsync(
                    $$"""
                    (function () {
                        if (!document.querySelector('#main footer, #main [contenteditable="true"]')) return false;
                        // De kop heeft niet altijd meer een title-attribuut: op de koptekst
                        // zelf controleren dat de juiste chat openstaat. Emoji's in de naam
                        // rendert WhatsApp in de kop als <img>, dus die ontbreken in
                        // textContent — vergelijk daarom zonder emoji's en zonder
                        // onzichtbare richtingstekens (die in namen uit de zijbalk sluipen).
                        const kop = document.querySelector('#main header');
                        if (!kop) return false;
                        const schoon = s => s.replace(/[^\p{L}\p{N}\p{P}\p{Zs}]/gu, '')
                            .replace(/\s+/g, ' ').trim();
                        const doel = schoon({{JsonSerializer.Serialize(naam)}});
                        return doel.length >= 2
                            ? schoon(kop.textContent || '').includes(doel)
                            : (kop.textContent || '').length > 0;
                    })()
                    """) == "true")
                {
                    await Task.Delay(500, ct); // berichten laten renderen
                    return;
                }
            }
        }
        var diag = await JsAsync(
            """
            JSON.stringify({
                main: !!document.querySelector('#main'),
                titel: (document.querySelector('#main header')?.textContent || '').slice(0, 60),
                paneel: !!document.querySelector('#pane-side'),
            })
            """);
        throw new TimeoutException($"Chat \"{naam}\" laadde niet (of de verkeerde bleef open). Stand: {diag}");
    }

    // ---------- Versturen ----------

    /// <summary>Opent de chat en verstuurt het bericht via het invoerveld van WhatsApp Web.</summary>
    public async Task VerstuurAsync(string naam, string tekst, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            await VerstuurKernAsync(naam, tekst, ct);
        }
        finally
        {
            _slot.Release();
        }
    }

    private async Task VerstuurKernAsync(string naam, string tekst, CancellationToken ct)
    {
        if (!await StartAsync(ct))
        {
            throw new InvalidOperationException("WhatsApp Web is niet ingelogd.");
        }
        await OpenChatAsync(naam, ct);

        var resultaat = await JsAsync(
            $$"""
            (function () {
                const box = document.querySelector('#main footer div[contenteditable="true"]');
                if (!box) return 'geen invoerveld';
                box.focus();
                document.execCommand('insertText', false, {{JsonSerializer.Serialize(tekst)}});
                box.dispatchEvent(new InputEvent('input', { bubbles: true }));
                return 'ok';
            })()
            """);
        if (resultaat != "\"ok\"")
        {
            throw new InvalidOperationException("Invoerveld van WhatsApp niet gevonden.");
        }

        await Task.Delay(400, ct); // WhatsApp de invoer laten verwerken
        await JsAsync(
            """
            (function () {
                const box = document.querySelector('#main footer div[contenteditable="true"]');
                box.dispatchEvent(new KeyboardEvent('keydown',
                    { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true }));
                return 'ok';
            })()
            """);
        await Task.Delay(600, ct);
    }

    // ---------- Hulpjes ----------

    /// <summary>
    /// Live DOM-zelftest: staat de zijbalk er nog en levert de rij-selector chats op?
    /// (De zijbalk toont altijd chats, dus nul rijen betekent hier échte selector-drift.)
    /// Leeg = in orde; anders een omschrijving van wat er ontbreekt.
    /// </summary>
    /// <summary>Uitkomst van de diagnose: een eventuele voorbeeldfoto als data-URL.</summary>
    public sealed record DiagnoseUitslag(string Voorbeeld);

    /// <summary>
    /// Onderzoekt de huidige stand van WhatsApp Web: ingelogd, hoeveel chatrijen, en wat er
    /// in een geopend gesprek aan afbeeldingen te vinden is (welke bronnen, welke formaten,
    /// en of een foto echt naar een data-URL om te zetten valt). Opent bewust een chat
    /// zónder ongelezen berichten, zodat er geen leesbevestiging vertrekt.
    /// </summary>
    public async Task<DiagnoseUitslag> DiagnoseAsync(Action<string> log, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 25))
            {
                log("Niet ingelogd — er is een QR-scan nodig ('WhatsApp koppelen…').");
                return new DiagnoseUitslag("");
            }
            log("Ingelogd, chatlijst staat er.");

            var lijstJson = await JsAsync(
                """
                JSON.stringify([...document.querySelectorAll(
                    '#pane-side [role="listitem"], #pane-side [role="row"]')]
                    .map(r => ({
                        naam: r.querySelector('span[title]')?.getAttribute('title') || '',
                        ongelezen: [...r.querySelectorAll('span')].some(s =>
                            (/^\d+$/.test(s.textContent.trim()) && s.getAttribute('aria-label')) ||
                            /ongelezen|unread|non lu/i.test(s.getAttribute('aria-label') || '')),
                    })).filter(r => r.naam))
                """);
            // Let op: System.Text.Json matcht standaard hoofdlettergevoelig, en de sleutels
            // uit het script zijn kleine letters. Daarom expliciet uitlezen.
            using var lijstDoc = JsonDocument.Parse(
                JsonSerializer.Deserialize<string>(lijstJson) ?? "[]");
            var rijen = lijstDoc.RootElement.EnumerateArray()
                .Select(r => new Rij(
                    r.GetProperty("naam").GetString() ?? "",
                    r.GetProperty("ongelezen").GetBoolean()))
                .Where(r => r.Naam.Length > 0)
                .ToList();
            log($"Chatrijen in de zijbalk: {rijen.Count}, waarvan ongelezen: " +
                $"{rijen.Count(r => r.Ongelezen)}.");
            if (rijen.Count == 0)
            {
                log("De rij-selector vindt niets — WhatsApp heeft vermoedelijk zijn DOM gewijzigd.");
                return new DiagnoseUitslag("");
            }

            // Een chat zonder ongelezen berichten: openen kost dan geen blauwe vinkjes.
            var doel = rijen.FirstOrDefault(r => !r.Ongelezen)?.Naam ?? rijen[0].Naam;
            log($"Gesprek openen om afbeeldingen te onderzoeken: \"{doel}\".");
            await OpenChatAsync(doel, ct);

            var beeldJson = await JsAsync(
                """
                (function () {
                    const imgs = [...document.querySelectorAll('#main img')];
                    const tel = { blob: 0, data: 0, https: 0, klein: 0, groot: 0 };
                    for (const i of imgs) {
                        const src = i.currentSrc || i.src || '';
                        if (/^blob:/.test(src)) tel.blob++;
                        else if (/^data:/.test(src)) tel.data++;
                        else if (/^https:/.test(src)) tel.https++;
                        const breed = i.naturalWidth || i.clientWidth;
                        if (breed >= 50) tel.groot++; else tel.klein++;
                    }
                    return JSON.stringify({
                        bubbels: document.querySelectorAll('#main .message-in, #main .message-out').length,
                        prePlain: document.querySelectorAll('#main [data-pre-plain-text]').length,
                        afbeeldingen: imgs.length,
                        tel,
                        downloadKnoppen: document.querySelectorAll(
                            '#main [data-icon="media-download"], #main [data-icon="audio-download"]').length,
                    });
                })()
                """, tijdslimietSeconden: 15);
            log("DOM-stand: " + (JsonSerializer.Deserialize<string>(beeldJson) ?? beeldJson));

            // En nu de echte proef: dezelfde route als de fetch, één foto omzetten.
            var berichten = await OpenEnLeesAsync(doel, ct);
            var metFoto = berichten.Count(b => b.Beeld.Length > 0);
            log($"Berichten uitgelezen: {berichten.Count}, met foto: {metFoto}.");
            var voorbeeld = berichten.LastOrDefault(b => b.Beeld.Length > 0)?.Beeld ?? "";
            if (voorbeeld.Length > 0)
            {
                log($"Grootte van de omgezette foto: {voorbeeld.Length / 1024} kB (data-URL).");
            }
            return new DiagnoseUitslag(voorbeeld);
        }
        finally
        {
            _slot.Release();
        }
    }

    private sealed record Rij(string Naam, bool Ongelezen);

    public async Task<string> ZelftestAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 10))
            {
                return ""; // niet ingelogd: geen DOM-oordeel mogelijk
            }
            var rijen = await JsAsync(
                "document.querySelectorAll('#pane-side [role=\"listitem\"], " +
                "#pane-side [role=\"row\"]').length");
            return int.TryParse(rijen, out var n) && n > 0
                ? ""
                : "WhatsApp: chatrij-selector vindt geen rijen in de zijbalk";
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Voert een script uit in de pagina. Met tijdslimiet: een vastgelopen renderer liet de
    /// hele poll anders eeuwig hangen (ExecuteScriptAsync keert dan nooit terug). Bij een
    /// time-out wordt de sessie gemarkeerd voor een verse start.
    /// </summary>
    private async Task<string> JsAsync(string script, int tijdslimietSeconden = 20)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("WhatsApp-sessie is niet gestart.");
        }
        try
        {
            var taak = core.ExecuteScriptAsync(script);
            if (await Task.WhenAny(taak, Task.Delay(TimeSpan.FromSeconds(tijdslimietSeconden)))
                != taak)
            {
                _gecrasht = true;
                throw new TimeoutException(
                    $"WhatsApp Web reageerde niet binnen {tijdslimietSeconden} s; " +
                    "de sessie wordt bij de volgende synchronisatie opnieuw gestart.");
            }
            return await taak;
        }
        catch (Exception ex) when (ex.Message.Contains("no longer valid",
            StringComparison.OrdinalIgnoreCase))
        {
            // Browserproces onderweg gecrasht (ProcessFailed vuurt niet altijd eerst):
            // markeren zodat de volgende beurt de sessie vers opbouwt.
            _gecrasht = true;
            throw new InvalidOperationException(
                "De WhatsApp-browser is gecrasht en wordt bij de volgende synchronisatie " +
                "automatisch opnieuw gestart.", ex);
        }
    }

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
    }

    public void Dispose()
    {
        _web?.Dispose();
        _venster?.Dispose(); // Dispose passeert de FormClosing-annulering
    }
}
