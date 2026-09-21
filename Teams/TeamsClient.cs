using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Eenvoudige Microsoft Teams-uitlezer via de webclient (teams.cloud.microsoft) in een
/// (meestal onzichtbaar) WebView2-venster: chats met ongelezen berichten signaleren voor
/// de cockpit. De koppeling (Microsoft-login, incl. MFA) gebeurt één keer in het zichtbare
/// venster en blijft bewaard in een eigen WebView2-profiel. Alleen uitlezen — antwoorden
/// gebeurt in Teams zelf. De web-UI van Teams wijzigt geregeld; de detectie is bewust op
/// meerdere kenmerken gebouwd (data-tid, aria-labels, vetgedrukte titels) en fouten worden
/// gelogd in plaats van stil te mislukken.
/// </summary>
public sealed class TeamsClient : IDisposable
{
    public static TeamsClient Instance { get; } = new();

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string MarkerFile = Path.Combine(DataDir, "teams-linked.txt");

    /// <summary>Buiten-beeld-werkformaat: hoog venster = meer gerenderde chatrijen.</summary>
    private static readonly Size WerkFormaat = new(1200, 1600);

    private Form? _venster;
    private WebView2? _web;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private DateTimeOffset _laatstHerladen = DateTimeOffset.MinValue;
    private bool _gelezenToegestaan; // tijdelijk aan tijdens bewust "als gelezen zetten"
    private volatile bool _gecrasht; // browserproces weg → bij de volgende beurt vers opbouwen

    /// <summary>Laat de volgende poll de pagina vers herladen ("Volledige synchronisatie").</summary>
    public void ForceerHerlaad() => _laatstHerladen = DateTimeOffset.MinValue;

    /// <summary>
    /// Nachtelijk onderhoud: de sessie bij de eerstvolgende beurt volledig vers opbouwen
    /// (zelfde route als het crash-herstel; cookies en dus de aanmelding blijven staan).
    /// </summary>
    public void MarkeerVoorVerseStart() => _gecrasht = true;

    /// <summary>Is er ooit met succes gekoppeld? Zo niet, dan slaat de cockpit Teams over.</summary>
    public static bool OoitGekoppeld => File.Exists(MarkerFile);

    /// <summary>Is de sessie op dit moment ingelogd? (Bijgewerkt bij elke start/poll.)</summary>
    public static bool Aangemeld { get; private set; }

    /// <summary>
    /// Start de ingebedde Teams-sessie (buiten beeld). Retourneert true zodra de app
    /// geladen is op het Teams-domein (= ingelogd); false als er een login nodig is.
    /// </summary>
    private readonly SemaphoreSlim _initSlot = new(1, 1);

    public async Task<bool> StartAsync(CancellationToken ct, int wachtSeconden = 30)
    {
        // Initialisatie serialiseren: een poll en een koppel-klik mogen nooit tegelijk
        // een tweede venster/WebView aanmaken (zelfde profielmap = vergrendeld profiel).
        await _initSlot.WaitAsync(ct);
        try
        {
            if (_gecrasht)
            {
                // Na een browsercrash blijft _web.CoreWebView2 een ongeldig object dat bij
                // elk gebruik "no longer valid" gooit: control en venster volledig weggooien
                // zodat de opbouw hieronder een verse sessie start (profiel/cookies blijven).
                try { _web?.Dispose(); } catch { /* al kapot */ }
                try { _venster?.Dispose(); } catch { /* al kapot */ }
                _web = null;
                _venster = null;
                _gecrasht = false;
            }
            if (_web?.CoreWebView2 is null)
            {
                if (_venster is null)
                {
                    _venster = new Form
                    {
                        Text = "Teams koppelen – meld je aan met je Microsoft-account",
                        // Bewust groot (buiten beeld): meer gerenderde chatrijen in de
                        // gevirtualiseerde lijst = minder scroll-zoekwerk.
                        Size = WerkFormaat,
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-4000, -4000),
                        ShowInTaskbar = false,
                    };
                    _venster.FormClosing += (_, e) =>
                    {
                        e.Cancel = true; // sessie blijft op de achtergrond leven
                        Verberg();
                    };
                    _web = new WebView2 { Dock = DockStyle.Fill };
                    _venster.Controls.Add(_web);
                    _venster.Show();
                }

                // Zonder deze vlaggen bevriest de browser de (onzichtbare) pagina na een
                // tijdje en komen nieuwe berichten niet meer binnen.
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(DataDir, "webview2-teams"),
                    new CoreWebView2EnvironmentOptions(
                        "--disable-background-timer-throttling " +
                        "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding"));
                // Met tijdslimiet én zelfherstel: hangt de init op een vergrendeld profiel
                // (achtergebleven webview-processen), dan worden die opgeruimd en volgt
                // één nieuwe poging met een verse control.
                _web = await WebViewOpruimer.InitMetHerstelAsync(_venster!, _web!, env,
                    Path.Combine(DataDir, "webview2-teams"), "Teams", ct);
                // Crasht het browserproces (Teams is zwaar; gebeurt na dagen draaien), dan is
                // deze CoreWebView2 blijvend ongeldig: markeren zodat de volgende beurt de
                // sessie automatisch vers opbouwt. Alleen een renderer-crash is ter plekke te
                // herstellen met een reload.
                var webNu = _web;
                _web.CoreWebView2!.ProcessFailed += (_, e) =>
                {
                    if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                        or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    {
                        try
                        {
                            webNu.CoreWebView2!.Reload();
                            _laatstHerladen = DateTimeOffset.Now;
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
                        File.AppendAllText(Path.Combine(DataDir, "teams-crash-log.txt"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ProcessFailed: " +
                            $"{e.ProcessFailedKind} (herstel: {(_gecrasht ? "herstart bij volgende poll" : "reload")})\r\n");
                    }
                    catch
                    {
                        // Alleen diagnose.
                    }
                };
                // Bij elke verse sessie de lokale sitedata wissen (cookies blijven staan,
                // dus de aanmelding blijft geldig). Teams bewaart leesstanden ook lokaal
                // (IndexedDB/DOM-storage) en die lopen in deze sessie scheef: wij blokkeren
                // de leesbevestigingen, dus lokaal raken chats "ongelezen" die op de server
                // (via Teams op desktop/telefoon) al lang gelezen zijn — en andersom. Vers
                // beginnen = de lijst toont de serverstand.
                try
                {
                    await _web.CoreWebView2!.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.AllDomStorage |
                        CoreWebView2BrowsingDataKinds.IndexedDb |
                        CoreWebView2BrowsingDataKinds.DiskCache);
                }
                catch
                {
                    // Dan blijft de oude lokale staat staan; hooguit een verouderde badge.
                }
                // Teams opent bij het laden automatisch de recentste chat en zou die als
                // gelezen markeren. De markering loopt via "consumptionhorizon"-calls: die
                // blokkeren we — behalve wanneer archiveren in de cockpit een chat bewust
                // als gelezen wil zetten (_gelezenToegestaan).
                _web.CoreWebView2!.AddWebResourceRequestedFilter(
                    "*", CoreWebView2WebResourceContext.All);
                _web.CoreWebView2.WebResourceRequested += (_, e) =>
                {
                    // Alleen schrijvende calls blokkeren: een GET met "consumptionhorizon"
                    // in de URL is het óphalen van de leesstand en moet gewoon doorgaan.
                    if (!_gelezenToegestaan &&
                        e.Request.Uri.Contains("consumptionhorizon", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(e.Request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                            null, 403, "Blocked by WorkManager", "");
                    }
                };
                // Pop-ups (window.open vanuit Teams of de Microsoft-login) in ditzelfde
                // verborgen venster afhandelen: standaard maakt WebView2 er een écht,
                // zichtbaar pop-upvenster van.
                _web.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    _web.CoreWebView2.Navigate(e.Uri);
                };
                _web.CoreWebView2.Navigate("https://teams.cloud.microsoft/");
                _laatstHerladen = DateTimeOffset.Now;
            }
        }
        finally
        {
            _initSlot.Release();
        }

        for (var i = 0; i < wachtSeconden * 2; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsIngelogdAsync())
            {
                File.WriteAllText(MarkerFile, DateTimeOffset.Now.ToString("O"));
                Aangemeld = true;
                return true;
            }
            // Verlopen sessie: het Microsoft-aanmeldscherm stil invullen (wachtwoord +
            // TOTP-code), net als bij Outlook. Alleen op échte aanmeldpagina's, nooit in
            // Teams zelf — daar zou de "Ja"-fallback van het KmsI-scherm mis kunnen klikken.
            if (i % 2 == 1 && await JsAsync(
                    "/(^|\\.)login\\.microsoftonline\\.com$|(^|\\.)login\\.live\\.com$|adfs|(^|\\.)sts\\./" +
                    ".test(location.hostname)") == "true")
            {
                MicrosoftLogin.NaLoginStap(await JsAsync(MicrosoftLogin.VulScript()), null);
            }
            await Task.Delay(500, ct);
        }
        Aangemeld = false;
        return false;
    }

    /// <summary>Toont het venster voor de Microsoft-login en verbergt het na een geslaagde aanmelding.</summary>
    public async Task KoppelAsync(CancellationToken ct)
    {
        // Kort checken of we al ingelogd zijn; zo niet, meteen het venster tonen.
        var ingelogd = await StartAsync(ct, wachtSeconden: 3);
        if (ingelogd || _venster is null)
        {
            if (ingelogd)
            {
                BronGezondheid.Hervat("Teams");
            }
            return;
        }
        // Hangt de sessie op een foutpagina (bv. Microsofts "offline"-pagina, die als rauwe
        // broncode verschijnt), dan helpt tonen alleen niet: eerst vers naar Teams
        // navigeren — dat geeft ofwel de chatlijst, ofwel het echte aanmeldscherm.
        try
        {
            _web?.CoreWebView2?.Navigate("https://teams.cloud.microsoft/");
        }
        catch
        {
            // Navigeren is best effort; de aanmeldlus hieronder doet de rest.
        }
        // Op het scherm waar de gebruiker nu werkt (muispositie), en even topmost zodat
        // het venster niet achter de gemaximaliseerde cockpit verdwijnt. Passend gemaakt:
        // het vaste werkformaat (1200×1600) is hoger dan het scherm, waardoor de titelbalk
        // — en dus de sluitknop — buiten beeld viel. Verberg() zet het werkformaat terug.
        var scherm = Screen.FromPoint(Cursor.Position).WorkingArea;
        _venster.WindowState = FormWindowState.Normal;
        _venster.Visible = true;
        _venster.Size = new Size(
            Math.Min(WerkFormaat.Width, scherm.Width - 60),
            Math.Min(WerkFormaat.Height, scherm.Height - 60));
        _venster.Location = new Point(
            scherm.X + (scherm.Width - _venster.Width) / 2,
            scherm.Y + (scherm.Height - _venster.Height) / 2);
        _venster.TopMost = true;
        _venster.BringToFront();
        _venster.Activate();
        try
        {
            File.WriteAllText(Path.Combine(DataDir, "teams-koppel-debug.txt"),
                $"{DateTime.Now:HH:mm:ss} venster={_venster.Bounds} zichtbaar={_venster.Visible} " +
                $"topmost={_venster.TopMost} web={(_web?.CoreWebView2 is null ? "GEEN core" : "core ok")} " +
                $"scherm={scherm}");
        }
        catch
        {
            // Alleen diagnose.
        }

        // Met try/finally: breekt het aanmelden af, dan bleef dit venster anders "altijd
        // bovenop" staan en kun je niet meer bij vensters die erachter zitten.
        try
        {
            var jsFoutGelogd = false;
            for (var i = 0; i < 900; i++) // max. 7,5 min voor login + MFA
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct);
                bool nuIngelogd;
                try
                {
                    // E-mail en wachtwoord vullen we in; alleen de MFA-stap blijft handwerk.
                    MicrosoftLogin.NaLoginStap(await JsAsync(MicrosoftLogin.VulScript()), _venster);
                    nuIngelogd = await IsIngelogdAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Eén mislukte JS-beurt — bv. de 2-min-poll die tegelijk dezelfde pagina
                    // bestuurt, of een navigatie die net het document verving — mag de
                    // aanmelding niet afbreken: de finally verborg dan meteen het venster,
                    // alsof de knop niets deed (29 augustus 2026). Halve seconde later opnieuw.
                    if (!jsFoutGelogd)
                    {
                        jsFoutGelogd = true;
                        try
                        {
                            File.AppendAllText(Path.Combine(DataDir, "teams-koppel-debug.txt"),
                                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} KoppelAsync: JS-fout " +
                                $"genegeerd, lus loopt door — {ex.Message}\r\n");
                        }
                        catch
                        {
                            // Alleen diagnose.
                        }
                    }
                    continue;
                }
                if (nuIngelogd)
                {
                    File.WriteAllText(MarkerFile, DateTimeOffset.Now.ToString("O"));
                    MfaTijd.Noteer("teams"); // een echte interactieve MFA-aanmelding
                    Aangemeld = true;
                    // Vers aangemeld: een eerdere foutpauze mag niet blijven blokkeren.
                    BronGezondheid.Hervat("Teams");
                    await Task.Delay(3000, ct); // chatlijst laten laden
                    return;
                }
            }
        }
        finally
        {
            Verberg();
        }
        throw new TimeoutException("De Teams-aanmelding werd niet (op tijd) afgerond.");
    }

    private void Verberg()
    {
        _venster!.TopMost = false;
        _venster.Location = new Point(-4000, -4000);
        _venster.Size = WerkFormaat; // terug naar het grote buiten-beeld-werkformaat
    }

    /// <summary>Diagnose van de trusted klikken (%APPDATA%\WorkManager\teams-klik-debug.txt).</summary>
    private void LogKlik(string melding)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "teams-klik-debug.txt"),
                $"{DateTime.Now:HH:mm:ss} {melding}\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    /// <summary>
    /// Echte (trusted) muisklik via het DevTools-protocol op viewport-coördinaten.
    /// De nieuwe Teams-DOM controleert isTrusted en negeert synthetische DOM-events
    /// (dispatchEvent-reeksen komen er sinds september 2026 niet meer aan).
    /// </summary>
    private async Task CdpKlikAsync(double x, double y)
    {
        var core = _web!.CoreWebView2!;
        // Zelfde reeks en velden als Puppeteer/Playwright: eerst bewegen, dan indrukken
        // (buttons=1) en na een menselijke pauze loslaten (buttons=0).
        foreach (var (type, knop, knoppen, telling, pauze) in new[]
        {
            ("mouseMoved", "none", 0, 0, 60),
            ("mousePressed", "left", 1, 1, 80),
            ("mouseReleased", "left", 0, 1, 0),
        })
        {
            var resultaat = await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",
                JsonSerializer.Serialize(new
                {
                    type, x, y, button = knop, buttons = knoppen,
                    clickCount = telling, pointerType = "mouse",
                }));
            if (resultaat is not ("{}" or ""))
            {
                LogKlik($"cdp {type} ({x},{y}) → {resultaat}");
            }
            if (pauze > 0)
            {
                await Task.Delay(pauze);
            }
        }
    }

    // Verhouding tussen CSS-coördinaten en wat het DevTools-protocol verwacht: bij
    // DPI-schaling komt een klik op CSS-coördinaten soms op een ándere plek aan. Wordt
    // automatisch gekalibreerd door te meten waar de eerste klik echt landt.
    private double _cdpSchaal = 1;

    /// <summary>
    /// Echte (trusted) Enter-toets via het DevTools-protocol, voor het element dat op
    /// dat moment focus heeft — een muisklik geeft een Fluent-rij wél focus maar gaat
    /// af en toe verloren wanneer React de rij nét hermount tussen indrukken en
    /// loslaten; het toetsenbord is dan de tweede route.
    /// </summary>
    private async Task CdpEnterAsync()
    {
        var core = _web!.CoreWebView2!;
        foreach (var type in new[] { "rawKeyDown", "keyUp" })
        {
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                JsonSerializer.Serialize(new
                {
                    type, key = "Enter", code = "Enter",
                    windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13,
                }));
            await Task.Delay(60);
        }
    }

    /// <summary>
    /// Zoekt het element dat de JS-expressie oplevert, scrolt het in beeld en klikt het
    /// trusted aan (zie <see cref="CdpKlikAsync"/>). Meet via een capture-listener waar
    /// de klik werkelijk landt en stelt de schaal bij als dat niet op het doel was.
    /// False als het element er niet is.
    /// </summary>
    private async Task<bool> KlikTrustedAsync(string zoekExpressie)
    {
        var gescrold = await JsAsync(
            $$"""
            (function () {
                const doel = {{zoekExpressie}};
                if (!doel) return false;
                doel.scrollIntoView({ block: 'center' });
                return true;
            })()
            """);
        if (gescrold != "true")
        {
            return false;
        }
        // De (gevirtualiseerde) lijst kan na het scrollen nog re-layouten en banners kunnen
        // alles een rij opschuiven: pas meten als het doel écht op het meetpunt ligt
        // (hit-test met elementFromPoint), en dan meteen klikken.
        var x = 0;
        var y = 0;
        for (var meting = 0; meting < 5; meting++)
        {
            await Task.Delay(meting == 0 ? 350 : 250);
            var ruw = await JsAsync(
                $$"""
                (function () {
                    const doel = {{zoekExpressie}};
                    if (!doel) return '';
                    const b = doel.getBoundingClientRect();
                    const x = Math.round(b.x + b.width / 2), y = Math.round(b.y + b.height / 2);
                    const raak = document.elementFromPoint(x, y);
                    if (!raak || !(doel.contains(raak) || raak.contains(doel))) {
                        doel.scrollIntoView({ block: 'center' });
                        return 'verschoven';
                    }
                    return x + '|' + y;
                })()
                """);
            string plek;
            try
            {
                plek = JsonSerializer.Deserialize<string>(ruw) ?? "";
            }
            catch (JsonException)
            {
                return false;
            }
            if (plek.Length == 0)
            {
                return false; // doel is uit de DOM verdwenen
            }
            if (plek == "verschoven")
            {
                continue;
            }
            var delen = plek.Split('|');
            if (delen.Length != 2 || !int.TryParse(delen[0], out x) ||
                !int.TryParse(delen[1], out y))
            {
                return false;
            }
            break;
        }
        if (x == 0 && y == 0)
        {
            LogKlik("doel blijft verschuiven; klik overgeslagen");
            return false;
        }
        for (var poging = 0; poging < 3; poging++)
        {
            await JsAsync(
                """
                (function () {
                    window.__wmKlikRaak = null;
                    window.addEventListener('click', e => window.__wmKlikRaak =
                        { x: e.clientX, y: e.clientY, trusted: e.isTrusted },
                        { capture: true, once: true });
                    return true;
                })()
                """);
            await CdpKlikAsync(x * _cdpSchaal, y * _cdpSchaal);
            await Task.Delay(250);
            var raakRuw = await JsAsync("JSON.stringify(window.__wmKlikRaak)");
            string raakJson;
            try
            {
                raakJson = JsonSerializer.Deserialize<string>(raakRuw) ?? "null";
            }
            catch (JsonException)
            {
                raakJson = "null";
            }
            if (raakJson is "null")
            {
                // Geen klik-event gezien: waarschijnlijk buiten het venster beland
                // (te grote schaal) of opgeslokt — devicePixelRatio als tweede gok.
                var dprRuw = await JsAsync("String(window.devicePixelRatio || 1)");
                var dpr = double.TryParse(JsonSerializer.Deserialize<string>(dprRuw),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 1;
                LogKlik($"klik ({x},{y}) ×{_cdpSchaal:0.###} kwam niet aan; probeer ×{dpr:0.###}");
                if (Math.Abs(_cdpSchaal - dpr) < 0.001)
                {
                    return true; // zelfde schaal nogmaals proberen heeft geen zin
                }
                _cdpSchaal = dpr;
                continue;
            }
            using var raak = JsonDocument.Parse(raakJson);
            var rx = raak.RootElement.GetProperty("x").GetDouble();
            var ry = raak.RootElement.GetProperty("y").GetDouble();
            if (Math.Abs(rx - x) <= 4 && Math.Abs(ry - y) <= 4)
            {
                return true; // raak
            }
            // Verkeerde plek: schaal herleiden uit de meting en opnieuw klikken.
            if (rx > 1)
            {
                LogKlik($"klik ({x},{y}) ×{_cdpSchaal:0.###} landde op ({rx:0},{ry:0}); herkalibreren");
                _cdpSchaal *= x / rx;
            }
            else
            {
                return true; // onbruikbare meting: niet blijven proberen
            }
        }
        return true;
    }

    /// <summary>
    /// Parkeert de sessie op de (lege) "Concepten"-weergave. Teams heropent bij een reload
    /// de laatst actieve weergave: zo wordt er nooit een echte chat auto-geopend en blijven
    /// de ongelezen-markeringen in de lijst staan.
    /// </summary>
    private async Task ParkeerOpConceptenAsync()
    {
        try
        {
            await KlikTrustedAsync(
                """
                [...document.querySelectorAll('[role="treeitem"], [role="listitem"], [data-tid]')]
                    .find(el => (el.textContent || '').trim() === 'Concepten' ||
                                (el.textContent || '').trim() === 'Drafts')
                """);
            await Task.Delay(800);
        }
        catch
        {
            // Best effort; hooguit blijft de auto-geopende chat staan.
        }
    }

    /// <summary>
    /// De pagina vers laden en wachten tot de chatlijst er weer staat. Aanroeper houdt het
    /// slot vast. Duurt 15 à 25 seconden: Teams start dan zijn hele web-app opnieuw op.
    /// </summary>
    private async Task HerlaadKernAsync(CancellationToken ct)
    {
        try
        {
            _web!.CoreWebView2!.Reload();
        }
        catch (Exception ex) when (ex.Message.Contains("no longer valid",
            StringComparison.OrdinalIgnoreCase))
        {
            _gecrasht = true;
            throw new InvalidOperationException(
                "De Teams-browser is gecrasht en wordt bij de volgende synchronisatie " +
                "automatisch opnieuw gestart.", ex);
        }
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(250, ct);
            if (await IsIngelogdAsync())
            {
                break;
            }
        }
        // Wachten tot de chatlijst er echt staat (max. ~6 s) in plaats van blind te wachten.
        await Task.Delay(300, ct);
        await WachtOpRijenAsync(20, 300, ct);
        _laatstHerladen = DateTimeOffset.Now;
    }

    private bool _herlaadLoopt;

    /// <summary>
    /// De periodieke herlaadbeurt buiten de ophaalbeurt om: hij neemt zelf het slot zodra dat
    /// vrij is, zodat de cockpit intussen al klaar is met zijn ronde.
    /// </summary>
    private async Task HerlaadOpAchtergrondAsync()
    {
        if (_herlaadLoopt)
        {
            return;
        }
        _herlaadLoopt = true;
        try
        {
            await _slot.WaitAsync();
            try
            {
                // Eerst parkeren: na de reload heropent Teams de laatst actieve weergave, en
                // dat mag geen echte chat zijn (die zou dan als gelezen gelden).
                await ParkeerOpConceptenAsync();
                await HerlaadKernAsync(CancellationToken.None);
                await TerugNaarChatlijstAsync();
                // Na een verse laadbeurt eerst weer echt kijken: dáárvoor herladen we.
                _laatsteVingerafdruk = "";
            }
            finally
            {
                _slot.Release();
            }
        }
        catch
        {
            // Mislukt: de volgende beurt merkt dat de herlaadbeurt nog openstaat.
        }
        finally
        {
            _herlaadLoopt = false;
        }
    }

    /// <summary>
    /// De rijen van de chatlijst. Teams levert per versie een andere structuur: op deze
    /// build heten de chatrijen <c>[data-testid="list-item"]</c>; de oude
    /// <c>data-tid</c>-varianten geven nul, en <c>[role="tree"] [role="treeitem"]</c> leverde
    /// de navigatie-items op ("Copilot", "Vermeldingen", "Concepten") in plaats van chats.
    /// Alle plekken die op rijen wachten gebruiken daarom dezelfde lijst — met één verkeerde
    /// selector wacht zo'n lus stilletjes altijd zijn volledige budget vol.
    /// </summary>
    private const string RijSelector =
        "'[data-testid=\"list-item\"], [data-tid^=\"chat-list-item\"], " +
        "[data-tid=\"chat-list\"] [role=\"listitem\"], [role=\"list\"] [role=\"listitem\"]'";

    private static string MetSelector(string script) =>
        script.Replace("__SELECTOR__", RijSelector);

    private string _laatsteVingerafdruk = "";
    private (int Totaal, List<TeamsBericht> Ongelezen, Dictionary<string, string> Previews)?
        _laatsteUitslag;
    private int _overgeslagen;

    /// <summary>
    /// Zet de filterknop "Ongelezen" boven de chatlijst aan of uit. Resultaat: "geklikt",
    /// "al-goed" (stond al in de gevraagde stand) of "geen-chip" (knop niet gevonden).
    /// Meldt de chip zijn stand niet via aria-attributen, dan wordt er bij het úítzetten
    /// altijd geklikt: de aanroeper zet hem alleen uit nadat hij (vermoedelijk) aanstond,
    /// en een filter die blijft hangen zou álle volgende beurten vergiftigen.
    /// </summary>
    private async Task<string> ZetOngelezenFilterAsync(bool aan)
    {
        var ruw = await JsAsync(
            $$"""
            (function () {
                // Taalonafhankelijk id-patroon eerst ("…-toggle-button-UNREAD"), daarna de
                // knoptekst als terugvaloptie.
                const chip = document.querySelector(
                        '[data-tid$="toggle-button-UNREAD"], [data-testid$="toggle-button-UNREAD"]') ||
                    [...document.querySelectorAll(
                        'button, [role="button"], [role="tab"], [role="radio"],' +
                        '[role="checkbox"], [role="switch"]')]
                    .find(b => /^(ongelezen|unread|non lus?)$/i.test(
                        (b.textContent || '').replace(/\s+/g, ' ').trim()));
                if (!chip) return 'geen-chip';
                const stand = chip.getAttribute('aria-pressed') ??
                    chip.getAttribute('aria-checked') ?? chip.getAttribute('aria-selected');
                if (stand !== null && (stand === 'true') === {{(aan ? "true" : "false")}}) {
                    return 'al-goed|' + chip.outerHTML.slice(0, 200);
                }
                // Niet zelf klikken: synthetische events negeert Teams (isTrusted-check),
                // dus alleen de plek doorgeven — de echte klik komt via het DevTools-protocol.
                chip.scrollIntoView({ block: 'center' });
                const b = chip.getBoundingClientRect();
                return 'klik|' + Math.round(b.x + b.width / 2) + '|' +
                    Math.round(b.y + b.height / 2) + '|' + chip.outerHTML.slice(0, 200);
            })()
            """);
        string uitslag;
        try
        {
            uitslag = JsonSerializer.Deserialize<string>(ruw) ?? "";
        }
        catch (JsonException)
        {
            return ruw;
        }
        if (uitslag.StartsWith("klik|", StringComparison.Ordinal))
        {
            var delen = uitslag.Split('|');
            if (delen.Length >= 3 && int.TryParse(delen[1], out var x) &&
                int.TryParse(delen[2], out var y))
            {
                await CdpKlikAsync(x, y);
                return "geklikt|" + string.Join('|', delen.Skip(3));
            }
            return "geen-chip";
        }
        return uitslag;
    }

    /// <summary>
    /// Momentopname van de gerenderde chatlijst (aantal + begin van elke rijtekst), om te
    /// zien of een klik op de filterknop de lijst überhaupt veranderd heeft.
    /// </summary>
    private Task<string> RijBeeldAsync() => JsAsync(MetSelector(
        """
        (function () {
            let items = [...document.querySelectorAll(__SELECTOR__)];
            items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
            return items.length + ' # ' + items.slice(0, 30)
                .map(it => (it.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 40))
                .join(' | ');
        })()
        """));

    /// <summary>
    /// Leest de ongelezen chats via de filterknop "Ongelezen" boven de chatlijst: even
    /// aanzetten, de (korte) gefilterde lijst lezen, weer uitzetten. Nodig sinds Teams de
    /// rijen zelf geen badge of "ongelezen"-label meer geeft. Retourneert null als de knop
    /// er niet is of de gefilterde lijst ongeloofwaardig blijft (dan geldt de heuristiek).
    /// Rijen zonder tijdstip én zonder preview (ook niet in de volledige scrape) zijn geen
    /// echte chats: na een meeting laat Teams soms kale naam-items van de deelnemers achter
    /// ("Henny", "Kevin") die de filter als ongelezen toont — die hielden een cockpitrij
    /// eindeloos in leven omdat elke sessie-herbouw ze opnieuw aanleverde.
    /// </summary>
    private async Task<List<TeamsBericht>?> FilterOngelezenAsync(
        Dictionary<string, string> scrapePreviews, CancellationToken ct)
    {
        var vooraf = int.TryParse(
            await JsAsync($"document.querySelectorAll({RijSelector}).length"), out var v) ? v : 0;
        var voorafBeeld = await RijBeeldAsync();
        var chip = await ZetOngelezenFilterAsync(aan: true);
        if (chip.StartsWith("geen-chip"))
        {
            return null;
        }
        var diagnose = new List<string> { $"chip={chip}", $"vooraf={vooraf}" };
        var herstelNodig = true;

        // Wachten tot de lijst echt hergefilterd is (het aantal rijen verandert); een
        // gelijk gebleven vólle lijst zou anders elke bovenste chat "ongelezen" maken.
        // Daarna nog even doorwachten tot de telling stilstaat: de gevirtualiseerde
        // lijst wisselt tijdens het herfilteren van aantal, en te vroeg lezen gaf een
        // lege tussenstand (0 rijen) terwijl de ongelezen chat nog moest renderen.
        async Task<int> WachtOpOmslagAsync()
        {
            var na = vooraf;
            var stabiel = 0;
            for (var i = 0; i < 25; i++)
            {
                await Task.Delay(200, ct);
                var n = int.TryParse(
                    await JsAsync($"document.querySelectorAll({RijSelector}).length"),
                    out var t) ? t : 0;
                if (n != vooraf && n == na)
                {
                    if (++stabiel >= 3) // 600 ms onveranderd na de omslag
                    {
                        break;
                    }
                }
                else
                {
                    stabiel = 0;
                }
                na = n;
            }
            return na;
        }

        try
        {
            var na = await WachtOpOmslagAsync();
            // Exact dezelfde lijst ná een klik = de klik is verloren gegaan (trusted klikken
            // missen af en toe, zie OpenChatAsync). Vroeger gold dat pas vanaf 25 rijen; toen
            // Teams nog maar 20 rijen renderde, kwam de volle ongefilterde lijst erdoor en
            // werden álle 20 chats als ongelezen gemeld (15 september 2026: "gevonden=20/20",
            // cockpit vol oude Teams-chats die bovendien allemaal geopend werden).
            if (chip.StartsWith("geklikt") && na == vooraf && await RijBeeldAsync() == voorafBeeld)
            {
                diagnose.Add("klik-verloren");
                chip = await ZetOngelezenFilterAsync(aan: true);
                diagnose.Add($"chip2={chip}");
                na = await WachtOpOmslagAsync();
                if (na == vooraf && await RijBeeldAsync() == voorafBeeld)
                {
                    diagnose.Add($"na={na}");
                    diagnose.Add("uitslag=filter-niet-toegepast");
                    // De lijst staat nog ongefilterd: "terugzetten" zou de filter juist
                    // áánzetten (de chip meldt zijn stand niet) en zo blijven hangen.
                    herstelNodig = chip.StartsWith("al-goed");
                    return null;
                }
            }
            diagnose.Add($"na={na}");
            // Een filter die evenveel rijen overlaat als de volle lijst is ook met een
            // (door een re-render) iets ander beeld niet geloofwaardig.
            if (na == vooraf && na >= 10)
            {
                diagnose.Add("uitslag=filter-niet-toegepast");
                herstelNodig = false;
                return null; // filter lijkt niet toegepast: niet op gokken
            }
            if (na > vooraf)
            {
                // Filteren kan de lijst alleen laten krimpen. Groeit hij, dan stond de
                // filter vermoedelijk al aan (hangen gebleven na een crash) en heeft onze
                // klik hem juist úítgezet — vooral niet de volle lijst als "ongelezen"
                // teruggeven. De lijst staat nu al in de gewenste ongefilterde stand,
                // dus het herstel in de finally moet níét nog eens klikken.
                diagnose.Add("uitslag=lijst-groeide");
                herstelNodig = false;
                return null;
            }
            var json = await JsAsync(MetSelector(
                """
                (function () {
                    let items = [...document.querySelectorAll(__SELECTOR__)];
                    items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                    return JSON.stringify(items.slice(0, 30).map(it => {
                        let ruw = (it.textContent || '').replace(/\s+/g, ' ').trim();
                        ruw = ruw.replace(/^(chats?|ongelezen|unread|non lus?)[,.:]?\s*/i, '');
                        const tijd = ruw.match(/\d{1,2}:\d{2}|\d{1,2}-\d{1,2}/);
                        let naam = ruw, preview = '';
                        if (tijd) {
                            naam = ruw.slice(0, tijd.index).trim();
                            preview = ruw.slice(tijd.index + tijd[0].length).trim();
                        }
                        preview = preview.split(/\s*(?:Ongelezen|Unread|Non lus?)(?=[A-ZÀ-Ž])/)[0].trim();
                        const titelAttr = (it.querySelector('[data-tid="chat-list-item-title"]') ||
                            it.querySelector('span[title]'))?.getAttribute('title');
                        if (titelAttr) naam = titelAttr;
                        naam = naam.replace(/[,.]$/, '').slice(0, 60).trim();
                        return { naam, preview: preview.slice(0, 300), hadTijd: !!tijd };
                    }).filter(r => r.naam));
                })()
                """));
            var tekst = JsonSerializer.Deserialize<string>(json) ?? "[]";
            diagnose.Add($"rijen={tekst}");
            try
            {
                // Schermafdruk mét actieve filter: zonder deze is niet te zien of de
                // gefilterde lijst leeg was of dat de uitlezer ernaast keek.
                using var beeld = new MemoryStream();
                await _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld);
                File.WriteAllBytes(Path.Combine(DataDir, "teams-filter-screen.png"),
                    beeld.ToArray());
            }
            catch
            {
                // Alleen diagnose.
            }
            using var doc = JsonDocument.Parse(tekst);
            var ruis = new System.Text.RegularExpressions.Regex(
                "de opname is klaar|opname is klaar|recording is (ready|available)|" +
                "transcript is (ready|available|now available)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var kandidaten = doc.RootElement.EnumerateArray()
                .Select(e => (Bericht: new TeamsBericht(
                        e.GetProperty("naam").GetString() ?? "",
                        e.GetProperty("preview").GetString() ?? ""),
                    HadTijd: e.TryGetProperty("hadTijd", out var t) && t.GetBoolean()))
                .Where(k => k.Bericht.Naam.Length > 0 &&
                    !ruis.IsMatch(k.Bericht.Naam + " " + k.Bericht.Preview))
                .DistinctBy(k => k.Bericht.Naam)
                .ToList();
            // Alleen rijen die op een echte chat lijken: met tijdstip of met een preview
            // (desnoods uit de volledige scrape). Kale naam-items houden anders eeuwig een
            // cockpitrij in leven.
            var spook = kandidaten.Where(k => !k.HadTijd && k.Bericht.Preview.Length == 0 &&
                    !(scrapePreviews.TryGetValue(k.Bericht.Naam, out var sp) && sp.Length > 0))
                .Select(k => k.Bericht.Naam).ToList();
            if (spook.Count > 0)
            {
                diagnose.Add($"spookrijen={string.Join(",", spook)}");
            }
            var rijen = kandidaten
                .Where(k => !spook.Contains(k.Bericht.Naam))
                .Select(k => k.Bericht)
                .ToList();
            // Meer dan 30 "ongelezen" chats is geen geloofwaardige filteruitkomst.
            diagnose.Add($"uitslag={rijen.Count}");
            return rijen.Count > 30 ? null : rijen;
        }
        finally
        {
            try
            {
                if (herstelNodig)
                {
                    await ZetOngelezenFilterAsync(aan: false);
                }
            }
            catch
            {
                // De volgende beurt zet de filter alsnog terug (ZetOngelezenFilterAsync
                // controleert de stand voordat hij klikt).
            }
            try
            {
                File.WriteAllText(Path.Combine(DataDir, "teams-filter-debug.txt"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" + string.Join("\r\n", diagnose));
            }
            catch
            {
                // Alleen diagnose.
            }
        }
    }

    /// <summary>
    /// Goedkope vingerafdruk van de zichtbare bovenkant van de chatlijst: per rij de naam en
    /// of hij ongelezen is. Eén JS-rondje, geen scrollen — genoeg om te zien of er iets
    /// veranderd is sinds de vorige beurt.
    /// </summary>
    private async Task<string> VingerafdrukAsync()
    {
        try
        {
            var ruw = await JsAsync(MetSelector(
                """
                (function () {
                    let items = [...document.querySelectorAll(__SELECTOR__)];
                    items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                    if (items.length < 5) return '';
                    // De volledige rijtekst (naam + tijdstip + preview) én de titelbadge
                    // ("(2) Chat | …"): alleen de naam was te grof — een nieuw bericht in de
                    // chat die al bovenaan stond, veranderde de vingerafdruk dan niet en de
                    // snelweg bleef tot tien beurten lang de oude uitslag teruggeven.
                    return document.title.split('|')[0].trim() + '|' + items.length + '|' +
                        items.slice(0, 25).map(it =>
                            ((it.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 80)))
                        .join(';');
                })()
                """));
            return JsonSerializer.Deserialize<string>(ruw) ?? "";
        }
        catch
        {
            return ""; // geen afdruk = gewoon de volledige scrape doen
        }
    }

    /// <summary>
    /// Eén stuk JavaScript in de Teams-sessie draaien (diagnose, zie de CLI --teamsjs).
    /// Teams wijzigt zijn DOM geregeld; dan moet je ter plekke kunnen kijken.
    /// </summary>
    public async Task<string> DiagnoseJsAsync(string script, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                return "(niet ingelogd)";
            }
            // De Teams-web-app heeft na het opstarten tijd nodig; anders meet je een lege DOM.
            for (var i = 0; i < 60; i++)
            {
                var n = await JsAsync("document.querySelectorAll('[data-tid]').length");
                if (int.TryParse(n, out var aantal) && aantal > 20)
                {
                    break;
                }
                await Task.Delay(500, ct);
            }
            return await JsAsync(script);
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Diagnose voor de bubbelweergave (CLI --teamschat): opent de genoemde chat en dumpt
    /// per bericht de DOM-structuur — welke elementen kandidaat-auteur zijn en wat de
    /// huidige selector oplevert. Teams wijzigt zijn DOM geregeld; dan moet je ter plekke
    /// kunnen kijken zonder de leescode te slopen.
    /// </summary>
    public async Task<string> DiagnoseChatAsync(string naam, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                return "(niet ingelogd)";
            }
            // De Teams-web-app heeft na het opstarten ruim tijd nodig ("We stellen dingen
            // voor u in…"): wachten tot er echt chatrijen staan, niet op een elemententelling.
            await WachtOpRijenAsync(90, 500, ct);
            await TerugNaarChatlijstAsync();
            if (naam.Length == 0)
            {
                // Zonder naam: de chatlijst tonen, zodat je weet welke naam je moet meegeven.
                return await JsAsync(MetSelector(
                    """
                    (function () {
                        let items = [...document.querySelectorAll(__SELECTOR__)];
                        items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                        return JSON.stringify(items.map(it =>
                            ((it.querySelector('span[title]')?.getAttribute('title')) ||
                             it.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 60)));
                    })()
                    """));
            }
            var geklikt = await KlikTrustedAsync(MetSelector(
                $$"""
                (() => {
                    const naam = {{JsonSerializer.Serialize(naam)}};
                    let items = [...document.querySelectorAll(__SELECTOR__)];
                    items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                    // Eerst op de chattitel matchen; de rijtekst pas als laatste redmiddel,
                    // want de berichtpreview van een ándere chat kan de naam ook bevatten.
                    const titel = it =>
                        (it.querySelector('span[title]')?.getAttribute('title') || '').trim();
                    return items.find(it => titel(it) === naam) ||
                        items.find(it => titel(it).includes(naam)) ||
                        items.find(it => (it.textContent || '').includes(naam)) || null;
                })()
                """));
            if (!geklikt)
            {
                return $"chat \"{naam}\" niet gevonden";
            }
            await Task.Delay(2500, ct);
            var dump = await JsAsync(
                """
                (function () {
                    let msgs = [...document.querySelectorAll(
                        '[data-tid="chat-pane-message"], [id^="message-body-"],' +
                        '[data-tid="message-wrapper"]')];
                    if (msgs.length === 0) {
                        msgs = [...document.querySelectorAll(
                            '[class*="fui-ChatMessage"], [class*="fui-ChatMyMessage"], [data-mid]')];
                    }
                    const kort = e => {
                        const cls = (typeof e.className === 'string' ? e.className : '')
                            .split(/\s+/).filter(c => c).slice(0, 4).join(' ');
                        return e.tagName.toLowerCase() +
                            (e.getAttribute('data-tid') ? `[tid=${e.getAttribute('data-tid')}]` : '') +
                            (e.getAttribute('data-testid') ? `[testid=${e.getAttribute('data-testid')}]` : '') +
                            (cls ? `.${cls.slice(0, 90)}` : '');
                    };
                    return JSON.stringify(msgs.slice(-8).map(m => {
                        // De body is niet per se de wrapper: kijk ook in de omliggende
                        // fui-ChatMessage voor auteur en tijdstip.
                        const wrap = m.closest('[class*="fui-ChatMessage"], [class*="fui-ChatMyMessage"]')
                            ?.parentElement || m.parentElement || m;
                        const mid = m.getAttribute('data-mid') || '';
                        const el = naam => {
                            const e = document.getElementById(naam + '-' + mid);
                            return !e ? null : kort(e) + ' => ' +
                                (e.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 50);
                        };
                        return {
                            wortel: kort(m),
                            mid,
                            auteurEl: el('author'),
                            tijdEl: el('timestamp'),
                            inhoudEl: el('content'),
                            tekst: (m.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 60),
                        };
                    }), null, 1);
                })()
                """);
            await ParkeerOpConceptenAsync();
            return dump;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Diagnose (CLI --teamsdiag): opent een chat in de verborgen sessie, draait er een stuk
    /// JavaScript in (mag een Promise opleveren) en maakt optioneel een screenshot van hoe
    /// Teams de chat zelf toont — referentiebeeld voor de bubbelweergave. Opent de chat, dus
    /// Teams markeert hem als gelezen.
    /// </summary>
    public async Task<string> DiagnoseInChatAsync(string naam, string script, string screenshotPad,
        CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            // Koude CLI-sessie: Teams doet er ruim over voor de app er staat.
            if (!await StartAsync(ct, wachtSeconden: 120))
            {
                return "(niet ingelogd)";
            }
            await TerugNaarChatlijstAsync();
            await WachtOpRijenAsync(90, 500, ct);
            if (naam.Length > 0)
            {
                await OpenChatAsync(naam, ct);
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
                        uit = JsonSerializer.Deserialize<string>(klaar) ?? "";
                        break;
                    }
                }
            }
            if (screenshotPad.Length > 0)
            {
                await Task.Delay(800, ct);
                using var beeld = new MemoryStream();
                await _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld);
                File.WriteAllBytes(screenshotPad, beeld.ToArray());
            }
            await ParkeerOpConceptenAsync();
            return uit;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// De map "Chats" in de zijbalk kan ingeklapt staan (Teams onthoudt dat over sessies;
    /// dan rendert de lijst nul rijen en vindt geen enkele zoektocht een chat). Uitklappen
    /// via een trusted klik op de map, en als de Fluent-tree die negeert via focus + Enter
    /// (toetsenbordroute) — daarna verifiëren op aria-expanded.
    /// </summary>
    private async Task OntvouwChatsAsync()
    {
        const string Ingeklapt =
            "document.querySelector('[role=\"treeitem\"][data-item-type=\"chats\"][aria-expanded=\"false\"]')";
        if (await JsAsync($"!!({Ingeklapt})") != "true")
        {
            return;
        }
        // Route 1: klik op de kop van de map (het label, niet het midden van de rij).
        await KlikTrustedAsync(
            $"({Ingeklapt})?.querySelector('[data-testid*=\"folder\"], span, div') || {Ingeklapt}");
        await Task.Delay(700);
        if (await JsAsync($"!!({Ingeklapt})") != "true")
        {
            LogKlik("map Chats uitgeklapt via klik");
            return;
        }
        // Route 2: focus op het treeitem en Enter (Fluent-tree: toggelt de map).
        await JsAsync($"(function () {{ const t = {Ingeklapt}; if (t) {{ t.setAttribute('tabindex', '0'); t.focus(); }} return true; }})()");
        await Task.Delay(200);
        await CdpEnterAsync();
        await Task.Delay(700);
        LogKlik("map Chats " + (await JsAsync($"!!({Ingeklapt})") == "true"
            ? "blijft ingeklapt (klik én Enter negeerd)" : "uitgeklapt via Enter"));
    }

    /// <summary>
    /// Wacht tot de chatlijst echt rijen rendert (≥ 5). Staat de map "Chats" ingeklapt, dan
    /// komen die rijen nooit — daarom wordt onderweg uitgeklapt zodra de map er staat.
    /// True zodra er rijen zijn.
    /// </summary>
    private async Task<bool> WachtOpRijenAsync(int pogingen, int pauzeMs, CancellationToken ct)
    {
        for (var i = 0; i < pogingen; i++)
        {
            var rijen = await JsAsync($"document.querySelectorAll({RijSelector}).length");
            if (int.TryParse(rijen, out var n) && n >= 5)
            {
                return true;
            }
            if (i % 2 == 1)
            {
                await OntvouwChatsAsync();
            }
            await Task.Delay(pauzeMs, ct);
        }
        return false;
    }

    /// <summary>Staat de chatlijst al open? (Dan hoeft er niet genavigeerd te worden.)</summary>
    private async Task<bool> OpChatlijstAsync()
    {
        // Rijen tellen is niet genoeg: de agendaweergave heeft dezelfde list-items, en de
        // sessie springt daar vanzelf naartoe. De filterknop bleek niet te discrimineren
        // (die staat er ook op de agenda), de paginatitel wél: Teams zet daar "Chat" of
        // "Calendar" in. Bleef de drift onopgemerkt, dan schraapten we de agenda én viel de
        // inlogcontrole om — dat was de bron van de herhaalde Teams-fouten.
        var stand = await JsAsync(
            $$"""
            JSON.stringify({
                rijen: document.querySelectorAll({{RijSelector}}).length,
                chatweergave: /chat/i.test(document.title),
            })
            """);
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(stand) ?? stand);
            return doc.RootElement.GetProperty("chatweergave").GetBoolean() &&
                doc.RootElement.GetProperty("rijen").GetInt32() >= 5;
        }
        catch
        {
            return false; // niet vast te stellen: dan liever navigeren
        }
    }

    /// <summary>Keert vanuit de Concepten-parkeerstand terug naar de volledige chatlijst.</summary>
    private async Task TerugNaarChatlijstAsync()
    {
        try
        {
            await KlikTrustedAsync(
                """
                document.querySelector('[data-tid="back-button"], button[aria-label*="Terug"],' +
                    'button[aria-label*="Back"]') ||
                [...document.querySelectorAll('[data-tid="app-bar"] [aria-label], [role="tab"]')]
                    .find(el => /^(chat|chatten)\b/i.test((el.getAttribute('aria-label') ||
                        el.textContent || '').trim()))
                """);
            // Wachten tot de rijen er zijn in plaats van blind 1,5 s (max. 1,5 s); klapt
            // onderweg de map "Chats" uit als die dicht staat.
            await WachtOpRijenAsync(10, 150, CancellationToken.None);
        }
        catch
        {
            // Best effort.
        }
    }

    private async Task<bool> IsIngelogdAsync() =>
        // Strikt: het uitgelogde Teams-shell bevat ook al app-layout-elementen, maar dan mét
        // een aanmeldknop (me-control-signin-…). Alleen ingelogd als die knop er níét is.
        await JsAsync(
            """
            (location.hostname.endsWith('teams.cloud.microsoft') ||
             location.hostname.endsWith('teams.microsoft.com')) &&
            !document.querySelector(
                '[data-tid="me-control-signin-trigger"],' +
                '[data-tid="me-control-avatar-signin-button"], [data-tid="signin-button"]') &&
            // Ankers uit twee generaties Teams plus een algemene ondergrens: deze build
            // gebruikt data-testid in plaats van data-tid, en op de agendaweergave staan de
            // oude ankers er helemaal niet. Alleen daarop toetsen leverde "niet ingelogd" op
            // terwijl de sessie prima was — met een pauze van een half uur als gevolg.
            !!(document.querySelector('[data-tid="app-bar"]') ||
               document.querySelector('[data-tid*="chat-list"]') ||
               document.querySelector('[data-tid="left-rail"]') ||
               document.querySelector('[data-tid="app-layout-area--main"]') ||
               document.querySelector('[data-testid="list-item"]') ||
               document.querySelector('[data-testid^="simple-collab-left-rail"]') ||
               document.querySelectorAll('[role="treeitem"]').length > 5)
            """) == "true";

    public sealed record TeamsBericht(string Naam, string Preview);

    /// <summary>
    /// Chats met ongelezen berichten (naam + preview van het laatste bericht), zonder iets
    /// te openen (dus zonder leesbevestigingen). De platte itemtekst wordt uiteengerafeld
    /// op het tijdstip: "[Ongelezen]Naam12:02Preview…" → naam en preview apart.
    /// </summary>
    public async Task<(int Totaal, List<TeamsBericht> Ongelezen, Dictionary<string, string> Previews)>
        OngelezenAsync(CancellationToken ct)
    {
        // Faseklok: Teams is de traagste bron van de ophaalbeurt, en zonder meting is niet
        // te zien of dat aan de herlaadbeurt, het renderen of het scrollen ligt. Eén regel
        // per beurt in %APPDATA%\WorkManager	eams-timing.txt.
        var klok = System.Diagnostics.Stopwatch.StartNew();
        var fasen = new List<string>();
        void Fase(string naam) => fasen.Add($"{naam}={klok.ElapsedMilliseconds / 1000.0:0.0}s");

        await _slot.WaitAsync(ct);
        Fase("slot");
        // Buiten de try, want de finally start de herlaadbeurt zodra deze beurt klaar is.
        var herlaadNodig = false;
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Teams is niet ingelogd — koppel opnieuw via 'Teams koppelen…'.");
            }
            Fase("start");
            // De verborgen pagina kan verouderen: badges van intussen (elders) gelezen chats
            // blijven staan. Daarvoor wordt er periodiek volledig herladen — maar dat kost
            // 15 à 25 seconden, want Teams start dan zijn hele web-app opnieuw op, en dat
            // was in z'n eentje de traagste stap van de hele ophaalbeurt.
            //
            // Elke minuut herladen is ook niet nodig: de pagina draait met
            // --disable-background-timer-throttling en houdt haar eigen websocket open, dus
            // nieuwe berichten komen tussendoor gewoon binnen. Vijf minuten is de afweging:
            // een badge die elders gelezen is, verdwijnt hooguit één pollronde later.
            // Eerste beurt van deze sessie: wél meteen herladen (de pagina kan nog in de
            // Concepten-parkeerstand of op een verouderde weergave staan). Daarna nooit meer
            // in de wachtrij van de gebruiker: is er een herlaadbeurt toe, dan lezen we eerst
            // de live lijst en herladen we erná op de achtergrond. Zo wacht geen enkele
            // ophaalbeurt nog twintig seconden op het opstarten van de Teams-web-app.
            herlaadNodig = DateTimeOffset.Now - _laatstHerladen > TimeSpan.FromMinutes(5);
            var eersteBeurt = _laatstHerladen == DateTimeOffset.MinValue;
            if (herlaadNodig && eersteBeurt)
            {
                await HerlaadKernAsync(ct);
                herlaadNodig = false;
            }
            Fase("herladen");
            // De sessie blijft tussen de beurten gewoon op de chatlijst staan. Parkeren op
            // Concepten is alleen nodig rond een herlaadbeurt (Teams heropent dan de laatst
            // actieve weergave en zou een echte chat kunnen openen); dat gebeurt nu in
            // HerlaadOpAchtergrondAsync. Het heen-en-weer klikken kostte 2,4 s per ronde.
            if (!await OpChatlijstAsync())
            {
                await TerugNaarChatlijstAsync();
            }
            Fase("chatlijst");
            // Diagnose: welke rij-selector levert hier iets op? (Zonder dit is niet te zien
            // waarom de snelweg hieronder overgeslagen wordt.)
            // Teams zet het aantal ongelezen chats zelf in de paginatitel ("(2) Chat | …").
            // Dat is een onafhankelijke controle op onze eigen telling: loopt die uiteen, dan
            // klopt de ongelezen-herkenning niet meer.
            fasen.Add("titel=" + (await JsAsync("document.title")).Trim('"').Split('|')[0].Trim());
            fasen.Add("rijen=" + await JsAsync($"document.querySelectorAll({RijSelector}).length"));
            // Wachten tot de (gevirtualiseerde) lijst echt gerenderd én stabiel is: na een
            // verse sessie (gewiste sitedata) druppelen de rijen binnen en staan titels
            // kort vet zonder dat de leesstand al gesynct is — scrapen vóór de lijst
            // stilstaat gaf valse "ongelezen"-rijen (1 rij, fontgewicht 700).
            // Snelweg: is de bovenkant van de lijst identiek aan de vorige beurt, dan is er
            // niets veranderd en kan de hele scrape (navigeren, stabiliseren, scrollen)
            // overgeslagen worden. Dat mag, omdat een chat met een nieuw bericht in Teams
            // altijd naar boven springt: verandert er iets aan ongelezen, dan verandert deze
            // vingerafdruk mee. Voor de zekerheid hooguit tien beurten op rij overslaan.
            if (_laatsteUitslag is { } vorige && _overgeslagen < 10)
            {
                var afdrukNu = await VingerafdrukAsync();
                if (afdrukNu.Length > 0 && afdrukNu == _laatsteVingerafdruk)
                {
                    _overgeslagen++;
                    Fase("ongewijzigd");
                    return vorige;
                }
            }
            _overgeslagen = 0;

            var vorigAantal = -1;
            for (var i = 0; i < 50; i++) // kleinere stapjes, dus meer rondjes voor dezelfde 25 s
            {
                var aantalRuw = await JsAsync(
                    """
                    document.querySelectorAll(
                        '[data-testid="list-item"], [data-tid^="chat-list-item"],' +
                        '[data-tid="chat-list"] [role="listitem"], [role="list"] [role="listitem"]').length
                    """);
                var aantal = int.TryParse(aantalRuw, out var n) ? n : 0;
                if (aantal >= 5 && aantal == vorigAantal)
                {
                    break; // twee metingen gelijk = de lijst staat stil
                }
                vorigAantal = aantal;
                await Task.Delay(500, ct);
            }
            Fase("stabiel");
            // De chatlijst is gevirtualiseerd: een asynchrone job scrolt erdoorheen en
            // verzamelt alle rijen; het resultaat komt in window.__wmTeams en wordt gepolld.
            await JsAsync(
                """
                (function () {
                    window.__wmTeams = null;
                    (async () => {
                        const gezien = new Map();
                        const lees = () => {
                            let items = [...document.querySelectorAll(
                                '[data-tid^="chat-list-item"], [data-tid="chat-list"] [role="listitem"],' +
                                '[data-testid="list-item"], [role="list"] [role="listitem"]')];
                            // Alleen de binnenste (echte) chatrijen: containers die zelf weer
                            // rijen bevatten overslaan.
                            items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                            for (const it of items) {
                                const label = it.getAttribute('aria-label') ||
                                    it.querySelector('[aria-label]')?.getAttribute('aria-label') || '';
                                const titelEl = it.querySelector('[data-tid="chat-list-item-title"]') ||
                                    it.querySelector('span[title]') || it;
                                let ruw = (it.textContent || '').replace(/\s+/g, ' ').trim();
                                ruw = ruw.replace(/^(chats?|ongelezen|unread|non lus?)[,.:]?\s*/i, '');
                                // Knippen op het tijdstip ("12:51") óf de datum ("27-7") die
                                // Teams tussen naam en preview zet.
                                const tijd = ruw.match(/\d{1,2}:\d{2}|\d{1,2}-\d{1,2}/);
                                let naam = ruw, preview = '';
                                if (tijd) {
                                    naam = ruw.slice(0, tijd.index).trim();
                                    preview = ruw.slice(tijd.index + tijd[0].length).trim();
                                }
                                preview = preview.split(/\s*(?:Ongelezen|Unread|Non lus?)(?=[A-ZÀ-Ž])/)[0].trim();
                                const titelAttr = titelEl.getAttribute && titelEl.getAttribute('title');
                                if (titelAttr) naam = titelAttr;
                                naam = naam.replace(/[,.]$/, '').slice(0, 60).trim();
                                if (!naam || gezien.has(naam)) continue;
                                const stijl = getComputedStyle(titelEl);
                                const viaLabel = /ongelezen|unread|non lu/i.test(label);
                                const badgeEl = it.querySelector('[data-tid*="unread"], [class*="unread"]');
                                const viaBadge = !!badgeEl && badgeEl.offsetParent !== null &&
                                    ((badgeEl.textContent || '').trim().length > 0 ||
                                     /ongelezen|unread/i.test(badgeEl.getAttribute('aria-label') || ''));
                                const gewicht = parseInt(stijl.fontWeight, 10); // alleen diagnose
                                // Meeting-notificaties ("De opname is klaar", transcripts) zijn
                                // intern wel ongelezen maar geen echte berichten: overslaan.
                                // Alleen echte systeemnotificaties (opname/transcript klaar)
                                // wegfilteren — niet elke chat die het wóórd 'transcript' bevat,
                                // anders verdwijnt een workshop-chat met echte discussie.
                                const meetingRuis = /de opname is klaar|opname is klaar|recording is (ready|available)|transcript is (ready|available|now available)/i
                                    .test(naam + ' ' + preview);
                                // Alleen het expliciete signaal telt (aria-label of badge).
                                // Vetgedrukt (gewicht >= 600) was een terugvaloptie, maar gaf
                                // valse treffers: tijdens een verse sync rendert Teams titels
                                // eerst vet vóórdat de leesstand van de server binnen is.
                                const ongelezen = (viaLabel || viaBadge) && !meetingRuis;
                                gezien.set(naam, { naam, preview: preview.slice(0, 300), ongelezen,
                                    viaLabel, viaBadge, gewicht,
                                    badge: badgeEl ? {
                                        tag: badgeEl.tagName,
                                        tid: badgeEl.getAttribute('data-tid') || '',
                                        cls: (badgeEl.getAttribute('class') || '').slice(0, 80),
                                        tekst: (badgeEl.textContent || '').trim().slice(0, 30),
                                        aria: (badgeEl.getAttribute('aria-label') || '').slice(0, 60),
                                        zichtbaar: badgeEl.offsetParent !== undefined
                                            ? badgeEl.offsetParent !== null
                                            : 'svg',
                                    } : null });
                            }
                            return items[0];
                        };
                        const eerste = lees();
                        let scroller = eerste;
                        while (scroller && scroller !== document.body) {
                            const s = getComputedStyle(scroller);
                            if (/(auto|scroll)/.test(s.overflowY) &&
                                scroller.scrollHeight > scroller.clientHeight + 10) break;
                            scroller = scroller.parentElement;
                        }
                        if (scroller && scroller !== document.body) {
                            // Ruime limieten: de lijst telt intussen ruim 100 chats en bij
                            // een te krappe cap vallen de onderste (mogelijk ongelezen) af.
                            // Wél vroeg stoppen als er al vijftig chats gezien zijn en er in
                            // drie schermen niets ongelezens meer bijkwam: Teams zet een chat
                            // met nieuwe berichten bovenaan, dus dieper zoeken levert niets.
                            let leegOpRij = 0;
                            const telOngelezen = () =>
                                [...gezien.values()].filter(c => c.ongelezen).length;
                            let vorigeOngelezen = telOngelezen();
                            for (let i = 0; i < 40 && gezien.size < 250; i++) {
                                scroller.scrollTop += scroller.clientHeight * 0.8;
                                await new Promise(r => setTimeout(r, 200));
                                const voor = gezien.size;
                                lees();
                                const nu = telOngelezen();
                                leegOpRij = nu > vorigeOngelezen ? 0 : leegOpRij + 1;
                                vorigeOngelezen = nu;
                                if (gezien.size >= 50 && leegOpRij >= 3) break;
                                if (gezien.size === voor &&
                                    scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 5) break;
                            }
                            scroller.scrollTop = 0;
                        }
                        const alle = [...gezien.values()];
                        window.__wmTeams = {
                            totaal: alle.length,
                            ongelezen: alle.filter(c => c.ongelezen)
                                .map(c => ({ naam: c.naam, preview: c.preview })),
                            // Alle zijbalkpreviews: de cockpit ziet zo of Maarten intussen
                            // zelf geantwoord heeft ("U: …") en vult lege previews van de
                            // filterlezing aan.
                            previews: alle.map(c => ({ naam: c.naam, preview: c.preview })),
                            diagnose: alle.map(c => ({ naam: c.naam, ongelezen: c.ongelezen,
                                preview: c.preview.slice(0, 60),
                                viaLabel: c.viaLabel, viaBadge: c.viaBadge, gewicht: c.gewicht,
                                badge: c.badge })),
                            // Vindt de uitlezer niets, dan de DOM in kaart brengen zodat de
                            // selectors op de echte structuur afgestemd kunnen worden.
                            domDiag: alle.length > 0 ? null : {
                                url: location.href.slice(0, 150),
                                tids: [...new Set([...document.querySelectorAll('[data-tid]')]
                                    .map(e => e.getAttribute('data-tid')))].slice(0, 80),
                                listitems: document.querySelectorAll('[role="listitem"]').length,
                                treeitems: document.querySelectorAll('[role="treeitem"]').length,
                                rows: document.querySelectorAll('[role="row"]').length,
                                opties: document.querySelectorAll('[role="option"]').length,
                            },
                        };
                    })();
                    return true;
                })()
                """);
            Fase("script");
            var json = """{"totaal":0,"ongelezen":[]}""";
            for (var i = 0; i < 75; i++) // max. ~15 s op het scrollen wachten
            {
                await Task.Delay(200, ct);
                var klaar = await JsAsync("JSON.stringify(window.__wmTeams)");
                if (klaar is not ("null" or "\"null\""))
                {
                    json = System.Text.Json.JsonSerializer.Deserialize<string>(klaar) ?? json;
                    break;
                }
            }
            try
            {
                // Diagnose: per chat waarom hij (niet) als ongelezen geldt.
                File.WriteAllText(Path.Combine(DataDir, "teams-debug.json"), json);
                // Plus een screenshot van de verborgen pagina: zo is te zien wat de
                // sessie werkelijk toont (ingelogd? actuele lijst? badges?).
                using var beeld = new MemoryStream();
                await _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld);
                File.WriteAllBytes(Path.Combine(DataDir, "teams-screen.png"), beeld.ToArray());
            }
            catch
            {
                // Alleen diagnose.
            }
            using var doc = JsonDocument.Parse(json);
            var previews = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("previews", out var previewLijst))
            {
                foreach (var e in previewLijst.EnumerateArray())
                {
                    previews[e.GetProperty("naam").GetString() ?? ""] =
                        e.GetProperty("preview").GetString() ?? "";
                }
            }
            var uitslag = (
                Totaal: doc.RootElement.GetProperty("totaal").GetInt32(),
                Ongelezen: doc.RootElement.GetProperty("ongelezen").EnumerateArray()
                    .Select(e => new TeamsBericht(
                        e.GetProperty("naam").GetString() ?? "",
                        e.GetProperty("preview").GetString() ?? ""))
                    .Where(b => b.Naam.Length > 0)
                    .ToList(),
                Previews: previews);
            // Sinds september 2026 zet Teams géén "unread"-badge of aria-label meer op de
            // chatrijen (alle rijen renderen gewicht 400, badge null), terwijl de zijbalk
            // wél een ongelezen-teller toont — de heuristiek hierboven vindt dan niets.
            // De filterknop "Ongelezen" boven de chatlijst ís er nog: die even aanzetten
            // toont uitsluitend de ongelezen chats, wat elke DOM-gok overbodig maakt.
            var gefilterd = await FilterOngelezenAsync(previews, ct);
            if (gefilterd is not null)
            {
                uitslag.Ongelezen.Clear();
                // De gefilterde rijen missen soms hun preview (de rij rendert daar zonder
                // tijdstip, waar de tekstsplitsing op steunt): dan de preview uit de
                // volledige scrape overnemen — de cockpit sleutelt rijen op naam+preview,
                // en een lege preview zou nieuwe berichten onzichtbaar maken.
                uitslag.Ongelezen.AddRange(gefilterd.Select(b =>
                    b.Preview.Length == 0 && uitslag.Previews.TryGetValue(b.Naam, out var p)
                        ? b with { Preview = p }
                        : b));
                fasen.Add("viaFilter");
            }
            // Alleen een geloofwaardige uitslag als vertrekpunt onthouden: bij een half
            // gerenderde lijst zou de volgende beurt anders een verkeerde stand hergebruiken.
            fasen.Add($"gevonden={uitslag.Ongelezen.Count}/{uitslag.Totaal}");
            // Een beurt waarin de filter mislukte, is geen vertrekpunt: anders hergebruikt de
            // vingerafdruk-snelweg die (onvolledige) uitslag tot tien beurten lang, en een
            // ongelezen chat uit precies die beurt blijft zo tot twintig minuten onzichtbaar.
            if (uitslag.Totaal >= 10 && gefilterd is not null)
            {
                _laatsteUitslag = uitslag;
                _laatsteVingerafdruk = await VingerafdrukAsync();
            }
            return uitslag;
        }
        finally
        {
            Fase("klaar");
            if (herlaadNodig)
            {
                // Buiten de ophaalbeurt om: de cockpit is nu klaar, de pagina wordt intussen
                // vers geladen zodat de volgende ronde weer een actuele lijst ziet.
                _ = HerlaadOpAchtergrondAsync();
            }
            try
            {
                var pad = Path.Combine(DataDir, "teams-timing.txt");
                var regels = File.Exists(pad)
                    ? File.ReadAllLines(pad).TakeLast(100).ToList()
                    : new List<string>();
                regels.Add($"{DateTime.Now:HH:mm:ss}  " + string.Join("  ", fasen));
                File.WriteAllLines(pad, regels);
            }
            catch
            {
                // Alleen diagnose.
            }
            _slot.Release();
        }
    }

    /// <summary>
    /// Eén item uit een Teams-chat. Soort "" = gewoon bericht; "divider" = datumscheiding
    /// (Tekst = "Gisteren", "donderdag", "maandag 22 september 2025"); "systeem" = melding
    /// ("X heeft Y toegevoegd"). Tijd is de zichtbare vorm ("Gisteren 13:51"), TijdVol de
    /// volledige ("Gisteren om 13:51"), Iso het exacte tijdstip.
    /// </summary>
    public sealed record TeamsChatBericht(
        string Tijd, string Auteur, bool Uitgaand, string Tekst, string Beeld = "",
        bool Foto = false)
    {
        public string Soort { get; init; } = "";
        public string Iso { get; init; } = "";
        public string TijdVol { get; init; } = "";
        /// <summary>Opgeschoonde HTML van de inhoud (opmaak, lijsten, links, @vermeldingen).</summary>
        public string Html { get; init; } = "";
        /// <summary>Profielfoto van de afzender (data-URL), leeg = initialen.</summary>
        public string AvatarUrl { get; init; } = "";
        public TeamsCitaat? Citaat { get; init; }
        public List<string> Bijlagen { get; init; } = [];
        /// <summary>Reacties als "👍 😂 2" (aantal alleen boven 1).</summary>
        public string Reacties { get; init; } = "";
        public bool EigenReactie { get; init; }
        /// <summary>Uitgaand: "Gezien", "Verzonden" … (aria-label van het statusicoon).</summary>
        public string Status { get; init; } = "";
        public bool Bewerkt { get; init; }
        /// <summary>Maarten wordt in dit bericht @vermeld.</summary>
        public bool Vermeld { get; init; }

        [System.Text.Json.Serialization.JsonIgnore]
        public DateTimeOffset? Tijdstip =>
            DateTimeOffset.TryParse(Iso, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t.ToLocalTime() : null;
    }

    /// <summary>Geciteerd (antwoord) of doorgestuurd bericht bovenin een bubbel.</summary>
    public sealed record TeamsCitaat(string Wie, string Wanneer, string Wat, bool Doorgestuurd);

    /// <summary>
    /// Opent een chat uit de (gevirtualiseerde) chatlijst met een echte klik — zo nodig
    /// scrollend tot de rij gerenderd is — en controleert daarna dat hij ook écht
    /// openstaat: een gemiste klik laat gewoon de bovenste chat zien en zou stilletjes
    /// de verkeerde berichten opleveren. Aanroeper houdt het slot vast.
    /// </summary>
    private async Task OpenChatAsync(string naam, CancellationToken ct)
    {
        var zoekRij =
            $$"""
            (() => {
                const naam = {{JsonSerializer.Serialize(naam)}};
                let items = [...document.querySelectorAll(
                    '[data-testid="list-item"], [data-tid^="chat-list-item"],' +
                    '[data-tid="chat-list"] [role="listitem"], [role="list"] [role="listitem"]')];
                items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                // Eerst op de chattitel matchen; de rijtekst pas als laatste redmiddel,
                // want de berichtpreview van een ándere chat kan de naam ook bevatten.
                const titel = it =>
                    (it.querySelector('span[title]')?.getAttribute('title') || '').trim();
                return items.find(it => titel(it) === naam) ||
                    items.find(it => titel(it).includes(naam)) ||
                    items.find(it => (it.textContent || '').includes(naam)) || null;
            })()
            """;
        var verifieer =
            $$"""
            (function () {
                const naam = {{JsonSerializer.Serialize(naam)}};
                // Groepschats: zijbalknaam "Henny, Kevin" ≈ kop met statusbolletjes
                // ertussen — daarom per deelnaam controleren.
                const delen = naam.split(',').map(s => s.trim()).filter(s => s.length > 1);
                const kop = [...document.querySelectorAll('h2')]
                    .map(h => h.textContent || '').join(' ') + ' ' + document.title;
                return delen.length > 0 && delen.every(d => kop.includes(d));
            })()
            """;
        for (var poging = 0; poging < 6; poging++)
        {
            var gevonden = await KlikTrustedAsync(zoekRij);
            if (!gevonden)
            {
                // Gevirtualiseerde lijst: alleen zichtbare rijen bestaan in de DOM,
                // dus scrollend zoeken tot de gevraagde chat gerenderd is.
                for (var stap = 0; stap < 25 && !gevonden; stap++)
                {
                    var verder = await JsAsync(
                        """
                        (function () {
                            let scroller = document.querySelector(
                                '[data-testid="list-item"], [data-tid^="chat-list-item"]');
                            while (scroller && scroller !== document.body) {
                                const s = getComputedStyle(scroller);
                                if (/(auto|scroll)/.test(s.overflowY) &&
                                    scroller.scrollHeight > scroller.clientHeight + 10) break;
                                scroller = scroller.parentElement;
                            }
                            if (!scroller || scroller === document.body) return 'geen-scroller';
                            const voor = scroller.scrollTop;
                            scroller.scrollTop += scroller.clientHeight * 0.8;
                            return scroller.scrollTop > voor ? 'gescrold' : 'einde';
                        })()
                        """);
                    if (!verder.Contains("gescrold"))
                    {
                        break;
                    }
                    await Task.Delay(250, ct);
                    gevonden = await KlikTrustedAsync(zoekRij);
                }
            }
            if (gevonden)
            {
                await Task.Delay(2500, ct); // chat laten laden
                if (await JsAsync(verifieer) == "true")
                {
                    return;
                }
                // De klik gaf de rij focus maar navigeerde niet (rij nét hermount):
                // Enter als tweede route, dat opent de gefocuste rij alsnog.
                await CdpEnterAsync();
                await Task.Delay(1800, ct);
                if (await JsAsync(verifieer) == "true")
                {
                    return;
                }
                LogKlik($"verify mis voor \"{naam}\": kop = " + await JsAsync(
                    """
                    ([...document.querySelectorAll('h2')]
                        .map(h => (h.textContent || '').trim()).join(' ~ ') +
                     ' | titel: ' + document.title).slice(0, 200)
                    """));
                try
                {
                    await using var beeld = File.Create(
                        Path.Combine(DataDir, $"teams-klik-mis-{poging}.png"));
                    await _web!.CoreWebView2!.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, beeld);
                }
                catch
                {
                    // Alleen diagnose.
                }
            }
            await Task.Delay(1000, ct);
        }
        await ParkeerOpConceptenAsync();
        throw new InvalidOperationException(
            $"Chat \"{naam}\" kon niet geopend worden in de Teams-lijst.");
    }

    /// <summary>
    /// De laatste berichten uit een Teams-chat, gestructureerd voor de bubbelweergave —
    /// alles wat Teams zelf toont: auteur en tijd (met ISO-tijdstip), avatar, opmaak (als
    /// opgeschoonde HTML), citaten, bijlagen, reacties, leesstatus, vermeldingen, datum-
    /// scheidingen en systeemmeldingen. Opent de chat in de verborgen sessie (Teams
    /// markeert hem daardoor als gelezen) en leest de gerenderde berichten uit.
    /// </summary>
    public async Task<List<TeamsChatBericht>> LaatsteBerichtenAsync(
        string naam, int max, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            // Ruim wachten: een koude sessie (CLI-diagnose, of net herbouwd) heeft 30-60 s
            // nodig; een warme sessie antwoordt toch meteen.
            if (!await StartAsync(ct, wachtSeconden: 90))
            {
                throw new InvalidOperationException("Teams is niet ingelogd.");
            }
            await TerugNaarChatlijstAsync(); // vanuit de Concepten-parkeerstand
            // Koude start (net opgebouwde sessie): de Teams-app doet er lang over voordat
            // de chatlijst er staat ("We stellen dingen voor u in…") — wachten op echte rijen.
            await WachtOpRijenAsync(60, 500, ct);
            await OpenChatAsync(naam, ct);

            // Asynchrone verzameljob in de pagina (zelfde patroon als WhatsApp): afbeeldingen
            // en avatars moeten per stuk geladen en naar data-URL's omgezet worden, dus het
            // resultaat komt in window.__wmTeamsMsgs en wordt hieronder gepolld.
            await JsAsync(
                $$"""
                (function () {
                    window.__wmTeamsMsgs = null;
                    (async () => {
                      try {
                        const wacht = ms => new Promise(r => setTimeout(r, ms));
                        const ESC = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;')
                            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
                        // Delen van een bericht die geen inhoud zijn (los uitgelezen of ruis).
                        const RUIS = '[data-tid="quoted-reply-card"], [data-tid="forward-message-card"], ' +
                            '[data-tid="file-attachment-grid"], [data-testid="lazy-image-wrapper"], ' +
                            '[data-tid="message-actions-menu-hidden-button"], button, [class*="__details"], ' +
                            '[data-tid="diverse-reaction-summary"], [class*="__reactions"], [id^="read-status-icon-"]';
                        const isEmoji = n => n.tagName === 'IMG' && (n.closest('[data-tid="emoticon-renderer"]') ||
                            /Emoji/i.test(n.getAttribute('itemtype') || ''));
                        const isMention = n => /Mention/i.test(n.getAttribute('itemtype') || '') ||
                            (n.getAttribute('data-tid') || '').includes('mention');
                        // Opgeschoonde HTML: alleen structuur en opmaak (geen classes, geen
                        // scripts), zodat de cockpit het in zijn eigen Teams-stijl kan tonen.
                        const schoon = n => {
                            if (n.nodeType === 3) return ESC(n.nodeValue);
                            if (n.nodeType !== 1) return '';
                            const tag = n.tagName.toLowerCase();
                            if (n.matches(RUIS) || tag === 'script' || tag === 'style' || tag === 'svg') return '';
                            if (tag === 'img') return isEmoji(n) ? ESC(n.alt || '') : '';
                            if (tag === 'br') return '<br>';
                            if (isMention(n)) return '<span class="tm-m">' +
                                ESC((n.textContent || '').trim().replace(/^@/, '')) + '</span>';
                            const kids = [...n.childNodes].map(schoon).join('');
                            switch (tag) {
                                case 'p': case 'div': return '<div>' + kids + '</div>';
                                case 'ul': case 'ol': case 'li': case 'blockquote': case 'pre': case 'code':
                                case 'u': case 'table': case 'tbody': case 'thead': case 'tr': case 'td': case 'th':
                                    return '<' + tag + '>' + kids + '</' + tag + '>';
                                case 'strong': case 'b': return '<b>' + kids + '</b>';
                                case 'em': case 'i': return '<i>' + kids + '</i>';
                                case 's': case 'strike': case 'del': return '<s>' + kids + '</s>';
                                case 'h1': case 'h2': case 'h3': case 'h4': return '<div><b>' + kids + '</b></div>';
                                case 'a': {
                                    const href = n.getAttribute('href') || '';
                                    return /^https?:/i.test(href)
                                        ? '<a href="' + ESC(href) + '">' + kids + '</a>' : kids;
                                }
                                default: return kids;
                            }
                        };
                        // Platte tekst mét regeleinden, emoji's en @vermeldingen (voor Claude).
                        const BLOK = /^(p|div|li|tr|h[1-6]|blockquote|pre|ul|ol|table)$/;
                        const tekstVan = n => {
                            if (n.nodeType === 3) return n.nodeValue;
                            if (n.nodeType !== 1) return '';
                            const tag = n.tagName.toLowerCase();
                            if (n.matches(RUIS) || tag === 'script' || tag === 'style' || tag === 'svg') return '';
                            if (tag === 'img') return isEmoji(n) ? (n.alt || '') : '';
                            if (tag === 'br') return '\n';
                            if (isMention(n)) return '@' + (n.textContent || '').trim().replace(/^@/, '');
                            let s = [...n.childNodes].map(tekstVan).join('');
                            if (tag === 'li') s = '• ' + s.trim();
                            if (tag === 'td' || tag === 'th') s = s.trim() + '\t';
                            return BLOK.test(tag) ? '\n' + s + '\n' : s;
                        };
                        const T = el => !el ? '' : tekstVan(el).replace(/[ \t]+\n/g, '\n')
                            .replace(/\n{3,}/g, '\n\n').replace(/ /g, ' ').trim();

                        const lijst = document.querySelector('[data-tid="message-pane-list-runway"]');
                        let items = lijst ? [...lijst.querySelectorAll('[data-tid="chat-pane-item"]')]
                            .filter(it => !it.parentElement.closest('[data-tid="chat-pane-item"]')) : [];
                        if (items.length === 0) {
                            // Oudere DOM's zonder lijst-runway: elk bericht is zijn eigen item.
                            items = [...document.querySelectorAll(
                                '[data-tid="chat-pane-message"], [id^="message-body-"], [data-tid="message-wrapper"]')];
                            if (items.length === 0) {
                                items = [...document.querySelectorAll(
                                    '[class*="fui-ChatMessage"], [class*="fui-ChatMyMessage"], [data-mid]')];
                            }
                            items = items.filter(it => !items.some(o => o !== it && o.contains(it)));
                        }
                        if (items.length === 0) {
                            window.__wmTeamsMsgs = { leeg: true, diag: {
                                paneMsg: document.querySelectorAll('[data-tid="chat-pane-message"]').length,
                                msgBody: document.querySelectorAll('[id^="message-body-"]').length,
                                fui: document.querySelectorAll('[class*="ChatMessage"]').length,
                                mid: document.querySelectorAll('[data-mid]').length,
                                mainTekst: (document.querySelector('[data-tid="app-layout-area--main"]')
                                    ?.textContent || '').slice(0, 120),
                            } };
                            return;
                        }
                        // Alleen de laatste {{max}} échte berichten, plus de scheidingen ertussen.
                        let teller = 0;
                        for (let i = items.length - 1; i >= 0; i--) {
                            if (items[i].querySelector('[data-tid="chat-pane-message"], [id^="message-body-"]') ||
                                items[i].matches('[data-tid="chat-pane-message"], [id^="message-body-"]')) {
                                if (++teller >= {{max}}) { items = items.slice(i); break; }
                            }
                        }

                        const paneel = document.querySelector('[data-tid="app-layout-area--main"]') || document.body;
                        const paneRect = paneel.getBoundingClientRect();
                        const avatarCache = new Map();
                        const avatarData = async src => {
                            if (!src) return '';
                            if (avatarCache.has(src)) return avatarCache.get(src);
                            let d = '';
                            try {
                                const resp = await fetch(src, { credentials: 'include' });
                                const bmp = await createImageBitmap(await resp.blob());
                                const cv = document.createElement('canvas');
                                cv.width = cv.height = 64;
                                const z = Math.min(bmp.width, bmp.height);
                                cv.getContext('2d').drawImage(bmp, (bmp.width - z) / 2, (bmp.height - z) / 2, z, z, 0, 0, 64, 64);
                                d = cv.toDataURL('image/jpeg', 0.8);
                            } catch { d = ''; }
                            avatarCache.set(src, d);
                            return d;
                        };
                        const uitkomst = [];
                        let fotoBudget = 8; // niet eindeloos downloaden bij een fotoreeks
                        // Totaalcap: de uiteindelijke HTML gaat via NavigateToString (limiet
                        // ±1,5 MB); daarboven zou de hele bubbelweergave wegvallen.
                        let fotoTekens = 0;
                        for (const it of items) {
                            const body = it.matches('[data-tid="chat-pane-message"], [id^="message-body-"]') ? it :
                                it.querySelector('[data-tid="chat-pane-message"], [id^="message-body-"], ' +
                                    '[data-tid="message-body-content"], [class*="fui-ChatMessage__body"], ' +
                                    '[class*="fui-ChatMyMessage__body"]');
                            if (!body) {
                                // Datumscheiding ("Gisteren", "donderdag", "maandag 22 september
                                // 2025") of systeemmelding ("X heeft Y toegevoegd").
                                const divider = it.querySelector('.fui-Divider[role="heading"], [class*="Divider__wrapper"]');
                                const t = T(divider || it).replace(/\s*\n\s*/g, ' ').trim();
                                if (!t) continue;
                                uitkomst.push(divider ? { divider: t.slice(0, 80) } : { systeem: t.slice(0, 300) });
                                continue;
                            }
                            if (it.querySelector('[data-tid="control-message-renderer"], [class*="ChatControlMessage"]') ||
                                body.closest('[class*="ChatControlMessage"]')) {
                                const t = T(body).replace(/\s*\n\s*/g, ' ').trim();
                                if (t) uitkomst.push({ systeem: t.slice(0, 300) });
                                continue;
                            }
                            // Auteur en tijd staan búíten de body: de body verwijst er via
                            // aria-labelledby naar (elementen author-<mid> en timestamp-<mid>).
                            const mid = (body.id || '').replace('message-body-', '') || body.getAttribute('data-mid') || '';
                            const byId = voor => mid ? document.getElementById(voor + '-' + mid) : null;
                            const auteurEl = byId('author') || it.querySelector('[data-tid="message-author-name"],' +
                                '[class*="fui-ChatMessage__author"]');
                            const tijdEl = byId('timestamp') || it.querySelector('time, [data-tid*="timestamp"],' +
                                '[id*="timestamp"], [class*="__timestamp"]');
                            const auteur = ((auteurEl?.textContent) || '').replace(/\s+/g, ' ').trim().slice(0, 60);
                            const tijd = ((tijdEl?.textContent) || '').trim().slice(0, 40);
                            const iso = tijdEl?.getAttribute('datetime') || '';
                            const tijdVol = (tijdEl?.getAttribute('title') || tijdEl?.getAttribute('aria-label') || '')
                                .replace(/\.$/, '').trim();
                            // Richting: eigen berichten hebben de ChatMyMessage-component (in body
                            // of wrapper); anders de positie rechts van het midden als terugval.
                            let uit = !!(body.closest('[class*="ChatMyMessage"]') || it.querySelector('[class*="ChatMyMessage"]') ||
                                (typeof body.className === 'string' && body.className.includes('ChatMyMessage')));
                            if (!uit && !auteur) {
                                const rect = body.getBoundingClientRect();
                                if (rect.width > 0 && rect.width < paneRect.width * 0.85) {
                                    uit = rect.left + rect.width / 2 > paneRect.left + paneRect.width / 2;
                                }
                            }
                            const content = byId('content') || body.querySelector('[id^="content-"], ' +
                                '[data-tid="message-body-content"]') || body;
                            // Citaat (antwoord) of doorgestuurd bericht bovenin de bubbel.
                            let citaat = null;
                            const qr = it.querySelector('[data-tid="quoted-reply-card"]');
                            const fw = it.querySelector('[data-tid="forward-message-card"]');
                            if (qr) {
                                const spans = [...qr.querySelectorAll('span')].filter(s => !s.querySelector('span'));
                                const wanneer = qr.querySelector('[data-tid="quoted-reply-timestamp"]');
                                const wat = qr.querySelector('[data-tid="quoted-reply-preview-content"]');
                                citaat = {
                                    wie: (spans.find(s => s !== wanneer && s !== wat && !wat?.contains(s))?.textContent || '').trim(),
                                    wanneer: (wanneer?.textContent || '').trim(),
                                    wat: T(wat).slice(0, 300), doorgestuurd: false,
                                };
                            } else if (fw) {
                                citaat = {
                                    wie: (fw.querySelector('[data-testid="message-card-header-author-name"]')?.textContent || '').trim(),
                                    wanneer: (fw.querySelector('[data-testid="formatted-date-time"]')?.textContent || '').trim(),
                                    wat: T(fw.querySelector('[id^="content-"]') || fw).slice(0, 600), doorgestuurd: true,
                                };
                            }
                            // Bijlagen (bestanden): naam per kaart; tijdens het laden alleen een spinner.
                            const bijlagen = [];
                            for (const grid of it.querySelectorAll('[data-tid="file-attachment-grid"]')) {
                                const namen = [...new Set([...grid.querySelectorAll('[title], a, [data-tid*="file-name"], [data-tid*="attachment-name"]')]
                                    .map(e => (e.getAttribute('title') || e.textContent || '').trim())
                                    .filter(t => t && t.length < 120 && !/^(Meer|More|Download)/i.test(t)))];
                                if (namen.length) bijlagen.push(...namen.slice(0, 4));
                                else bijlagen.push(grid.querySelector('[role="progressbar"]') ? 'Bijlage (nog aan het laden)' : 'Bijlage');
                            }
                            // Reacties: pilletjes met emoji + tekst "1 Leuk vinden-reactie." (aantal).
                            const reacties = [];
                            for (const knop of it.querySelectorAll('[data-tid="diverse-reaction-pill-button"]')) {
                                const emoji = knop.querySelector('img')?.alt || '';
                                const lab = knop.getAttribute('aria-labelledby');
                                const labTekst = (lab ? (document.getElementById(lab)?.textContent || '') : '') +
                                    ' ' + (knop.getAttribute('aria-label') || '') + ' ' + (knop.textContent || '');
                                const n = parseInt((labTekst.match(/\d+/) || ['1'])[0], 10) || 1;
                                if (emoji) reacties.push({ emoji, n, eigen: knop.getAttribute('aria-pressed') === 'true' });
                            }
                            const statusEl = it.querySelector('[id^="read-status-icon-"]');
                            const status = statusEl ? (statusEl.getAttribute('aria-label') || '').trim() : '';
                            const details = it.querySelector('[class*="__details"]');
                            const bewerkt = /bewerkt|edited|modifi/i.test((details?.textContent || '') +
                                ' ' + (it.querySelector('[data-tid*="edited" i]')?.textContent || ''));
                            const vermeld = !!it.querySelector('[data-tid="mention-badge"]');
                            const avImg = it.querySelector('[data-tid="message-avatar"] img, [class*="__avatar"] img');
                            const avatar = (!uit && avImg) ? await avatarData(avImg.currentSrc || avImg.src) : '';

                            let tekst = T(content).slice(0, 4000);
                            let html = schoon(content).slice(0, 12000);
                            // Meegestuurde afbeelding in de bubbel. Alleen binnen de body zoeken
                            // (avatars staan erbuiten); emoji's en pictogrammen vallen af op
                            // formaat. Teams gebruikt blob:-URL's én https-media (asyncgw, met
                            // sessiecookies bereikbaar).
                            let beeld = '';
                            const geenAvatar = i =>
                                !i.closest('[class*="Avatar"], [data-tid*="avatar"]') && !isEmoji(i);
                            const zoekKandidaten = () =>
                                [...body.querySelectorAll('img')].filter(i => {
                                    const src = i.src || i.currentSrc || '';
                                    if (!/^(blob:|data:image|https:)/.test(src)) return false;
                                    if (!geenAvatar(i)) return false;
                                    const b = i.getBoundingClientRect();
                                    const breed = i.naturalWidth || i.clientWidth || b.width;
                                    const hoog = i.naturalHeight || i.clientHeight || b.height;
                                    return breed >= 50 && hoog >= 50;
                                });
                            let kandidaten = zoekKandidaten();
                            // Herkenbare beeldcontainer: in een verborgen sessie mount het
                            // img-element soms pas (veel) later dan de container eromheen.
                            const container = !!body.querySelector(
                                '[data-testid*="image"], [data-tid*="image"], [itemtype*="Image"]');
                            // Het bericht bevat een foto, ook als het ophalen zo meteen mislukt:
                            // dan toont de cockpit een placeholder in plaats van het bericht
                            // geruisloos te laten wegvallen.
                            const foto = kandidaten.length > 0 || container ||
                                (tekst.length === 0 &&
                                [...body.querySelectorAll('img')].some(i =>
                                    geenAvatar(i) &&
                                    /^(blob:|data:image|https:)/.test(i.src || i.currentSrc || '')));
                            for (let poging = 0; poging < 3 && fotoBudget > 0 && !beeld &&
                                    (kandidaten.length > 0 || container); poging++) {
                                if (kandidaten.length === 0) {
                                    it.scrollIntoView({ block: 'center' });
                                    await wacht(900);
                                    kandidaten = zoekKandidaten();
                                    if (kandidaten.length === 0) continue;
                                }
                                const img = kandidaten.sort((a, b) =>
                                    (b.naturalWidth || b.clientWidth) - (a.naturalWidth || a.clientWidth))[0];
                                try {
                                    // In beeld brengen: Teams laadt afbeeldingen lui.
                                    it.scrollIntoView({ block: 'center' });
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
                                        bmp = await createImageBitmap(await (await fetch(bron, { credentials: 'include' })).blob());
                                    } catch {
                                        bmp = await new Promise((res, rej) => {
                                            const el = new Image();
                                            el.crossOrigin = 'use-credentials';
                                            el.onload = () => res(el);
                                            el.onerror = rej;
                                            el.src = bron;
                                        });
                                    }
                                    const w = bmp.naturalWidth || bmp.width, h = bmp.naturalHeight || bmp.height;
                                    if (!w) throw new Error('nog niet geladen');
                                    const schaal = Math.min(1, 900 / w);
                                    const canvas = document.createElement('canvas');
                                    canvas.width = Math.max(1, Math.round(w * schaal));
                                    canvas.height = Math.max(1, Math.round((h || 1) * schaal));
                                    canvas.getContext('2d').drawImage(bmp, 0, 0, canvas.width, canvas.height);
                                    const jpeg = canvas.toDataURL('image/jpeg', 0.72);
                                    if (jpeg.length > 200 && fotoTekens + jpeg.length <= 1100000) {
                                        beeld = jpeg;
                                        fotoTekens += jpeg.length;
                                        fotoBudget--;
                                    }
                                } catch { /* volgende poging, of de placeholder */ }
                                if (!beeld && poging < 2) {
                                    await wacht(1100);
                                    kandidaten = zoekKandidaten();
                                }
                            }
                            uitkomst.push({ tijd, iso, tijdVol, auteur, uit, tekst, html, beeld, foto, avatar,
                                citaat, bijlagen, reacties, status, bewerkt, vermeld });
                        }
                        window.__wmTeamsMsgs = uitkomst.filter(o => o.divider || o.systeem ||
                            o.tekst.length > 0 || o.beeld.length > 0 || o.foto || o.bijlagen.length > 0 || o.citaat);
                      } catch (e) {
                        window.__wmTeamsMsgs = { leeg: true, diag: { fout: String(e).slice(0, 200) } };
                      }
                    })();
                    return true;
                })()
                """);
            var json = "null";
            for (var i = 0; i < 100; i++) // foto's omzetten + herkansingen kan duren (max. ~30 s)
            {
                await Task.Delay(300, ct);
                var klaar = await JsAsync("JSON.stringify(window.__wmTeamsMsgs)");
                if (klaar is not ("null" or "\"null\""))
                {
                    json = JsonSerializer.Deserialize<string>(klaar) ?? "null";
                    break;
                }
            }
            if (json == "null")
            {
                await ParkeerOpConceptenAsync();
                throw new InvalidOperationException(
                    "Berichten uitlezen bleef hangen (geen resultaat).");
            }
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    await ParkeerOpConceptenAsync();
                    throw new InvalidOperationException(
                        $"0 berichten; DOM-stand: {doc.RootElement.GetProperty("diag").GetRawText()}");
                }
            }
            var ruw = JsonSerializer.Deserialize<List<TeamsRuw>>(json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
            var regels = new List<TeamsChatBericht>();
            foreach (var r in ruw)
            {
                if (r.Divider is { Length: > 0 } dag)
                {
                    regels.Add(new TeamsChatBericht("", "", false, dag) { Soort = "divider" });
                    continue;
                }
                if (r.Systeem is { Length: > 0 } sys)
                {
                    regels.Add(new TeamsChatBericht("", "", false, sys) { Soort = "systeem" });
                    continue;
                }
                var reacties = r.Reacties.Where(x => x.Emoji.Length > 0).ToList();
                regels.Add(new TeamsChatBericht(r.Tijd, r.Auteur, r.Uit, r.Tekst, r.Beeld, r.Foto)
                {
                    Iso = r.Iso,
                    TijdVol = r.TijdVol,
                    Html = r.Html,
                    AvatarUrl = r.Avatar,
                    Citaat = r.Citaat is { } c && (c.Wat.Length > 0 || c.Wie.Length > 0)
                        ? new TeamsCitaat(c.Wie, c.Wanneer, c.Wat, c.Doorgestuurd) : null,
                    Bijlagen = r.Bijlagen,
                    Reacties = string.Join(" ", reacties.Select(x => x.N > 1 ? $"{x.Emoji} {x.N}" : x.Emoji)),
                    EigenReactie = reacties.Any(x => x.Eigen),
                    Status = r.Status,
                    Bewerkt = r.Bewerkt,
                    Vermeld = r.Vermeld,
                });
            }
            // Teams zet de auteursnaam alleen boven het eerste bericht van een reeks; schuif
            // hem door naar de vervolgberichten zodat in groepschats elke afzender zichtbaar is.
            var vorigeAuteur = "";
            var vorigeAvatar = "";
            for (var i = 0; i < regels.Count; i++)
            {
                if (regels[i].Soort.Length > 0)
                {
                    continue;
                }
                if (regels[i].Uitgaand)
                {
                    vorigeAuteur = "";
                    vorigeAvatar = "";
                }
                else if (regels[i].Auteur.Length > 0)
                {
                    vorigeAuteur = regels[i].Auteur;
                    vorigeAvatar = regels[i].AvatarUrl.Length > 0 ? regels[i].AvatarUrl : vorigeAvatar;
                }
                else if (vorigeAuteur.Length > 0)
                {
                    regels[i] = regels[i] with { Auteur = vorigeAuteur, AvatarUrl = vorigeAvatar };
                }
            }
            await ParkeerOpConceptenAsync(); // de geopende chat weer sluiten
            return regels;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Wat de pagina per item teruggeeft (zie het script in LaatsteBerichtenAsync).</summary>
    private sealed class TeamsRuw
    {
        public string? Divider { get; set; }
        public string? Systeem { get; set; }
        public string Tijd { get; set; } = "";
        public string Iso { get; set; } = "";
        public string TijdVol { get; set; } = "";
        public string Auteur { get; set; } = "";
        public bool Uit { get; set; }
        public string Tekst { get; set; } = "";
        public string Html { get; set; } = "";
        public string Beeld { get; set; } = "";
        public bool Foto { get; set; }
        public string Avatar { get; set; } = "";
        public TeamsRuwCitaat? Citaat { get; set; }
        public List<string> Bijlagen { get; set; } = [];
        public List<TeamsRuwReactie> Reacties { get; set; } = [];
        public string Status { get; set; } = "";
        public bool Bewerkt { get; set; }
        public bool Vermeld { get; set; }
    }

    private sealed class TeamsRuwCitaat
    {
        public string Wie { get; set; } = "";
        public string Wanneer { get; set; } = "";
        public string Wat { get; set; } = "";
        public bool Doorgestuurd { get; set; }
    }

    private sealed class TeamsRuwReactie
    {
        public string Emoji { get; set; } = "";
        public int N { get; set; } = 1;
        public bool Eigen { get; set; }
    }

    /// <summary>
    /// Tekst van een bericht voor transcripten (Claude, cockpitlijst): citaat, bijlagen,
    /// foto en reacties als leesbare markeringen rond de tekst.
    /// </summary>
    public static string TranscriptTekst(TeamsChatBericht b)
    {
        var delen = new List<string>();
        if (b.Citaat is { } c)
        {
            delen.Add((c.Doorgestuurd ? "[doorgestuurd van " : "[antwoord op ") +
                (c.Wie.Length > 0 ? c.Wie : "bericht") + $": “{Kort(c.Wat, 120)}”]");
        }
        if (b.Beeld.Length > 0 || b.Foto)
        {
            delen.Add("[📷 afbeelding]");
        }
        foreach (var bijlage in b.Bijlagen)
        {
            delen.Add($"[📎 {bijlage}]");
        }
        if (b.Tekst.Length > 0)
        {
            delen.Add(b.Tekst);
        }
        if (b.Reacties.Length > 0)
        {
            delen.Add($"[reactie: {b.Reacties}]");
        }
        if (b.Bewerkt)
        {
            delen.Add("(bewerkt)");
        }
        return string.Join(" ", delen);
    }

    private static string Kort(string tekst, int max)
    {
        tekst = tekst.ReplaceLineEndings(" ").Trim();
        return tekst.Length <= max ? tekst : tekst[..max] + "…";
    }

    /// <summary>
    /// Chats waarin ik vandaag het laatste woord had, als signaalregels voor het dagvoorstel
    /// ("HH:mm chatnaam — U: preview"). Leest alleen de zichtbare chatlijst: een rij met een
    /// tijd (i.p.v. een datum) is van vandaag, en een preview die met "U:"/"You:" begint is
    /// een bericht van mijzelf. Alleen het láátste bericht per chat is zichtbaar, dus dit is
    /// een ondergrens — maar als werktijdsignaal volstaat "in die chat heb ik gereageerd".
    /// </summary>
    public async Task<List<string>> MijnChatsVanVandaagAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Teams is niet ingelogd — koppel opnieuw via 'Teams koppelen…'.");
            }
            if (!await OpChatlijstAsync())
            {
                await TerugNaarChatlijstAsync();
            }
            await WachtOpRijenAsync(20, 500, ct);
            // Zelfde rij-ontleding als de ongelezen-scrape: naam vóór het tijdstip, preview
            // erna. Alleen de gerenderde rijen (geen scrollen): chats van vandaag staan bovenaan.
            var json = await JsAsync(MetSelector(
                """
                (function () {
                    let items = [...document.querySelectorAll(__SELECTOR__)];
                    items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                    const uit = [];
                    for (const it of items) {
                        let ruw = (it.textContent || '').replace(/\s+/g, ' ').trim();
                        ruw = ruw.replace(/^(chats?|ongelezen|unread|non lus?)[,.:]?\s*/i, '');
                        const tijd = ruw.match(/\b\d{1,2}:\d{2}\b/);
                        const datum = ruw.match(/\b\d{1,2}-\d{1,2}\b/);
                        // Een datum vóór (of zonder) een tijd = een rij van een eerdere dag.
                        if (!tijd || (datum && datum.index < tijd.index)) continue;
                        let naam = ruw.slice(0, tijd.index).trim();
                        const titelAttr = (it.querySelector('[data-tid="chat-list-item-title"]') ||
                            it.querySelector('span[title]'))?.getAttribute('title');
                        if (titelAttr) naam = titelAttr;
                        naam = naam.replace(/[,.]$/, '').slice(0, 60).trim();
                        let preview = ruw.slice(tijd.index + tijd[0].length).trim();
                        preview = preview.split(/\s*(?:Ongelezen|Unread|Non lus?)(?=[A-ZÀ-Ž])/)[0].trim();
                        if (!naam || !/^(u|you|vous)\s*:/i.test(preview)) continue;
                        uit.push({ naam, tijd: tijd[0], preview: preview.slice(0, 160) });
                    }
                    return JSON.stringify(uit);
                })()
                """));
            var regels = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(
                    JsonSerializer.Deserialize<string>(json) is { } s ? s : json);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var naam = e.GetProperty("naam").GetString() ?? "";
                    var tijd = e.GetProperty("tijd").GetString() ?? "";
                    var preview = e.GetProperty("preview").GetString() ?? "";
                    regels.Add($"{tijd} {naam} — {preview}");
                }
            }
            catch
            {
                // Onverwachte DOM: dan gewoon geen Teams-signaal.
            }
            return regels.OrderBy(r => r, StringComparer.Ordinal).ToList();
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Zet één chat bewust als gelezen in Teams (voor "Archiveren" in de cockpit): opent de
    /// chat kort met tijdelijke toestemming voor de gelezen-markering en parkeert daarna weer.
    /// </summary>
    public async Task MarkeerGelezenAsync(string naam, CancellationToken ct)
    {
        void Log(string melding)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "teams-gelezen-debug.txt"),
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
                throw new InvalidOperationException("Teams is niet ingelogd.");
            }
            await TerugNaarChatlijstAsync();
            // De chat met een échte klik openen (zie OpenChatAsync: synthetische events —
            // en dus ook het oude "…"-menu-pad met "Als gelezen markeren" — negeert Teams
            // tegenwoordig volledig) en nadrukkelijk focus melden + naar onderen scrollen:
            // dan vuurt de leesbevestiging (consumptionhorizon) echt.
            _gelezenToegestaan = true;
            await OpenChatAsync(naam, ct);
            await JsAsync(
                """
                (function () {
                    window.dispatchEvent(new Event('focus'));
                    document.dispatchEvent(new Event('visibilitychange'));
                    const paneel = document.querySelector(
                        '[data-tid="message-pane-list-viewport"],' +
                        '[data-tid="app-layout-area--main"]');
                    if (paneel) paneel.scrollTop = paneel.scrollHeight;
                    return true;
                })()
                """);
            // De leesmarkering (consumptionhorizon-call) nog even doorlaten na de klik.
            await Task.Delay(2500, ct);
            var stand = await JsAsync(
                $$"""
                (function () {
                    const naam = {{JsonSerializer.Serialize(naam)}};
                    let items = [...document.querySelectorAll(
                        '[data-tid^="chat-list-item"], [data-tid="chat-list"] [role="listitem"],' +
                        '[data-testid="list-item"], [role="list"] [role="listitem"]')];
                    items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                    const titel = it =>
                        (it.querySelector('span[title]')?.getAttribute('title') || '').trim();
                    const rij = items.find(it => titel(it) === naam) ||
                        items.find(it => titel(it).includes(naam)) ||
                        items.find(it => (it.textContent || '').includes(naam));
                    const badge = rij && rij.querySelector('[data-tid*="unread"], [class*="unread"]');
                    return JSON.stringify({ stap: 'chat-geopend', nogOngelezen:
                        !!(badge && badge.offsetParent !== null &&
                        ((badge.textContent || '').trim().length > 0 ||
                         /ongelezen|unread/i.test(badge.getAttribute('aria-label') || ''))) });
                })()
                """);
            Log($"resultaat: {stand}");
        }
        finally
        {
            _gelezenToegestaan = false;
            try
            {
                await ParkeerOpConceptenAsync();
            }
            catch
            {
                // Best effort.
            }
            _slot.Release();
        }
    }

    /// <summary>
    /// Live DOM-zelftest: staan de structurele ankers van de Teams-app er nog? Alleen de
    /// vaste bakens (app-balk/linkerrail) — de chatlijst zelf wisselt met de parkeerstand.
    /// Leeg = in orde; anders een omschrijving van wat er ontbreekt.
    /// </summary>
    public async Task<string> ZelftestAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 10))
            {
                return ""; // niet ingelogd: geen DOM-oordeel mogelijk
            }
            var ok = await JsAsync(
                """
                !!(document.querySelector('[data-tid="app-bar"]') ||
                   document.querySelector('[data-tid="left-rail"]') ||
                   document.querySelector('[data-tid*="chat-list"]'))
                """) == "true";
            return ok ? "" : "Teams: app-balk/chatlijst-ankers niet gevonden";
        }
        finally
        {
            _slot.Release();
        }
    }

    private async Task<string> JsAsync(string script)
    {
        if (_web?.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("Teams-sessie is niet gestart.");
        }
        try
        {
            return await core.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex.Message.Contains("no longer valid",
            StringComparison.OrdinalIgnoreCase))
        {
            // Browserproces onderweg gecrasht (ProcessFailed vuurt niet altijd eerst):
            // markeren zodat de volgende beurt de sessie vers opbouwt.
            _gecrasht = true;
            throw new InvalidOperationException(
                "De Teams-browser is gecrasht en wordt bij de volgende synchronisatie " +
                "automatisch opnieuw gestart.", ex);
        }
    }

    public void Dispose()
    {
        _web?.Dispose();
        _venster?.Dispose();
    }
}
