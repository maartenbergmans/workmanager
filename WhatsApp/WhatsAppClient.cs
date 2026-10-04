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
    /// Zag de laatste start een ingelogde chatlijst? WhatsApp Web logt gekoppelde apparaten
    /// na ±14 dagen inactiviteit (of via de telefoon) uit; dan is een nieuwe QR-scan nodig
    /// en toont de cockpit de knop "WhatsApp koppelen…".
    /// </summary>
    public static bool Aangemeld { get; private set; }

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
            _venster = new StilVenster
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
                Aangemeld = true;
                return true;
            }
            await Task.Delay(500, ct);
        }
        Aangemeld = false;
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
                Aangemeld = true;
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
                    MessageId = "wa:" + naam + ":" + laatste.Pre + Kort(laatste.B.Tekst, 40),
                    Van = naam,
                    VanAdres = "whatsapp",
                    Onderwerp = laatste.B.Tekst.Length > 0 ? Kort(laatste.B.Tekst, 80) : "📷 foto",
                    Datum = DateTimeOffset.Now,
                    Tekst = string.Join("\n", berichten.Select(r =>
                        (r.B.Uitgaand ? "[eerder] Maarten (ikzelf): " : r.Pre.Length > 0 ? r.Pre : $"{naam}: ") +
                        TranscriptTekst(r.B))),
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
                await WebLimiet.MetLimietAsync(
                    _web!.CoreWebView2!.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, beeld),
                    WebLimiet.Invoer, "schermafdruk", CrashLog, Herstel(false));
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
        string Reacties = "")
    {
        /// <summary>Alleen het uur ("12:30"), zoals WhatsApp het in de bubbel toont.</summary>
        public string Klok { get; init; } = "";
        /// <summary>Datum in WhatsApps eigen vorm "13/9/2026" (voor de datumchips).</summary>
        public string Datum { get; init; } = "";
        /// <summary>Kleur van de afzendernaam in een groep (CSS, bv. "rgb(184, 5, 49)").</summary>
        public string Kleur { get; init; } = "";
        /// <summary>Telefoonnummer als de afzender geen bewaard contact is.</summary>
        public string Nummer { get; init; } = "";
        /// <summary>De naam is de eigen profielnaam van de afzender (WhatsApp: "~ Kathleen").</summary>
        public bool Profielnaam { get; init; }
        /// <summary>Groepslabel van het lid, bv. "Mama Gabrielle".</summary>
        public string Label { get; init; } = "";
        /// <summary>Profielfoto van de afzender (data-URL), leeg = initialen.</summary>
        public string AvatarUrl { get; init; } = "";
        /// <summary>Zelfde afzender als het bericht erboven: geen naam, avatar of staartje.</summary>
        public bool Vervolg { get; init; }
        /// <summary>"Doorgestuurd" / "Vaak doorgestuurd" (leeg als het niet doorgestuurd is).</summary>
        public string Doorgestuurd { get; init; } = "";
        public bool Bewerkt { get; init; }
        /// <summary>Uitgaand: gelezen / afgeleverd / verzonden / wachtend.</summary>
        public string Status { get; init; } = "";
        public string CitaatKleur { get; init; } = "";
        /// <summary>Miniatuur (data-URL) als het geciteerde bericht een foto/video is.</summary>
        public string CitaatBeeld { get; init; } = "";
        public WaLink? Link { get; init; }
        /// <summary>Extra beelden van een album (na <see cref="Beeld"/>), plus hoeveel er nog meer zijn.</summary>
        public List<string> Album { get; init; } = [];
        public int AlbumMeer { get; init; }
        /// <summary>Soort bijzonder bericht: video, audio, document, sticker, oproep, verwijderd,
        /// poll, systeem of overig (leeg = gewone tekst/foto).</summary>
        public string Media { get; init; } = "";
        public string MediaInfo { get; init; } = "";
        public WaPoll? Poll { get; init; }

        /// <summary>Afzender zoals een lezer (of Claude) hem moet zien, met nummer erbij.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string AfzenderVolledig => Nummer.Length > 0 && Nummer != Afzender
            ? $"{Afzender} ({Nummer})" : Afzender;
    }

    public sealed record WaLink(string Titel, string Omschrijving, string Domein, string Beeld, bool Groot);

    public sealed record WaPoll(string Vraag, bool Meerdere, List<WaPollOptie> Opties);

    public sealed record WaPollOptie(string Tekst, int Stemmen, bool Gekozen);

    /// <summary>
    /// De laatste berichten uit een chat, gestructureerd (tijd, afzender, richting, tekst)
    /// voor de bubbelweergave. Let op: hiervoor wordt de chat geopend, dus WhatsApp
    /// markeert hem als gelezen (blauwe vinkjes).
    /// </summary>
    public async Task<(List<WaBericht> Berichten, string AvatarDataUrl, string Ondertitel)>
        LaatsteBerichtenAsync(string naam, int max, CancellationToken ct)
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
                .Select(r => r.B)
                .ToList();
            return (berichten, await AvatarDataUrlAsync(ct), await OndertitelAsync());
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Tekst van een bericht voor transcripten (Claude, cockpitlijst): media, polls en
    /// oproepen krijgen een leesbare omschrijving, reacties komen erachter.
    /// </summary>
    public static string TranscriptTekst(WaBericht b)
    {
        var soort = b.Media switch
        {
            "video" => "🎥 video",
            "audio" => "🎤 spraakbericht",
            "document" => "📄 document",
            "sticker" => "sticker",
            "oproep" => "📞 oproep",
            "verwijderd" => "🚫 verwijderd bericht",
            _ => "",
        };
        var delen = new List<string>();
        if (b.Doorgestuurd.Length > 0)
        {
            delen.Add($"[{b.Doorgestuurd.ToLowerInvariant()}]");
        }
        if (soort.Length > 0)
        {
            delen.Add(b.MediaInfo.Length > 0 ? $"[{soort}: {b.MediaInfo}]" : $"[{soort}]");
        }
        else if (b.Media == "overig" && b.MediaInfo.Length > 0)
        {
            delen.Add($"[{b.MediaInfo}]");
        }
        if (b.Beeld.Length > 0 && b.Media is "" or "sticker")
        {
            delen.Add(b.Album.Count + b.AlbumMeer > 0
                ? $"[📷 {1 + b.Album.Count + b.AlbumMeer} foto's]" : "[📷 foto]");
        }
        if (b.Tekst.Length > 0)
        {
            delen.Add(b.Tekst);
        }
        if (b.Link is { Titel.Length: > 0 } link)
        {
            delen.Add($"[link: {link.Titel}]");
        }
        if (b.Reacties.Length > 0)
        {
            delen.Add($"[reactie: {b.Reacties}]");
        }
        return string.Join(" ", delen);
    }

    /// <summary>
    /// De regel onder de chatnaam in WhatsApps kop: deelnemers van een groep ("Donna, Hilke,
    /// …") of "laatst gezien …". Die verschijnt pas even na het openen; de tijdelijke tekst
    /// "klik hier voor groepsinformatie" telt niet.
    /// </summary>
    private async Task<string> OndertitelAsync()
    {
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var json = await JsAsync(
                    "(document.querySelector('#main [data-testid=\"chat-subtitle\"]')?.textContent || '').trim()");
                var tekst = JsonSerializer.Deserialize<string>(json) ?? "";
                if (tekst.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(tekst,
                        "klik hier|click here|cliquez|typt|typing|écrit",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    return tekst;
                }
                await Task.Delay(400);
            }
        }
        catch
        {
            // De kop is comfort.
        }
        return "";
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

    /// <summary>Een uitgelezen bericht plus de ruwe berichtkop (voor sleutels en transcripten).</summary>
    private sealed record Regel(string Pre, WaBericht B);

    /// <summary>
    /// JS-helper window.__wmWaTekst(el): de tekst van een bubbel zoals WhatsApp hem toont.
    /// innerText volstaat niet: emoji's zijn &lt;img alt="🥐"&gt; (vallen weg), elke regel is
    /// een blok-span die zelf al een "\n" bevat (dubbele witregels), lijsten zijn ul/li en
    /// opmaak zit in stijlen. Opmaak komt terug als WhatsApp-markup (*vet*, _cursief_,
    /// ~doorgehaald~, `code`) — dezelfde vorm als WhatsApps eigen kopiëren — en wordt in de
    /// bubbelweergave weer als opmaak gerenderd.
    /// </summary>
    private const string WaTekstJs =
        """
        window.__wmWaTekst = function (root) {
            const stijl = n => { try { return getComputedStyle(n); } catch { return null; } };
            // Markering om de witruimte heen leggen: "*vet *" rendert WhatsApp niet.
            const pak = (s, m) => {
                const k = s.match(/^([\s\x01]*)([\s\S]*?)([\s\x01]*)$/);
                return k[2] ? k[1] + m + k[2] + m + k[3] : s;
            };
            const REGEL = '\x01'; // "hier moet een nieuwe regel beginnen"
            const tekst = (n, ps) => {
                if (n.nodeType === 3) return n.nodeValue;
                if (n.nodeType !== 1) return '';
                const tag = n.tagName;
                if (tag === 'IMG') return n.getAttribute('data-plain-text') || n.alt || '';
                if (tag === 'BR') return '\n';
                if (tag === 'SCRIPT' || tag === 'STYLE' || tag === 'svg' ||
                    n.matches('.read-more-button, [data-testid="caption-read-more-button"]')) return '';
                const st = stijl(n);
                const kinderen = [...n.childNodes].filter(k =>
                    !((tag === 'UL' || tag === 'OL') && k.nodeType === 3 && !k.nodeValue.trim()));
                let s = kinderen.map(k => tekst(k, st)).join('');
                if (tag === 'LI') {
                    const ol = n.parentElement && n.parentElement.tagName === 'OL';
                    const nr = ol ? ([...n.parentElement.children].indexOf(n) + 1) + '. ' : '• ';
                    return REGEL + nr + s.trim() + '\n';
                }
                if (tag === 'UL' || tag === 'OL') return REGEL + s;
                // Blokelementen (regel-spans, citaatblokken) staan op een eigen regel.
                if (st && /^(block|flex|grid|table)$/.test(st.display)) s = REGEL + s + REGEL;
                // @vermeldingen zijn opgemaakte spans maar geen vetgedrukte tekst.
                if (st && ps && !/^\s*@/.test(s)) {
                    if (tag === 'STRONG' || tag === 'B' ||
                        (+st.fontWeight >= 600 && +ps.fontWeight < 600)) s = pak(s, '*');
                    if (tag === 'EM' || tag === 'I' ||
                        (st.fontStyle === 'italic' && ps.fontStyle !== 'italic')) s = pak(s, '_');
                    if (tag === 'DEL' || tag === 'S' ||
                        (/line-through/.test(st.textDecorationLine) &&
                         !/line-through/.test(ps.textDecorationLine))) s = pak(s, '~');
                    if (tag === 'CODE' ||
                        (/mono/i.test(st.fontFamily) && !/mono/i.test(ps.fontFamily))) s = pak(s, '`');
                }
                return s;
            };
            // Een reeks regelmarkeringen en "\n"'s wordt zoveel regeleinden als er echte
            // "\n"'s in zitten, maar minstens één: zo verdubbelt een blok-span die zelf al
            // op "\n" eindigt niets.
            return tekst(root, root.parentElement ? stijl(root.parentElement) : null)
                .replace(/[\n\x01]*\x01[\n\x01]*/g,
                    m => '\n'.repeat(Math.max(1, m.split('\n').length - 1)))
                .replace(/[ \t]+\n/g, '\n')
                .replace(/\n{3,}/g, '\n\n')
                .trim();
        };
        """;

    private async Task<List<Regel>> OpenEnLeesAsync(string naam, CancellationToken ct)
    {
        await OpenChatAsync(naam, ct);

        // Asynchrone verzamel-job in de pagina: foto's en profielfoto's zijn blob-/CDN-URL's
        // die per stuk opgehaald en naar data-URL's omgezet worden — dat kost even, dus het
        // resultaat komt in window.__wmWaMsgs en wordt hieronder gepolld.
        // Per bericht wordt alles meegenomen wat WhatsApp zelf toont: gekleurde afzender,
        // "~ profielnaam" + nummer, groepslabel, reeks (staartje), doorgestuurd, bewerkt,
        // vinkjes, citaat, linkvoorbeeld, album, poll, oproep, video/spraak/document.
        await JsAsync(WaTekstJs +
            """
            (function () {
                window.__wmWaMsgs = null;
                (async () => {
                  try {
                    const wacht = ms => new Promise(r => setTimeout(r, ms));
                    const T = el => el ? window.__wmWaTekst(el) : '';
                    // Het scrollende berichtenpaneel (de lijst is gevirtualiseerd: alleen wat
                    // in of net buiten beeld staat, staat in de DOM).
                    const paneel = () => {
                        const rij = document.querySelector('#main [role="row"]');
                        for (let e = rij; e && e !== document.body; e = e.parentElement) {
                            const st = getComputedStyle(e);
                            if (/(auto|scroll)/.test(st.overflowY) &&
                                e.scrollHeight > e.clientHeight + 40) return e;
                        }
                        return null;
                    };
                    // WhatsApp bewaart de scrollstand per chat, en onze eigen fotolezer
                    // (scrollIntoView hieronder) laat het paneel bij het oudste beeld achter.
                    // Een klik op een chat die al openstaat zet die stand niet terug, dus
                    // lazen we bij elke ronde weer hetzelfde oude stuk gesprek — nieuwe
                    // berichten kwamen nooit in beeld. Daarom eerst helemaal naar beneden;
                    // dat laadt de nieuwste bubbels bij (vandaar de herhaling).
                    const naarOnder = async () => {
                        const p = paneel();
                        if (!p) return;
                        for (let i = 0; i < 15; i++) {
                            const hoogte = p.scrollHeight;
                            p.scrollTop = hoogte;
                            await wacht(220);
                            if (p.scrollHeight === hoogte &&
                                p.scrollHeight - p.scrollTop - p.clientHeight < 8) return;
                        }
                    };
                    await naarOnder();
                    const zoekRijen = () => {
                        // Elke rij met een bubbel (ook foto's, albums, polls en oproepen zonder
                        // data-pre-plain-text) plus systeemmeldingen (rij met tekst, zonder bubbel).
                        let r = [...document.querySelectorAll('#main [role="row"]')].filter(x =>
                            x.querySelector('[data-testid="msg-container"]') ||
                            (x.textContent || '').trim().length > 0);
                        if (r.length === 0 || !r.some(x => x.querySelector('[data-testid="msg-container"]'))) {
                            // Oudere DOM's: message-in/out-classes, anders de berichtkop
                            // ("[tijd, datum] naam: "; lijstpunten dragen zelf "- ").
                            let oud = [...document.querySelectorAll('#main .message-in, #main .message-out')];
                            if (oud.length === 0) {
                                oud = [...document.querySelectorAll('#main [data-pre-plain-text^="["]')]
                                    .map(c => c.closest('[role="row"], [data-id]') || c);
                            }
                            if (oud.length > 0) r = oud;
                        }
                        return [...new Set(r)].slice(-25);
                    };
                    // Lange berichten zijn ingekort tot je op "Meer lezen" klikt: eerst
                    // uitklappen (heel lange soms in meerdere stappen), anders valt de staart weg.
                    for (let i = 0; i < 5; i++) {
                        const knoppen = zoekRijen().flatMap(m => [...m.querySelectorAll(
                            '[data-testid="caption-read-more-button"], .read-more-button')]);
                        if (knoppen.length === 0) break;
                        for (const k of knoppen) k.click();
                        await wacht(400);
                    }
                    const rijen = zoekRijen();
                    const mainRect = document.querySelector('#main').getBoundingClientRect();
                    const heeftStaarten = rijen.some(r => r.querySelector('[data-icon^="tail-"]'));

                    // Profielfoto's naast de bubbels staan niet ín de rij maar in een eigen laag:
                    // koppelen op hoogte (de avatar staat naast de eerste bubbel van een reeks).
                    // Eerst alles meten — het foto-scrollen hieronder verschuift de boel.
                    const avatars = [...document.querySelectorAll(
                        '#main [data-testid="group-chat-profile-picture"]')].map(a => {
                            const im = a.querySelector('img');
                            return { top: a.getBoundingClientRect().top,
                                     src: im ? (im.currentSrc || im.src || '') : '' };
                        });
                    const avatarVan = new Map();
                    for (const m of rijen) {
                        const staart = m.querySelector('[data-icon="tail-in"]');
                        if (!staart) continue;
                        const top = (m.querySelector('[data-testid="msg-container"]') || m)
                            .getBoundingClientRect().top;
                        let best = null, afstand = 45;
                        for (const a of avatars) {
                            const d = Math.abs(a.top - top);
                            if (d < afstand) { afstand = d; best = a; }
                        }
                        if (best) avatarVan.set(m, best.src);
                    }
                    const avatarCache = new Map();
                    const avatarData = async src => {
                        if (!src) return '';
                        if (avatarCache.has(src)) return avatarCache.get(src);
                        let d = '';
                        try {
                            const bmp = await createImageBitmap(await (await fetch(src)).blob());
                            const cv = document.createElement('canvas');
                            cv.width = cv.height = 64;
                            const z = Math.min(bmp.width, bmp.height);
                            cv.getContext('2d').drawImage(bmp, (bmp.width - z) / 2,
                                (bmp.height - z) / 2, z, z, 0, 0, 64, 64);
                            d = cv.toDataURL('image/jpeg', 0.8);
                        } catch { d = ''; }
                        avatarCache.set(src, d);
                        return d;
                    };

                    // Beeld naar verkleinde JPEG-data-URL. Budget: de uiteindelijke HTML gaat via
                    // NavigateToString (±1,5 MB); daarboven valt de hele bubbelweergave weg.
                    let fotoBudget = 14;
                    let fotoTekens = 0;
                    const beeldData = async (img, m, maxBreed) => {
                        if (!img || fotoBudget <= 0) return '';
                        try {
                            // In beeld brengen: WhatsApp laadt media pas als de bubbel
                            // zichtbaar is (lazy loading), anders blijft src leeg.
                            m.scrollIntoView({ block: 'center' });
                            await wacht(120);
                            if (!img.complete || !img.naturalWidth) {
                                await new Promise(r => {
                                    img.addEventListener('load', () => r(), { once: true });
                                    img.addEventListener('error', () => r(), { once: true });
                                    setTimeout(r, 1200);
                                });
                            }
                            const bron = img.currentSrc || img.src;
                            let bmp;
                            try {
                                // Via fetch → blob: nooit een "besmet" canvas (cross-origin).
                                bmp = await createImageBitmap(await (await fetch(bron)).blob());
                            } catch {
                                bmp = await new Promise((res, rej) => {
                                    const el = new Image();
                                    el.crossOrigin = 'anonymous';
                                    el.onload = () => res(el);
                                    el.onerror = rej;
                                    el.src = bron;
                                });
                            }
                            const w = bmp.naturalWidth || bmp.width, h = bmp.naturalHeight || bmp.height;
                            const schaal = Math.min(1, maxBreed / (w || maxBreed));
                            const cv = document.createElement('canvas');
                            cv.width = Math.max(1, Math.round(w * schaal));
                            cv.height = Math.max(1, Math.round(h * schaal));
                            cv.getContext('2d').drawImage(bmp, 0, 0, cv.width, cv.height);
                            const d = cv.toDataURL('image/jpeg', 0.72);
                            if (d.length > 200 && fotoTekens + d.length <= 1100000) {
                                fotoTekens += d.length;
                                fotoBudget--;
                                return d;
                            }
                        } catch { /* geen beeld: de rest van het bericht volstaat */ }
                        return '';
                    };
                    const grootste = (a, b) =>
                        (b.naturalWidth || b.clientWidth) - (a.naturalWidth || a.clientWidth);
                    const diepKleur = el => {
                        let d = el;
                        while (d.firstElementChild &&
                            ![...d.childNodes].some(n => n.nodeType === 3 && n.nodeValue.trim())) {
                            d = d.firstElementChild;
                        }
                        return getComputedStyle(d).color;
                    };

                    const msgs = [];
                    // Nieuwste eerst verwerken: raakt het fotobudget op, dan missen de oudste
                    // berichten hun beeld en niet de nieuwste. Achteraf terugdraaien.
                    for (const m of [...rijen].reverse()) {
                        const cont = m.querySelector('[data-testid="msg-container"]');
                        const oudeDom = m.matches('.message-in, .message-out') ||
                            m.querySelector('.message-in, .message-out, [data-pre-plain-text]');
                        if (!cont && !oudeDom) {
                            // Systeemmelding ("X heeft Y toegevoegd", beveiligingscode, …).
                            const t = T(m).replace(/\s*\n\s*/g, ' ').trim();
                            if (t) msgs.push({ systeem: true, uit: false, pre: '', txt: t.slice(0, 300) });
                            continue;
                        }
                        const scope = cont || m;
                        const c = scope.querySelector('[data-pre-plain-text^="["]') ||
                            (m.hasAttribute && m.hasAttribute('data-pre-plain-text') ? m : null);
                        const bijzonder = '[data-testid="quoted-message"], ' +
                            '[data-testid="link-preview-container"], [data-testid="poll-bubble"]';

                        // Eigen tekst (geneste treffers ontdubbeld, citaat/link/poll apart).
                        let delen = [...scope.querySelectorAll('span.selectable-text')];
                        if (delen.length === 0) delen = [...scope.querySelectorAll('.copyable-text span')];
                        delen = delen.filter(s => !s.closest(bijzonder));
                        delen = delen.filter(s => !delen.some(o => o !== s && o.contains(s)));

                        // Citaat: het geciteerde bericht bovenin de bubbel, als
                        // "↪ antwoord op wie: “wat”" vóór de eigen tekst, met de kleur van de streep.
                        let citaat = '', citKleur = '', citBeeld = '';
                        const qm = scope.querySelector('[data-testid="quoted-message"]') ||
                            scope.querySelector('.quoted-mention, [aria-label*="geciteerd" i], ' +
                                '[aria-label*="quoted" i]');
                        if (qm) {
                            delen = delen.filter(s => !qm.contains(s));
                            const qa = [...qm.querySelectorAll('[data-testid="author"]')];
                            let wie = qa[0] ? T(qa[0]).trim() : '';
                            let wat = T(qm.querySelector(
                                'span.selectable-text, span[data-testid="selectable-text"]'));
                            if (!wat) {
                                // Geciteerde foto/video zonder tekst: rest van het blok.
                                wat = T(qm);
                                for (const a of qa) wat = wat.split(a.textContent).join('');
                            }
                            if (!qa.length && !wie) {
                                // Oudere DOM: eerste regel = wie, rest = wat.
                                const regels = T(qm).split('\n').map(r => r.trim()).filter(Boolean);
                                if (regels.length > 1) { wie = regels[0]; wat = regels.slice(1).join(' '); }
                            }
                            const streep = [...qm.querySelectorAll('span, div')].find(e => {
                                const bg = getComputedStyle(e).backgroundColor;
                                const b = e.getBoundingClientRect();
                                return bg && bg !== 'rgba(0, 0, 0, 0)' && b.width > 0 && b.width <= 6;
                            });
                            citKleur = streep ? getComputedStyle(streep).backgroundColor
                                : (qa[0] ? diepKleur(qa[0]) : '');
                            if (/^(jij|you|vous|u)$/i.test(wie)) wie = 'jou';
                            // Geciteerde foto/video: WhatsApp toont rechts in het citaat een miniatuur.
                            const qImg = [...qm.querySelectorAll('img')].filter(i =>
                                !i.classList.contains('emoji') && (i.naturalWidth || i.clientWidth) >= 30)
                                .sort(grootste)[0];
                            if (qImg) citBeeld = await beeldData(qImg, m, 120);
                            wat = wat.replace(/\s+/g, ' ').trim().slice(0, 120);
                            // Geciteerde media zonder miniatuur: WhatsApp zet er een symbool voor
                            // ("📷 2 foto's", "🎥 Video", "🎤 Spraakbericht").
                            const sym = qm.querySelector('[data-testid="chat-msg-symbol"] title')?.textContent || '';
                            const symEmoji = /image|camera/i.test(sym) ? '📷' : /video/i.test(sym) ? '🎥' :
                                /ptt|audio|mic/i.test(sym) ? '🎤' : /doc/i.test(sym) ? '📄' :
                                /sticker/i.test(sym) ? '🩵' : /location|map/i.test(sym) ? '📍' : '';
                            if (symEmoji && !/^\p{Extended_Pictographic}/u.test(wat)) wat = (symEmoji + ' ' + wat).trim();
                            if (!wat && citBeeld) wat = '📷 Foto';
                            if (wat) citaat = '↪ antwoord op ' + (wie ? wie + ': ' : '') + '“' + wat + '”';
                        }

                        // Afzender zoals WhatsApp hem in een groep toont.
                        const auteurs = [...scope.querySelectorAll('[data-testid="author"]')]
                            .filter(a => !a.closest('[data-testid="quoted-message"]'));
                        let auteur = '', nummer = '', profiel = false, kleur = '', label = '';
                        if (auteurs[0]) {
                            auteur = T(auteurs[0]).trim();
                            kleur = diepKleur(auteurs[0]);
                            profiel = /^(mogelijk|maybe|peut-être)\b/i.test(
                                auteurs[0].getAttribute('aria-label') || '');
                            if (auteurs[1]) nummer = T(auteurs[1]).trim();
                            // Groepslabel ("Mama Gabrielle"): een kaal knopje vlak na de naam.
                            let el = auteurs[auteurs.length - 1];
                            zoek: for (let k = 0; k < 3 && el && el !== scope; k++, el = el.parentElement) {
                                let sib = el.nextElementSibling;
                                for (let j = 0; j < 2 && sib; j++, sib = sib.nextElementSibling) {
                                    if (sib.hasAttribute('data-testid') || sib.hasAttribute('data-pre-plain-text') ||
                                        sib.querySelector('[data-testid], [data-pre-plain-text], img')) continue;
                                    const t = (sib.textContent || '').trim();
                                    if (t && t.length <= 60) { label = t; break zoek; }
                                }
                            }
                        }

                        // Meta: tijd, "Bewerkt" en de vinkjes.
                        const meta = scope.querySelector('[data-testid="msg-meta"]');
                        const metaTekst = meta ? T(meta) : '';
                        const metaLabels = meta ? [...meta.querySelectorAll('[aria-label], [data-icon], title')]
                            .map(x => x.getAttribute('aria-label') || x.getAttribute('data-icon') ||
                                x.textContent || '').join(' ') : '';
                        const klok = (metaTekst.match(/\d{1,2}:\d{2}(\s?[AP]M)?/i) || [''])[0];
                        const bewerkt = /bewerkt|edited|modifi/i.test(metaTekst);
                        let status = '';
                        if (/gelezen|\bread\b|\blu\b/i.test(metaLabels)) status = 'gelezen';
                        else if (/afgeleverd|delivered|dblcheck|distribu/i.test(metaLabels)) status = 'afgeleverd';
                        else if (/verzonden|\bsent\b|msg-check|envoy/i.test(metaLabels)) status = 'verzonden';
                        else if (/behandeling|pending|clock|attente/i.test(metaLabels)) status = 'wachtend';

                        // Richting, van sterk naar zwak signaal: oude classes, het staartje,
                        // het data-id ("true_"/"false_" in oudere builds), de vinkjes (alleen
                        // bij eigen berichten) en anders de positie in het gesprek.
                        const tailIn = !!scope.querySelector('[data-icon="tail-in"]');
                        const tailOut = !!scope.querySelector('[data-icon="tail-out"]');
                        const dataId = (m.getAttribute && m.getAttribute('data-id')) ||
                            m.querySelector('[data-id]')?.getAttribute('data-id') || '';
                        let uit, dirZeker = true;
                        if (m.matches('.message-out, .message-out *') || m.querySelector('.message-out')) uit = true;
                        else if (m.matches('.message-in, .message-in *') || m.querySelector('.message-in')) uit = false;
                        else if (tailOut) uit = true;
                        else if (tailIn) uit = false;
                        else if (/^(true|false)_/.test(dataId)) uit = dataId.startsWith('true_');
                        else if (status) uit = true;
                        else {
                            // Vervolgbericht zonder staartje: de bubbelkleur beslist (eigen bubbels
                            // zijn groen, in het lichte én het donkere thema). De positie in het
                            // paneel is onbetrouwbaar: het verborgen venster is smal. De C#-kant
                            // erft bij twijfel de richting van het vorige bericht van de reeks.
                            dirZeker = false;
                            const bub = [...scope.querySelectorAll('div')]
                                .map(d => getComputedStyle(d).backgroundColor
                                    .match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)/))
                                .find(k => k && k[4] !== '0');
                            if (bub) {
                                uit = +bub[2] > +bub[1] && +bub[2] > +bub[3];
                            } else {
                                const rect = (scope.firstElementChild || scope).getBoundingClientRect();
                                uit = rect.width > 0 &&
                                    rect.left + rect.width / 2 > mainRect.left + mainRect.width / 2;
                            }
                        }

                        const fh = scope.querySelector('[data-testid="forwarded-header"]');
                        // (Cursief in WhatsApp; hier zonder markup, de weergave zet het zelf schuin.)
                        const fwd = fh ? (T(fh).replace(/[_*~]/g, '').trim() || 'Doorgestuurd') : '';

                        // Linkvoorbeeld (titel, omschrijving, domein, thumbnail).
                        let link = null;
                        const lp = scope.querySelector('[data-testid="link-preview-container"]');
                        const lpT = scope.querySelector('[data-testid^="link-preview-thumbnail"]');
                        const lpImg = lpT ? (lpT.tagName === 'IMG' ? lpT : lpT.querySelector('img')) : null;
                        if (lp) {
                            let lb = '';
                            if (lpImg && /^data:image/.test(lpImg.src) && lpImg.src.length < 80000) lb = lpImg.src;
                            else if (lpImg) lb = await beeldData(lpImg, m, 400);
                            link = {
                                titel: T(lp.querySelector('[data-testid="link-preview-title"]')),
                                omschrijving: T(lp.querySelector('[data-testid="link-description"]')),
                                domein: T(lp.querySelector('[data-testid="url-element"]')),
                                beeld: lb,
                                groot: !!scope.querySelector('[data-testid="high-quality-layout"]'),
                            };
                        }

                        // Beelden: album (tot 4 + "+N") of één foto/videostill/sticker.
                        let beeld = '', album = [], albumMeer = 0;
                        const albumEl = scope.querySelector('[data-testid="media-album"]');
                        if (albumEl) {
                            for (const th of [...albumEl.querySelectorAll('[data-testid="image-thumb"]')].slice(0, 4)) {
                                const im = [...th.querySelectorAll('img')].sort(grootste)[0];
                                const d = await beeldData(im, m, 480);
                                if (d) album.push(d);
                            }
                            const plus = [...albumEl.querySelectorAll('span')]
                                .map(s => (s.textContent || '').trim()).find(t => /^\+\d+$/.test(t));
                            if (plus) albumMeer = parseInt(plus.slice(1), 10);
                            beeld = album.shift() || '';
                        } else {
                            const kandidaten = [...scope.querySelectorAll('img')].filter(i => {
                                if (i.closest(bijzonder) || i === lpImg || i.classList.contains('emoji')) return false;
                                const src = i.src || i.currentSrc || '';
                                if (!/^(blob:|data:image|https:)/.test(src)) return false;
                                const b = i.getBoundingClientRect();
                                const breed = i.naturalWidth || i.clientWidth || b.width;
                                const hoog = i.naturalHeight || i.clientHeight || b.height;
                                return breed >= 50 && hoog >= 50;
                            });
                            beeld = await beeldData(kandidaten.sort(grootste)[0], m, 900);
                        }

                        // Bijzondere berichtsoorten.
                        let media = '', mediaInfo = '';
                        const tijdenIn = el => (T(el).match(/\b\d{1,2}:\d{2}\b/g) || []).filter(t => t !== klok);
                        const pollEl = scope.querySelector('[data-testid="poll-bubble"]');
                        let poll = null;
                        if (pollEl) {
                            media = 'poll';
                            const opties = [...pollEl.querySelectorAll('input[type="checkbox"], input[type="radio"]')]
                                .map(inp => {
                                    const lab = inp.id ? pollEl.querySelector('label[for="' + CSS.escape(inp.id) + '"]') : null;
                                    const al = inp.getAttribute('aria-label') || '';
                                    return {
                                        tekst: lab ? T(lab) : al.replace(/\s*\d+\s*(stemmen|stem|votes?|voix)\s*$/i, ''),
                                        stemmen: parseInt((al.match(/(\d+)\s*(stem|vote|voix)/i) || [])[1] || '0', 10),
                                        gekozen: inp.getAttribute('aria-checked') === 'true' || !!inp.checked,
                                    };
                                });
                            poll = {
                                // De vraag is in WhatsApp altijd vet: geen *markup* eromheen.
                                vraag: T(pollEl.querySelector('[data-pre-plain-text] span.selectable-text, ' +
                                    '[data-pre-plain-text] span[data-testid="selectable-text"]'))
                                    .replace(/^\*([\s\S]*)\*$/, '$1'),
                                meerdere: !!pollEl.querySelector('[data-icon*="multi-select"]'),
                                opties,
                            };
                        } else if (scope.querySelector('[data-testid="call-log-system-message"]')) {
                            media = 'oproep';
                            mediaInfo = T(scope.querySelector('[data-testid="call-log-system-message"]'))
                                .split('\n').map(s => s.trim()).filter(s => s && s !== klok).join(' · ');
                        } else if (scope.querySelector('[data-testid="video-content"], [data-testid*="video-thumb"]')) {
                            media = 'video';
                            mediaInfo = tijdenIn(scope.querySelector('[data-testid="video-content"]') || scope)[0] || '';
                        } else if (scope.querySelector('[data-testid*="audio"], [data-testid*="ptt"], ' +
                            '[data-icon*="audio"], [data-icon*="ptt"], [aria-label*="spraakbericht" i], ' +
                            '[aria-label*="voice message" i]')) {
                            media = 'audio';
                            mediaInfo = tijdenIn(scope)[0] || '';
                        } else if (scope.querySelector('[data-testid*="document"], [data-icon*="document"], ' +
                            '[data-icon^="doc-"], [data-icon*="-doc"]')) {
                            media = 'document';
                            const titel = scope.querySelector('[title]')?.getAttribute('title') || '';
                            const rest = T(scope).split('\n').map(s => s.trim())
                                .filter(s => s && s !== klok && s !== auteur && s !== nummer && s !== label && s !== titel);
                            mediaInfo = [titel, ...rest].filter(Boolean).slice(0, 3).join(' · ');
                        } else if (scope.querySelector('[data-testid*="sticker"]')) {
                            media = 'sticker';
                        } else if (scope.querySelector('[data-icon*="recalled"]')) {
                            media = 'verwijderd';
                        }

                        // Emoji-reacties (❤️ 👍 …) onder de bubbel: een knopje met aria-label en
                        // de emoji's als <img alt>. Die staan naast de bubbel, dus in de rij zoeken.
                        let reacties = '';
                        const rEl = m.querySelector(
                            '[aria-label*="reactie" i], [aria-label*="reaction" i], ' +
                            '[aria-label*="réaction" i], [data-testid*="reaction"]');
                        if (rEl) {
                            reacties = [...rEl.querySelectorAll('img')]
                                .map(i => i.alt || '').filter(Boolean).join('');
                            if (!reacties) {
                                const bron = (rEl.getAttribute('aria-label') || '') + ' ' +
                                    (rEl.textContent || '');
                                reacties = [...bron.matchAll(/\p{Extended_Pictographic}/gu)]
                                    .map(x => x[0]).join('');
                            }
                            const totaal = parseInt((rEl.getAttribute('aria-label') || '')
                                .match(/\d+/)?.[0] || '', 10);
                            if (reacties && totaal > 1) { reacties += ' ' + totaal; }
                        }

                        let txt = ((citaat ? citaat + '\n' : '') +
                            delen.map(s => T(s)).join(' ').trim()).trim();
                        if (!media && /^(dit bericht is verwijderd|je hebt dit bericht verwijderd|this message was deleted|you deleted this message)/i.test(txt)) {
                            media = 'verwijderd';
                        }
                        if (poll) {
                            // Als tekst (voor Claude en de cockpitlijst) de vraag met de stand.
                            txt = (citaat ? citaat + '\n' : '') + '📊 ' + poll.vraag + '\n' +
                                poll.opties.map(o => '• ' + o.tekst + ' (' + o.stemmen + ')').join('\n');
                        }
                        if (!txt && !beeld && !media && !link) {
                            // Onbekende soort (contactkaart, locatie, evenement…): de zichtbare
                            // tekst van de bubbel zonder naam en tijd, zodat niets verdwijnt.
                            mediaInfo = T(scope).split('\n').map(s => s.trim())
                                .filter(s => s && s !== klok && s !== auteur && s !== nummer && s !== label &&
                                    !/^(bewerkt|edited)$/i.test(s))
                                .join(' · ').slice(0, 200);
                            if (mediaInfo) media = 'overig';
                        }
                        msgs.push({
                            pre: c ? c.getAttribute('data-pre-plain-text') : '',
                            uit, dirZeker, txt, beeld, reacties, klok, kleur, auteur, nummer, profiel, label,
                            avatar: uit ? '' : await avatarData(avatarVan.get(m) || ''),
                            vervolg: heeftStaarten && !tailIn && !tailOut,
                            fwd, bewerkt, status: uit ? status : '', citKleur, citBeeld, link, album, albumMeer,
                            media, mediaInfo, poll, systeem: false,
                        });
                    }
                    // Het fotolezen heeft het paneel omhoog gescrold: weer onderaan
                    // achterlaten, zodat een volgende ronde (en WhatsApp zelf) bij de
                    // nieuwste berichten begint.
                    const pEind = paneel();
                    if (pEind) pEind.scrollTop = pEind.scrollHeight;
                    msgs.reverse();
                    const gevuld = msgs.filter(m => m.txt || m.beeld || m.media || m.link);
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
        for (var i = 0; i < 60; i++) // foto's en avatars omzetten kan even duren (max. ~18 s)
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
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Diagnose-object: geen berichten gevonden — meld wát er dan wél in de DOM staat.
                throw new InvalidOperationException(
                    $"0 berichten; DOM-stand: {doc.RootElement.GetProperty("diag").GetRawText()}");
            }
        }
        var ruw = JsonSerializer.Deserialize<List<WaRuw>>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        return NaarBerichten(ruw, naam);
    }

    /// <summary>Wat de pagina per bericht teruggeeft (zie het script in OpenEnLeesAsync).</summary>
    private sealed class WaRuw
    {
        public string Pre { get; set; } = "";
        public bool Uit { get; set; }
        public bool DirZeker { get; set; } = true;
        public string Txt { get; set; } = "";
        public string Beeld { get; set; } = "";
        public string Reacties { get; set; } = "";
        public string Klok { get; set; } = "";
        public string Kleur { get; set; } = "";
        public string Auteur { get; set; } = "";
        public string Nummer { get; set; } = "";
        public bool Profiel { get; set; }
        public string Label { get; set; } = "";
        public string Avatar { get; set; } = "";
        public bool Vervolg { get; set; }
        public string Fwd { get; set; } = "";
        public bool Bewerkt { get; set; }
        public string Status { get; set; } = "";
        public string CitKleur { get; set; } = "";
        public string CitBeeld { get; set; } = "";
        public WaRuwLink? Link { get; set; }
        public List<string> Album { get; set; } = [];
        public int AlbumMeer { get; set; }
        public string Media { get; set; } = "";
        public string MediaInfo { get; set; } = "";
        public WaRuwPoll? Poll { get; set; }
        public bool Systeem { get; set; }
    }

    private sealed class WaRuwLink
    {
        public string Titel { get; set; } = "";
        public string Omschrijving { get; set; } = "";
        public string Domein { get; set; } = "";
        public string Beeld { get; set; } = "";
        public bool Groot { get; set; }
    }

    private sealed class WaRuwPoll
    {
        public string Vraag { get; set; } = "";
        public bool Meerdere { get; set; }
        public List<WaRuwOptie> Opties { get; set; } = [];
    }

    private sealed class WaRuwOptie
    {
        public string Tekst { get; set; } = "";
        public int Stemmen { get; set; }
        public bool Gekozen { get; set; }
    }

    private static readonly System.Text.RegularExpressions.Regex PreRegex = new(
        @"^\[(?<klok>[^,\]]+),\s*(?<datum>[^\]]+)\]\s*(?<wie>[^:]*):\s*$");

    /// <summary>
    /// Zet de ruwe pagina-uitlezing om naar berichten: tijd en datum uit de berichtkop
    /// ("[12:30, 13/9/2026] Naam: "), datum doorgeschoven naar berichten zonder kop (foto,
    /// album, poll), en vervolgberichten erven de afzender van het bericht erboven.
    /// </summary>
    private static List<Regel> NaarBerichten(List<WaRuw> ruw, string chatNaam)
    {
        var uit = new List<Regel>();
        var datum = "";
        WhatsAppClient.WaBericht? vorige = null;
        foreach (var r in ruw)
        {
            if (r.Systeem)
            {
                var sys = new WaBericht("", "", false, r.Txt) { Media = "systeem", Datum = datum };
                uit.Add(new Regel("", sys));
                vorige = null; // na een systeemmelding begint WhatsApp een nieuwe reeks
                continue;
            }
            var m = PreRegex.Match(r.Pre);
            var klok = r.Klok.Length > 0 ? r.Klok : m.Success ? m.Groups["klok"].Value.Trim() : "";
            if (m.Success)
            {
                datum = m.Groups["datum"].Value.Trim();
            }
            var wie = m.Success ? m.Groups["wie"].Value.Trim() : "";
            string afzender;
            string nummer = r.Nummer, kleur = r.Kleur, label = r.Label, avatar = r.Avatar;
            var profiel = r.Profiel;
            if (!r.DirZeker && r.Vervolg && vorige is not null && r.Auteur.Length == 0)
            {
                // Richting was gegokt (geen staartje, geen vinkjes): een vervolgbericht
                // hoort bij dezelfde kant als het bericht erboven.
                r.Uit = vorige.Uitgaand;
            }
            if (r.Uit)
            {
                afzender = "Ik";
            }
            else if (r.Auteur.Length > 0)
            {
                afzender = r.Auteur;
            }
            else if (vorige is { Uitgaand: false } && (r.Vervolg || wie.Length == 0 ||
                     wie == vorige.Nummer || wie == vorige.Afzender))
            {
                // Vervolgbericht: WhatsApp toont de naam alleen boven het eerste van de reeks.
                afzender = vorige.Afzender;
                nummer = vorige.Nummer;
                kleur = vorige.Kleur;
                label = vorige.Label;
                profiel = vorige.Profielnaam;
                avatar = vorige.AvatarUrl;
            }
            else
            {
                afzender = wie.Length > 0 ? wie : chatNaam;
            }
            var tijd = klok.Length > 0 && datum.Length > 0 ? $"{klok}, {datum}"
                : m.Success ? $"{m.Groups["klok"].Value.Trim()}, {m.Groups["datum"].Value.Trim()}" : klok;
            var b = new WaBericht(tijd, afzender, r.Uit, r.Txt, r.Beeld, r.Reacties)
            {
                Klok = klok,
                Datum = datum,
                Kleur = kleur,
                Nummer = nummer,
                Profielnaam = profiel,
                Label = label,
                AvatarUrl = avatar,
                Vervolg = r.Vervolg,
                Doorgestuurd = r.Fwd,
                Bewerkt = r.Bewerkt,
                Status = r.Status,
                CitaatKleur = r.CitKleur,
                CitaatBeeld = r.CitBeeld,
                Link = r.Link is { } l && (l.Titel.Length > 0 || l.Domein.Length > 0)
                    ? new WaLink(l.Titel, l.Omschrijving, l.Domein, l.Beeld, l.Groot) : null,
                Album = r.Album,
                AlbumMeer = r.AlbumMeer,
                Media = r.Media,
                MediaInfo = r.MediaInfo,
                Poll = r.Poll is { } p
                    ? new WaPoll(p.Vraag, p.Meerdere,
                        p.Opties.Select(o => new WaPollOptie(o.Tekst, o.Stemmen, o.Gekozen)).ToList())
                    : null,
            };
            uit.Add(new Regel(r.Pre, b));
            vorige = b;
        }
        return uit;
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
        if (await InvoerLeegAsync())
        {
            return;
        }

        // De Enter-toets kwam niet aan (WhatsApp negeert geregeld synthetische events):
        // tweede route is de verzendknop zelf, met een echte muisklik-reeks.
        await JsAsync(
            """
            (function () {
                const knop = document.querySelector(
                    '#main footer button[aria-label], #main footer [data-icon="send"]');
                const doel = knop?.closest('button') || knop;
                if (!doel) return 'geen knop';
                const b = doel.getBoundingClientRect();
                const opts = { bubbles: true, cancelable: true, view: window,
                    clientX: b.x + b.width / 2, clientY: b.y + b.height / 2, buttons: 1 };
                for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) {
                    doel.dispatchEvent(type.startsWith('pointer')
                        ? new PointerEvent(type, opts) : new MouseEvent(type, opts));
                }
                return 'ok';
            })()
            """);
        await Task.Delay(800, ct);
        if (!await InvoerLeegAsync())
        {
            // Niet stilzwijgend "verstuurd" melden: de tekst staat nog in het invoerveld,
            // dus het bericht is WhatsApp niet in gegaan.
            throw new InvalidOperationException(
                "WhatsApp nam het bericht niet aan — de tekst staat nog in het invoerveld.");
        }
    }

    /// <summary>
    /// Is het invoerveld leeg? WhatsApp wist het pas als het bericht echt vertrokken is,
    /// dus dit is het betrouwbaarste bewijs dat er verstuurd is.
    /// </summary>
    private async Task<bool> InvoerLeegAsync() =>
        await JsAsync(
            """
            (function () {
                const box = document.querySelector('#main footer div[contenteditable="true"]');
                return !box || (box.textContent || '').trim().length === 0;
            })()
            """) == "true";

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
            var metFoto = berichten.Count(r => r.B.Beeld.Length > 0);
            log($"Berichten uitgelezen: {berichten.Count}, met foto: {metFoto}.");
            var voorbeeld = berichten.LastOrDefault(r => r.B.Beeld.Length > 0)?.B.Beeld ?? "";
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

    /// <summary>
    /// Diagnose (CLI --wajs): opent optioneel een chat en draait een stuk JavaScript in de
    /// WhatsApp-sessie. Handig als WhatsApp zijn bubbel-DOM weer eens wijzigt. Let op: een
    /// chat openen markeert hem als gelezen.
    /// </summary>
    public async Task<string> DiagnoseJsAsync(string chat, string script, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 25))
            {
                return "(niet ingelogd)";
            }
            if (chat.Length > 0)
            {
                await OpenChatAsync(chat, ct);
            }
            // Het script mag ook een Promise opleveren (async-diagnoses): het resultaat
            // komt dan in window.__wmDiag en wordt gepolld.
            await JsAsync(
                "window.__wmDiag = null; Promise.resolve(eval(" + JsonSerializer.Serialize(script) +
                ")).then(v => { window.__wmDiag = String(v); }, e => { window.__wmDiag = 'FOUT: ' + e; }); true");
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(300, ct);
                var klaar = await JsAsync("window.__wmDiag");
                if (klaar != "null")
                {
                    return JsonSerializer.Deserialize<string>(klaar) ?? "";
                }
            }
            return "(geen resultaat binnen 30 s)";
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Diagnose (CLI --wascreenshot): chat openen, onderaan zetten en een PNG maken van hoe
    /// WhatsApp Web hem zelf toont — referentiebeeld voor de bubbelweergave.
    /// </summary>
    public async Task DiagnoseScreenshotAsync(string chat, string pad, CancellationToken ct,
        string voorafJs = "")
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 25))
            {
                throw new InvalidOperationException("niet ingelogd");
            }
            _venster!.Size = new Size(1500, 1250); // breed: gesprekspaneel zoals op een desktop
            if (chat.Length > 0)
            {
                await OpenChatAsync(chat, ct);
            }
            await Task.Delay(1500, ct);
            if (voorafJs.Length > 0)
            {
                await JsAsync(voorafJs); // bv. omhoog scrollen naar een ouder stuk gesprek
                await Task.Delay(1500, ct);
            }
            using var beeld = new MemoryStream();
            await WebLimiet.MetLimietAsync(
                _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld),
                WebLimiet.Invoer, "schermafdruk", CrashLog, Herstel(false));
            File.WriteAllBytes(pad, beeld.ToArray());
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Diagnose (CLI --waberichten): de bubbel-leescode draaien op één chat.</summary>
    public async Task<string> DiagnoseBerichtenAsync(string chat, CancellationToken ct)
    {
        var (berichten, _, ondertitel) = await LaatsteBerichtenAsync(chat, 25, ct);
        return $"ondertitel: {ondertitel}\n" + string.Join("\n---\n", berichten.Select(b =>
            $"[{b.Datum} {b.Klok}] {b.AfzenderVolledig}{(b.Uitgaand ? " (uit)" : "")}" +
            (b.Profielnaam ? " ~" : "") + (b.Label.Length > 0 ? $" «{b.Label}»" : "") +
            (b.Kleur.Length > 0 ? $" {b.Kleur}" : "") + (b.Vervolg ? " (vervolg)" : "") +
            (b.AvatarUrl.Length > 0 ? $" avatar:{b.AvatarUrl.Length / 1024}kB" : "") +
            (b.Status.Length > 0 ? $" ✓{b.Status}" : "") + (b.Bewerkt ? " bewerkt" : "") +
            (b.Doorgestuurd.Length > 0 ? $" [{b.Doorgestuurd}]" : "") +
            (b.CitaatKleur.Length > 0 ? $" citaat:{b.CitaatKleur}" : "") +
            (b.CitaatBeeld.Length > 0 ? $" citaatbeeld:{b.CitaatBeeld.Length / 1024}kB" : "") +
            (b.Media.Length > 0 ? $" <{b.Media}: {b.MediaInfo}>" : "") +
            (b.Beeld.Length > 0 ? $" beeld:{b.Beeld.Length / 1024}kB" : "") +
            (b.Album.Count > 0 ? $" album+{b.Album.Count}(+{b.AlbumMeer})" : "") +
            (b.Link is { } l ? $" link:«{l.Titel}» {l.Domein} beeld:{l.Beeld.Length / 1024}kB" : "") +
            (b.Poll is { } p ? $" poll:{p.Opties.Count} opties" : "") +
            $":\n{b.Tekst}" + (b.Reacties.Length > 0 ? $"  {{{b.Reacties}}}" : "")));
    }

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
    /// Wat er bij een vastloper moet gebeuren: de sessie markeren voor een verse start.
    /// </summary>
    private Action? Herstel(bool aan) => aan ? MarkeerVoorVerseStart : null;

    private const string CrashLog = "wa-crash-log.txt";

    /// <summary>
    /// Voert een script uit in de pagina. Met tijdslimiet (zie <see cref="WebLimiet"/>): een
    /// vastgelopen renderer liet de hele poll anders eeuwig hangen — ExecuteScriptAsync
    /// keert dan nooit terug. Bij een time-out wordt de sessie gemarkeerd voor een verse
    /// start en komt de vastloper in het crashlogboek.
    /// </summary>
    private async Task<string> JsAsync(string script, int tijdslimietSeconden = 20)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("WhatsApp-sessie is niet gestart.");
        }
        try
        {
            return await WebLimiet.MetLimietAsync(
                core.ExecuteScriptAsync(script), TimeSpan.FromSeconds(tijdslimietSeconden),
                "JavaScript", CrashLog, Herstel(true));
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
