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
                        Size = new Size(1200, 1600),
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
        // Op het scherm waar de gebruiker nu werkt (muispositie), en even topmost zodat
        // het venster niet achter de gemaximaliseerde cockpit verdwijnt.
        var scherm = Screen.FromPoint(Cursor.Position).WorkingArea;
        _venster.WindowState = FormWindowState.Normal;
        _venster.Visible = true;
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
            for (var i = 0; i < 900; i++) // max. 7,5 min voor login + MFA
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct);
                // E-mail en wachtwoord vullen we in; alleen de MFA-stap blijft handwerk.
                MicrosoftLogin.Verwerk(await JsAsync(MicrosoftLogin.VulScript()));
                if (await IsIngelogdAsync())
                {
                    File.WriteAllText(MarkerFile, DateTimeOffset.Now.ToString("O"));
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
    }

    private static string KlikJs(string zoekExpressie) =>
        $$"""
        (function () {
            const doel = {{zoekExpressie}};
            if (!doel) return false;
            const b = doel.getBoundingClientRect();
            const opts = { bubbles: true, cancelable: true, view: window,
                clientX: b.x + b.width / 2, clientY: b.y + b.height / 2, buttons: 1 };
            for (const type of ['pointerover', 'mouseover', 'pointerdown', 'mousedown',
                                'pointerup', 'mouseup', 'click']) {
                doel.dispatchEvent(type.startsWith('pointer')
                    ? new PointerEvent(type, opts) : new MouseEvent(type, opts));
            }
            return true;
        })()
        """;

    /// <summary>
    /// Parkeert de sessie op de (lege) "Concepten"-weergave. Teams heropent bij een reload
    /// de laatst actieve weergave: zo wordt er nooit een echte chat auto-geopend en blijven
    /// de ongelezen-markeringen in de lijst staan.
    /// </summary>
    private async Task ParkeerOpConceptenAsync()
    {
        try
        {
            await JsAsync(KlikJs(
                """
                [...document.querySelectorAll('[role="treeitem"], [role="listitem"], [data-tid]')]
                    .find(el => (el.textContent || '').trim() === 'Concepten' ||
                                (el.textContent || '').trim() === 'Drafts')
                """));
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
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(300, ct);
            var gerenderd = await JsAsync($"document.querySelectorAll({RijSelector}).length");
            if (int.TryParse(gerenderd, out var rijen) && rijen >= 5)
            {
                break;
            }
        }
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
    private (int Totaal, List<TeamsBericht> Ongelezen)? _laatsteUitslag;
    private int _overgeslagen;

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
                    return items.length + '|' + items.slice(0, 25).map(it => {
                        const label = it.getAttribute('aria-label') ||
                            it.querySelector('[aria-label]')?.getAttribute('aria-label') || '';
                        const badgeEl = it.querySelector('[data-tid*="unread"], [class*="unread"]');
                        const ongelezen = /ongelezen|unread|non lu/i.test(label) ||
                            (!!badgeEl && badgeEl.offsetParent !== null);
                        const naam = (it.querySelector('[data-tid="chat-list-item-title"]') ||
                            it.querySelector('span[title]') || it).textContent || '';
                        return naam.replace(/\s+/g, ' ').trim().slice(0, 40) + (ongelezen ? '!' : '');
                    }).join(';');
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
            await JsAsync(KlikJs(
                """
                document.querySelector('[data-tid="back-button"], button[aria-label*="Terug"],' +
                    'button[aria-label*="Back"]') ||
                [...document.querySelectorAll('[data-tid="app-bar"] [aria-label], [role="tab"]')]
                    .find(el => /^(chat|chatten)\b/i.test((el.getAttribute('aria-label') ||
                        el.textContent || '').trim()))
                """));
            // Wachten tot de rijen er zijn in plaats van blind 1,5 s (max. 1,5 s).
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(150);
                var rijen = await JsAsync($"document.querySelectorAll({RijSelector}).length");
                if (int.TryParse(rijen, out var n) && n >= 5)
                {
                    break;
                }
            }
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
    public async Task<(int Totaal, List<TeamsBericht> Ongelezen)> OngelezenAsync(CancellationToken ct)
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
            var uitslag = (
                Totaal: doc.RootElement.GetProperty("totaal").GetInt32(),
                Ongelezen: doc.RootElement.GetProperty("ongelezen").EnumerateArray()
                    .Select(e => new TeamsBericht(
                        e.GetProperty("naam").GetString() ?? "",
                        e.GetProperty("preview").GetString() ?? ""))
                    .Where(b => b.Naam.Length > 0)
                    .ToList());
            // Alleen een geloofwaardige uitslag als vertrekpunt onthouden: bij een half
            // gerenderde lijst zou de volgende beurt anders een verkeerde stand hergebruiken.
            fasen.Add($"gevonden={uitslag.Ongelezen.Count}/{uitslag.Totaal}");
            if (uitslag.Totaal >= 10)
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

    public sealed record TeamsChatBericht(string Tijd, string Auteur, bool Uitgaand, string Tekst);

    /// <summary>
    /// De laatste berichten uit een Teams-chat, gestructureerd (tijd, auteur, richting,
    /// tekst) voor de bubbelweergave: opent de chat in de verborgen sessie (Teams
    /// markeert hem daardoor als gelezen) en leest de zichtbare berichten uit.
    /// </summary>
    public async Task<List<TeamsChatBericht>> LaatsteBerichtenAsync(
        string naam, int max, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException("Teams is niet ingelogd.");
            }
            await TerugNaarChatlijstAsync(); // vanuit de Concepten-parkeerstand
            var geklikt = await JsAsync(
                $$"""
                (function () {
                    const naam = {{JsonSerializer.Serialize(naam)}};
                    let items = [...document.querySelectorAll(
                        '[data-testid="list-item"], [data-tid^="chat-list-item"],' +
                        '[data-tid="chat-list"] [role="listitem"], [role="list"] [role="listitem"]')];
                    items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                    const doel = items.find(it =>
                        ((it.querySelector('span[title]')?.getAttribute('title')) ||
                         it.textContent || '').includes(naam));
                    if (!doel) return 'niet gevonden';
                    doel.scrollIntoView({ block: 'center' });
                    const b = doel.getBoundingClientRect();
                    const opts = { bubbles: true, cancelable: true, view: window,
                        clientX: b.x + b.width / 2, clientY: b.y + b.height / 2, buttons: 1 };
                    for (const type of ['pointerover', 'mouseover', 'pointerdown', 'mousedown',
                                        'pointerup', 'mouseup', 'click']) {
                        doel.dispatchEvent(type.startsWith('pointer')
                            ? new PointerEvent(type, opts) : new MouseEvent(type, opts));
                    }
                    return 'ok';
                })()
                """);
            if (geklikt != "\"ok\"")
            {
                throw new InvalidOperationException($"Chat \"{naam}\" niet gevonden in de Teams-lijst.");
            }
            await Task.Delay(2500, ct); // berichten laten laden

            var json = await JsAsync(
                $$"""
                (function () {
                    let msgs = [...document.querySelectorAll(
                        '[data-tid="chat-pane-message"], [id^="message-body-"],' +
                        '[data-tid="message-wrapper"]')];
                    if (msgs.length === 0) {
                        // Fallbacks voor nieuwere Teams-DOM's (Fluent-componenten).
                        msgs = [...document.querySelectorAll(
                            '[class*="fui-ChatMessage"], [class*="fui-ChatMyMessage"], [data-mid]')];
                    }
                    if (msgs.length === 0) {
                        return { leeg: true, diag: {
                            paneMsg: document.querySelectorAll('[data-tid="chat-pane-message"]').length,
                            msgBody: document.querySelectorAll('[id^="message-body-"]').length,
                            fui: document.querySelectorAll('[class*="ChatMessage"]').length,
                            mid: document.querySelectorAll('[data-mid]').length,
                            mainTekst: (document.querySelector('[data-tid="app-layout-area--main"]')
                                ?.textContent || '').slice(0, 120),
                        } };
                    }
                    const paneel = document.querySelector('[data-tid="app-layout-area--main"]') ||
                        document.body;
                    const paneRect = paneel.getBoundingClientRect();
                    return msgs.slice(-{{max}}).map(m => {
                        const auteur = m.querySelector('[data-tid="message-author-name"]')
                            ?.textContent?.trim() || '';
                        const tijd = (m.querySelector('time, [data-tid*="timestamp"],' +
                            '[id*="timestamp"]')?.textContent || '').trim().slice(0, 20);
                        const body = m.querySelector('[id^="message-body-"],' +
                            '[data-tid="message-body-content"], [class*="fui-ChatMessage__body"]');
                        let tekst = ((body || m).innerText || '')
                            .replace(/\s+/g, ' ').trim().slice(0, 500);
                        if (auteur && tekst.startsWith(auteur)) tekst = tekst.slice(auteur.length).trim();
                        if (tijd && tekst.startsWith(tijd)) tekst = tekst.slice(tijd.length).trim();
                        // Richting: eigen berichten hebben de ChatMyMessage-component of staan
                        // rechts van het midden van het berichtenpaneel.
                        let uit = !!(m.closest('[class*="ChatMyMessage"]') ||
                            m.querySelector('[class*="ChatMyMessage"]') ||
                            (typeof m.className === 'string' && m.className.includes('ChatMyMessage')));
                        if (!uit && !auteur) {
                            const rect = (body || m).getBoundingClientRect();
                            if (rect.width > 0 && rect.width < paneRect.width * 0.85) {
                                uit = rect.left + rect.width / 2 > paneRect.left + paneRect.width / 2;
                            }
                        }
                        return { tijd, auteur, uit, tekst };
                    }).filter(o => o.tekst.length > 0);
                })()
                """);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                await ParkeerOpConceptenAsync();
                throw new InvalidOperationException(
                    $"0 berichten; DOM-stand: {doc.RootElement.GetProperty("diag").GetRawText()}");
            }
            var regels = doc.RootElement.EnumerateArray()
                .Select(e => new TeamsChatBericht(
                    e.GetProperty("tijd").GetString() ?? "",
                    e.GetProperty("auteur").GetString() ?? "",
                    e.GetProperty("uit").GetBoolean(),
                    e.GetProperty("tekst").GetString() ?? ""))
                .Where(b => b.Tekst.Length > 0)
                .ToList();
            await ParkeerOpConceptenAsync(); // de geopende chat weer sluiten
            return regels;
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
            // De hele actie draait als asynchrone job in de pagina: de rij zoeken (zo nodig
            // door de gevirtualiseerde lijst scrollen), dan via het "…"-menu van de rij
            // "Als gelezen markeren" kiezen. Dat stuurt de leesmarkering direct, óók in een
            // verborgen venster zonder focus (alleen de chat openen deed dat niet — de
            // markering bleef dan uit en de badge bleef staan). Terugvaloptie: chat openen
            // en nadrukkelijk focus melden + naar onderen scrollen.
            _gelezenToegestaan = true;
            await JsAsync(
                $$"""
                (function () {
                    window.__wmGelezen = null;
                    (async () => {
                        const naam = {{JsonSerializer.Serialize(naam)}};
                        const res = { stap: 'start' };
                        const wacht = ms => new Promise(r => setTimeout(r, ms));
                        const vindRij = () => {
                            let items = [...document.querySelectorAll(
                                '[data-tid^="chat-list-item"], [data-tid="chat-list"] [role="listitem"],' +
                                '[data-testid="list-item"], [role="list"] [role="listitem"]')];
                            items = items.filter(it => !items.some(o => o !== it && it.contains(o)));
                            return items.find(it =>
                                ((it.querySelector('span[title]')?.getAttribute('title')) ||
                                 it.textContent || '').includes(naam)) || null;
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
                        if (!rij) {
                            // Gevirtualiseerde lijst: alleen zichtbare rijen bestaan in de DOM,
                            // dus scrollend zoeken tot de gevraagde chat gerenderd is.
                            let scroller = document.querySelector(
                                '[data-testid="list-item"], [data-tid^="chat-list-item"]');
                            while (scroller && scroller !== document.body) {
                                const s = getComputedStyle(scroller);
                                if (/(auto|scroll)/.test(s.overflowY) &&
                                    scroller.scrollHeight > scroller.clientHeight + 10) break;
                                scroller = scroller.parentElement;
                            }
                            if (scroller && scroller !== document.body) {
                                for (let i = 0; i < 25 && !rij; i++) {
                                    scroller.scrollTop += scroller.clientHeight * 0.8;
                                    await wacht(250);
                                    rij = vindRij();
                                    if (scroller.scrollTop + scroller.clientHeight >=
                                        scroller.scrollHeight - 5) break;
                                }
                            }
                        }
                        if (!rij) { res.stap = 'rij-niet-gevonden'; window.__wmGelezen = res; return; }
                        rij.scrollIntoView({ block: 'center' });
                        // Route 1: hover toont de "…"-knop van de rij → menu → "Als gelezen markeren".
                        hover(rij);
                        await wacht(500);
                        const meer = rij.querySelector(
                            '[data-tid*="more"], button[aria-label*="pties"],' +
                            'button[aria-label*="ptions"], button[aria-haspopup="menu"]') ||
                            rij.querySelector('button');
                        res.meerKnop = !!meer;
                        let menuItem = null;
                        if (meer) {
                            klik(meer);
                            await wacht(800);
                            menuItem = [...document.querySelectorAll(
                                '[role="menuitem"], [role="menuitemcheckbox"]')]
                                .find(el => /als gelezen|mark as read|marquer comme lu/i
                                    .test(el.textContent || ''));
                            res.menuTeksten = [...document.querySelectorAll('[role="menuitem"]')]
                                .map(el => (el.textContent || '').trim().slice(0, 40)).slice(0, 15);
                        }
                        if (menuItem) {
                            klik(menuItem);
                            res.stap = 'menu-geklikt';
                            await wacht(1200);
                        }
                        // Altijd óók de chat openen mét focus-signalen. Bij 1-op-1-chats zet het
                        // menu-item "Markeren als gelezen" de chat niet betrouwbaar op gelezen;
                        // de leesbevestiging (consumptionhorizon) vuurt pas echt als de chat
                        // geopend en gefocust is en het paneel naar onderen gescrold wordt.
                        document.body.dispatchEvent(new KeyboardEvent('keydown',
                            { key: 'Escape', bubbles: true }));
                        await wacht(300);
                        klik(rij);
                        if (res.stap !== 'menu-geklikt') res.stap = 'chat-geopend';
                        await wacht(2500);
                        window.dispatchEvent(new Event('focus'));
                        document.dispatchEvent(new Event('visibilitychange'));
                        const paneel = document.querySelector(
                            '[data-tid="message-pane-list-viewport"],' +
                            '[data-tid="app-layout-area--main"]');
                        if (paneel) paneel.scrollTop = paneel.scrollHeight;
                        await wacht(2500);
                        const rij2 = vindRij();
                        const badge = rij2 && rij2.querySelector('[data-tid*="unread"], [class*="unread"]');
                        res.nogOngelezen = !!(badge && badge.offsetParent !== null &&
                            ((badge.textContent || '').trim().length > 0 ||
                             /ongelezen|unread/i.test(badge.getAttribute('aria-label') || '')));
                        window.__wmGelezen = res;
                    })();
                    return true;
                })()
                """);
            var stand = "null";
            for (var i = 0; i < 60; i++) // de job scrolt en wacht zelf: ruim de tijd geven
            {
                await Task.Delay(300, ct);
                var klaar = await JsAsync("JSON.stringify(window.__wmGelezen)");
                if (klaar is not ("null" or "\"null\""))
                {
                    stand = klaar;
                    break;
                }
            }
            // De leesmarkering (consumptionhorizon-call) nog even doorlaten na de klik.
            await Task.Delay(2000, ct);
            Log($"resultaat: {stand}");
            if (stand.Contains("rij-niet-gevonden"))
            {
                throw new InvalidOperationException($"Chat \"{naam}\" niet gevonden in de Teams-lijst.");
            }
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
