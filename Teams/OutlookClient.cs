using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WorkManager;

/// <summary>
/// Eenvoudige Outlook-web-uitlezer voor de CED-mailbox (maarten.bergmans@ced.be) via
/// outlook.office.com in een (meestal onzichtbaar) WebView2-venster: ongelezen mails in
/// de inbox signaleren voor de cockpit, zonder ze te openen. Alleen uitlezen — antwoorden
/// gebeurt in Outlook zelf. Het CED-tenant vraagt dagelijks MFA: de sessie verloopt dus
/// elke dag en wordt met de knop "Outlook aanmelden…" in de cockpit opnieuw geopend.
/// </summary>
public sealed class OutlookClient : IDisposable
{
    public static OutlookClient Instance { get; } = new();

    private static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkManager");

    private static readonly string MarkerFile = Path.Combine(DataDir, "outlook-linked.txt");

    private Form? _venster;
    private WebView2? _web;
    private WebView2? _webAgenda; // tweede tabblad: agenda/afspraakdetails, zelfde profiel
    private WebView2? _jsDoel; // binnen agenda-operaties: het tabblad waar JsAsync op werkt
    private CoreWebView2Environment? _env;
    private readonly SemaphoreSlim _slot = new(1, 1);
    private DateTimeOffset _laatstHerladen = DateTimeOffset.MinValue;
    private volatile bool _gecrasht; // browserproces weg → bij de volgende beurt vers opbouwen

    /// <summary>Laat de volgende poll de pagina vers herladen ("Volledige synchronisatie").</summary>
    public void ForceerHerlaad() => _laatstHerladen = DateTimeOffset.MinValue;

    /// <summary>
    /// Nachtelijk onderhoud: de sessie bij de eerstvolgende beurt volledig vers opbouwen
    /// (zelfde route als het crash-herstel; cookies blijven staan, dus geen extra MFA
    /// zolang de dagelijkse aanmelding nog geldig is).
    /// </summary>
    public void MarkeerVoorVerseStart() => _gecrasht = true;

    /// <summary>Is er ooit met succes gekoppeld? Zo niet, dan slaat de cockpit Outlook over.</summary>
    public static bool OoitGekoppeld => File.Exists(MarkerFile);

    /// <summary>Is de sessie op dit moment ingelogd? (Bijgewerkt bij elke start/poll.)</summary>
    public static bool Aangemeld { get; private set; }

    private readonly SemaphoreSlim _initSlot = new(1, 1);

    public async Task<bool> StartAsync(CancellationToken ct, int wachtSeconden = 30)
    {
        // Initialisatie serialiseren + tijdslimiet: zie TeamsClient (vergrendeld profiel
        // door achtergebleven webview-processen mag nooit stil blijven hangen).
        await _initSlot.WaitAsync(ct);
        try
        {
            if (_gecrasht)
            {
                // Na een browsercrash (of het nachtelijk onderhoud) is de oude CoreWebView2
                // niet meer te vertrouwen: controls en venster weggooien zodat de opbouw
                // hieronder een verse sessie start (profiel/cookies blijven).
                try { _web?.Dispose(); } catch { /* al kapot */ }
                try { _webAgenda?.Dispose(); } catch { /* al kapot */ }
                try { _venster?.Dispose(); } catch { /* al kapot */ }
                _web = null;
                _webAgenda = null;
                _jsDoel = null;
                _venster = null;
                _gecrasht = false;
            }
            if (_web?.CoreWebView2 is null)
            {
                if (_venster is null)
                {
                    _venster = new Form
                    {
                        Text = "Outlook (CED) aanmelden – maarten.bergmans@ced.be",
                        // Bewust groot: het venster staat buiten beeld en hoe hoger het is,
                        // hoe meer rijen de gevirtualiseerde maillijst rendert (minder
                        // scroll- en zoekwerk om een mail te vinden).
                        Size = new Size(1400, 1600),
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
                // tijdje en komen nieuwe mails niet meer binnen.
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(DataDir, "webview2-outlook"),
                    new CoreWebView2EnvironmentOptions(
                        "--disable-background-timer-throttling " +
                        "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding"));
                _env = env; // ook voor het agenda-tabblad (zelfde profiel, zelfde login)
                // Met tijdslimiet én zelfherstel: hangt de init op een vergrendeld profiel
                // (achtergebleven webview-processen), dan worden die opgeruimd en volgt
                // één nieuwe poging met een verse control.
                _web = await WebViewOpruimer.InitMetHerstelAsync(_venster!, _web!, env,
                    Path.Combine(DataDir, "webview2-outlook"), "Outlook", ct);
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
                        File.AppendAllText(Path.Combine(DataDir, "outlook-crash-log.txt"),
                            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} ProcessFailed: " +
                            $"{e.ProcessFailedKind} (herstel: {(_gecrasht ? "herstart bij volgende poll" : "reload")})\r\n");
                    }
                    catch
                    {
                        // Alleen diagnose.
                    }
                };
                // Pop-ups (window.open vanuit Outlook of de Microsoft-login) in ditzelfde
                // verborgen venster afhandelen: standaard maakt WebView2 er een écht,
                // zichtbaar pop-upvenster van.
                _web.CoreWebView2.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    _web.CoreWebView2.Navigate(e.Uri);
                };
                _web.CoreWebView2.Navigate("https://outlook.office.com/mail/");
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

    /// <summary>
    /// Logt elke keer dat het (normaal verborgen) venster on-screen gezet wordt, mét
    /// aanroepstack — om spontane "Outlook popt op"-meldingen te kunnen herleiden.
    /// </summary>
    private static void LogVensterOnScreen(string route)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDir, "outlook-venster-log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} on-screen via {route}\r\n" +
                $"{Environment.StackTrace}\r\n\r\n");
        }
        catch
        {
            // Alleen diagnose.
        }
    }

    /// <summary>Toont het venster voor de (dagelijkse) MFA-aanmelding; verbergt het daarna weer.</summary>
    public async Task KoppelAsync(CancellationToken ct)
    {
        // Kort checken of we al ingelogd zijn; zo niet, meteen het venster tonen.
        var ingelogd = await StartAsync(ct, wachtSeconden: 3);
        if (ingelogd || _venster is null)
        {
            if (ingelogd)
            {
                BronGezondheid.Hervat("Outlook");
            }
            return;
        }
        LogVensterOnScreen("KoppelAsync (MFA-aanmelding)");
        // Op het scherm waar de gebruiker nu werkt (muispositie), en even topmost zodat
        // het venster niet achter de gemaximaliseerde cockpit verdwijnt.
        var scherm = Screen.FromPoint(Cursor.Position).WorkingArea;
        _venster.Location = new Point(
            scherm.X + (scherm.Width - _venster.Width) / 2,
            scherm.Y + (scherm.Height - _venster.Height) / 2);
        _venster.TopMost = true;
        _venster.BringToFront();
        _venster.Activate();

        // Met try/finally: breekt het aanmelden af (venster gesloten, browsercrash, MFA
        // afgebroken), dan bleef dit venster anders "altijd bovenop" staan — en dan kun je
        // niet meer bij vensters die erachter zitten.
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
                    BronGezondheid.Hervat("Outlook");
                    await Task.Delay(3000, ct); // inbox laten laden
                    return;
                }
            }
        }
        finally
        {
            Verberg();
        }
        throw new TimeoutException("De Outlook-aanmelding werd niet (op tijd) afgerond.");
    }

    private void Verberg()
    {
        _venster!.TopMost = false;
        _venster.Location = new Point(-4000, -4000);
        // Altijd terug naar Postvak IN (bv. na de Archief-weergave): de eerstvolgende
        // poll leest anders de verkeerde map uit. Best effort; in het inlogscherm
        // bestaat de mappenbalk simpelweg nog niet.
        _ = KlikMapAsync(InboxPatroon, CancellationToken.None);
    }

    private const string InboxPatroon = "postvak in|inbox|bo[iî]te de r[eé]ception";
    private const string ArchiefPatroon = "archief|archive";

    /// <summary>
    /// Klikt een map in de linker mappenbalk aan (op naam, hoofdletterongevoelig). Staat
    /// het navigatiedeelvenster ingeklapt (geen mappen in de pagina), dan wordt het eerst
    /// geopend via de hamburgerknop.
    /// </summary>
    private async Task<bool> KlikMapAsync(string patroon, CancellationToken ct)
    {
        for (var poging = 0; poging < 3; poging++)
        {
            string res;
            try
            {
                res = await JsAsync(
                    $$"""
                    (function () {
                        {{KlikHelpers}}
                        if (document.querySelectorAll('[role="treeitem"]').length === 0) {
                            // Mappenpaneel dicht: eerst openklappen. We herkennen de
                            // openknop op label (meerdere talen) én op het icoon.
                            const labelPat = /navigatiedeelvenster|mappen|navigation pane|folder pane|folders|volet de navigation|dossiers/i;
                            const iconPat = /GlobalNavButton|CollapseMenu|ExpandMenu|Nav|Hamburger|SidePanel/i;
                            const knoppen = [...document.querySelectorAll('button, [role="button"]')];
                            let nav = knoppen.find(x => labelPat.test(
                                (x.getAttribute('aria-label') || '') + ' ' +
                                (x.getAttribute('title') || '')));
                            if (!nav) {
                                nav = knoppen.find(x => iconPat.test(
                                    x.querySelector('i,[data-icon-name]')?.getAttribute('data-icon-name') || ''));
                            }
                            if (nav) { klik(nav); return 'nav-geopend'; }
                            return 'geen-mappen';
                        }
                        const pat = new RegExp({{JsonSerializer.Serialize(patroon)}}, 'i');
                        // OWA hangt in de maptitel een telsuffix (" - 15.218 items (20 ongelezen)")
                        // achter de mapnaam. Verankerde patronen (^cc$) matchen daar nooit op, dus
                        // strippen we die suffix eerst zodat alleen de kale mapnaam ("CC") overblijft.
                        const schoon = s => (s + '')
                            .replace(/\s*[-–]\s*[\d., ]+\s*items?.*$/i, '')
                            .replace(/\s+/g, ' ').trim();
                        const m = [...document.querySelectorAll('[role="treeitem"]')]
                            .find(x => {
                                const naam = schoon(x.getAttribute('title') ||
                                    x.querySelector('span[title]')?.getAttribute('title') || '');
                                return pat.test(naam);
                            });
                        if (!m) return 'niet-gevonden';
                        klik(m);
                        return 'ok';
                    })()
                    """);
            }
            catch
            {
                return false; // sessie (nog) niet gestart: niets te klikken
            }
            if (res.Contains("ok"))
            {
                await Task.Delay(2000, ct); // lijst van de nieuwe map laten laden
                return true;
            }
            if (res.Contains("nav-geopend"))
            {
                await Task.Delay(1500, ct); // paneel laten uitklappen en opnieuw proberen
                continue;
            }
            return false;
        }
        return false;
    }

    private async Task<bool> IsIngelogdAsync() =>
        // Alleen "ingelogd" bij een echt mailonderdeel. Let op: toetsen op berichtenrijen
        // ([data-convid] / [role=option]) is fout — bij een leeggewerkte Postvak IN zijn die
        // er niet, en dan concludeert StartAsync na dertig seconden "niet aangemeld". Dat
        // pauzeerde de bron een half uur terwijl de sessie gewoon werkte (10 augustus 2026).
        // De mappenboom en de lijstcontainer staan er altijd, ook bij een lege map.
        await JsAsync(
            """
            (location.hostname.endsWith('outlook.office.com') ||
             location.hostname.endsWith('outlook.office365.com') ||
             location.hostname.endsWith('outlook.cloud.microsoft')) &&
            !!(document.querySelector('#LeftRail') ||
               document.querySelectorAll('[role="treeitem"]').length > 5 ||
               document.getElementById('MailList') ||
               document.querySelector('[data-convid]') ||
               document.querySelector('[role="option"][aria-label]'))
            """) == "true";

    public sealed record OutlookBericht(
        string Van, string Onderwerp, string Preview, bool Ongelezen, DateTimeOffset? Datum);

    /// <summary>
    /// Exact ontvangstmoment uit de kop van de geopende mail ("ma 27-7-2026 13:16" of
    /// "maandag 27 juli 2026 om 13:16"). Null als het er niet in staat.
    /// </summary>
    internal static DateTimeOffset? ParseVolledigMoment(string kop)
    {
        var offset = DateTimeOffset.Now.Offset;
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                kop, @"\b(\d{1,2})[-/](\d{1,2})[-/](\d{4})\D{0,8}?(\d{1,2}):(\d{2})\b");
            if (m.Success)
            {
                return new DateTimeOffset(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value),
                    int.Parse(m.Groups[1].Value), int.Parse(m.Groups[4].Value),
                    int.Parse(m.Groups[5].Value), 0, offset);
            }
            m = System.Text.RegularExpressions.Regex.Match(
                kop, $@"\b(\d{{1,2}})\s+({string.Join('|', Maanden.Distinct())})\s+(\d{{4}})\D{{0,8}}?(\d{{1,2}}):(\d{{2}})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var maand = (Array.IndexOf(Maanden, m.Groups[2].Value.ToLowerInvariant()) % 12) + 1;
                return new DateTimeOffset(int.Parse(m.Groups[3].Value), maand,
                    int.Parse(m.Groups[1].Value), int.Parse(m.Groups[4].Value),
                    int.Parse(m.Groups[5].Value), 0, offset);
            }
        }
        catch
        {
            // Onparseerbaar: dan de lijst-datum gebruiken.
        }
        return null;
    }

    /// <summary>
    /// Ontvangstmoment uit het rijlabel: "13:16" = vandaag om die tijd, anders een datum
    /// als "26-7", "26-7-2026" of "26 juli" (eventueel met tijd). Null als er niets in staat.
    /// </summary>
    private static DateTimeOffset? ParseMailMoment(string label)
    {
        var nu = DateTimeOffset.Now;
        var tijd = System.Text.RegularExpressions.Regex.Match(label, @"\b(\d{1,2}):(\d{2})\b");
        var uur = tijd.Success ? int.Parse(tijd.Groups[1].Value) : 0;
        var minuut = tijd.Success ? int.Parse(tijd.Groups[2].Value) : 0;

        var dag = DateOnly.FromDateTime(nu.Date);
        var cijferDatum = System.Text.RegularExpressions.Regex.Match(
            label, @"\b(\d{1,2})[-/](\d{1,2})(?:[-/](\d{4}))?\b");
        var maandNaam = System.Text.RegularExpressions.Regex.Match(
            label, $@"\b(\d{{1,2}})\s+({string.Join('|', Maanden.Distinct())})\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Mails van de afgelopen week labelt OWA alleen met weekdag + tijd ("vr 21:56").
        var weekdag = System.Text.RegularExpressions.Regex.Match(
            label, @"\b(zo|ma|di|wo|do|vr|za|sun|mon|tue|wed|thu|fri|sat|dim|lun|mar|mer|jeu|ven|sam)\b\.?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        try
        {
            if (cijferDatum.Success)
            {
                var jaar = cijferDatum.Groups[3].Success ? int.Parse(cijferDatum.Groups[3].Value) : nu.Year;
                dag = new DateOnly(jaar, int.Parse(cijferDatum.Groups[2].Value),
                    int.Parse(cijferDatum.Groups[1].Value));
            }
            else if (maandNaam.Success)
            {
                var maand = (Array.IndexOf(Maanden, maandNaam.Groups[2].Value.ToLowerInvariant()) % 12) + 1;
                dag = new DateOnly(nu.Year, maand, int.Parse(maandNaam.Groups[1].Value));
            }
            else if (weekdag.Success)
            {
                // De meest recente eerdere dag met die naam (vandaag zelf toont alleen een
                // tijd, dus dezelfde weekdag betekent: vorige week).
                var namen = new[]
                {
                    "zo|sun|dim", "ma|mon|lun", "di|tue|mar", "wo|wed|mer",
                    "do|thu|jeu", "vr|fri|ven", "za|sat|sam",
                };
                var afk = weekdag.Groups[1].Value.ToLowerInvariant();
                var idx = Array.FindIndex(namen, n => n.Split('|').Contains(afk));
                if (idx >= 0)
                {
                    var terug = ((int)nu.DayOfWeek - idx + 7) % 7;
                    dag = DateOnly.FromDateTime(nu.Date).AddDays(-(terug == 0 ? 7 : terug));
                }
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(
                label, @"\b(gisteren|yesterday|hier)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                dag = DateOnly.FromDateTime(nu.Date).AddDays(-1);
            }
            else if (!tijd.Success)
            {
                return null; // geen tijd en geen datum in het label
            }
            if (dag > DateOnly.FromDateTime(nu.Date))
            {
                dag = dag.AddYears(-1); // datum zonder jaartal die in de toekomst zou vallen
            }
            var resultaat = new DateTimeOffset(dag.Year, dag.Month, dag.Day, uur, minuut, 0, nu.Offset);
            if (resultaat > nu.AddMinutes(10) && !cijferDatum.Success && !maandNaam.Success)
            {
                // "21:56" zonder datum kan niet in de toekomst liggen: dan was het gisteren.
                resultaat = resultaat.AddDays(-1);
            }
            return resultaat;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Alle mails in de zichtbare inboxlijst (gelezen én ongelezen), met afzender en
    /// onderwerp uit de rij-spans — zonder mails te openen.
    /// </summary>
    public async Task<List<OutlookBericht>> InboxAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            // De verborgen pagina veroudert ondanks de anti-throttling-vlaggen (OWA gaat
            // zelf in een slaapstand): periodiek volledig herladen, net als bij Teams.
            var herladen = DateTimeOffset.Now - _laatstHerladen > TimeSpan.FromMinutes(10);
            if (herladen)
            {
                await HerlaadAsync(ct);
            }
            var rijen = await LijstKernAsync(ct, netHerladen: herladen);
            // Nul rijen vlak na een herlaadbeurt betekent bijna altijd "de lijst was nog niet
            // klaar", niet "de inbox is leeg". Eén keer opnieuw laden en opnieuw lezen scheelt
            // een hele pollronde waarin de cockpit met de oude cache blijft staan.
            if (rijen.Count == 0 && !LaatsteScrapeEchtLeeg)
            {
                await HerlaadAsync(ct);
                rijen = await LijstKernAsync(ct);
            }
            return rijen;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// De pagina vers laden en wachten tot de maillijst er écht staat. Vroeger stond hier een
    /// vaste wachttijd van vier seconden: op een trage ochtend te kort (lege lijst) en op een
    /// snelle dag pure vertraging. Nu wordt er gepolst tot er rijen zijn, met een ruime
    /// bovengrens voor het geval OWA blijft hangen.
    /// </summary>
    private async Task HerlaadAsync(CancellationToken ct)
    {
        _web!.CoreWebView2!.Reload();
        for (var i = 0; i < 50; i++)
        {
            await Task.Delay(400, ct);
            if (await IsIngelogdAsync())
            {
                break;
            }
        }
        // Wachten op gerenderde rijen in plaats van op de klok (max. ~12 s). Bij een lege
        // Postvak IN komt er nooit een rij, dus ook hier stoppen zodra de mailmodule staat:
        // anders kost elke herlaadbeurt de volle twaalf seconden voor niets.
        for (var i = 0; i < 40; i++)
        {
            var stand = await JsAsync(
                """
                JSON.stringify({
                    rijen: document.querySelectorAll('[data-convid], [role="option"]').length,
                    gereed: document.querySelectorAll('[role="treeitem"]').length > 0 &&
                        !!(document.getElementById('MailList') ||
                           document.querySelector('[id^="MailList"]')),
                })
                """);
            var rijen = 0;
            var gereed = false;
            try
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(stand) ?? stand);
                rijen = doc.RootElement.GetProperty("rijen").GetInt32();
                gereed = doc.RootElement.GetProperty("gereed").GetBoolean();
            }
            catch
            {
                // Pagina nog aan het opbouwen: gewoon nog een rondje.
            }
            if (rijen > 0)
            {
                // Nog even laten synchroniseren: de eerste rijen staan er meestal vóór de rest.
                await Task.Delay(700, ct);
                break;
            }
            if (gereed && i >= 6)
            {
                break; // module staat er, lijst blijft leeg — dat is dan ook zo
            }
            await Task.Delay(300, ct);
        }
        _laatstHerladen = DateTimeOffset.Now;
    }

    /// <summary>
    /// Leest de rijen van de nu geopende maillijst (Postvak IN of een andere map). De
    /// OWA-lijst is gevirtualiseerd: een asynchrone job scrolt erdoorheen en verzamelt
    /// alle rijen (max. 100). Aanroeper houdt zelf het slot vast.
    /// </summary>
    private async Task<List<OutlookBericht>> LijstKernAsync(
        CancellationToken ct, bool netHerladen = true)
    {
        // Na een verse herlaadbeurt heeft de lijst tijd nodig; op een gewone pollronde staat
        // de pagina er al minuten. Toch niet meteen opgeven: OWA rendert een verborgen lijst
        // soms even opnieuw, en dan las één seconde geduld een lege lijst waar mail stond.
        var geduld = netHerladen ? 8 : 4;
        {
            await JsAsync(GeduldIn(
                """
                (function () {
                    window.__wmInbox = null;
                    (async () => {
                        // Vlak na een (her)laadbeurt is de lijst nog leeg: eerst wachten
                        // tot er echt rijen gerenderd zijn (max. ~15 s). Is de map echt
                        // leeg, dan komt er nooit een rij — na vier seconden herkennen we
                        // dat aan de gerenderde mappenboom plus lege lijstcontainer, en
                        // stoppen we meteen. Anders kostte elke lege Postvak IN 15 seconden.
                        // Vier seconden is ruim: een trage lijst heeft daarvóór al rijen,
                        // en de herlaadbeurt wacht zelf ook al op gerenderde rijen.
                        const lijstBak = () => document.getElementById('MailList') ||
                            document.querySelector('[id^="MailList"], [aria-label*="Berichtenlijst"], ' +
                                '[aria-label*="Message list"]');
                        for (let w = 0; w < 30; w++) {
                            if (document.querySelectorAll(
                                '[data-convid], [role="option"]').length > 0) break;
                            if (w >= __GEDULD__ && document.querySelectorAll('[role="treeitem"]').length > 0 &&
                                lijstBak()) break;
                            await new Promise(r => setTimeout(r, 500));
                        }
                        const verzameld = new Map();
                        // Alleen rijen uit de berichtenlijst zelf. In de nieuwe OWA zijn de
                        // bijlagetegels van een geopende mail óók [role="option"]; zonder
                        // deze begrenzing werd één mail met negen zip-bijlagen tien "mails"
                        // in de cockpit.
                        const inLijst = (el) => {
                            const bak = lijstBak();
                            return !bak || bak.contains(el);
                        };
                        // Een bijlagetegel herken je aan de bestandsgrootte in de tekst.
                        const isBijlage = (tekst) =>
                            /\d+([.,]\d+)?\s?(bytes|[kKmMgG]B)/.test(tekst) &&
                            !/\d{1,2}:\d{2}/.test(tekst);
                        const lees = () => {
                            for (const r of document.querySelectorAll('[data-convid], [role="option"]')) {
                                const label = r.getAttribute('aria-label') || '';
                                if (!label) continue;
                                if (!inLijst(r)) continue;
                                if (isBijlage((r.textContent || '').replace(/\s+/g, ' '))) continue;
                                // In Outlook gesluimerde (gesnoozede) mails overslaan: die komen
                                // vanzelf terug in de lijst zodra de sluimertijd voorbij is.
                                if (/sluimer|gesluimerd|snooze|uitgesteld tot/i.test(label)) continue;
                                const ongelezen = /ongelezen|niet gelezen|unread|non lu/i.test(label);
                                const teksten = [];
                                for (const el of r.querySelectorAll('span, div')) {
                                    if (el.children.length > 0) continue;
                                    const t = (el.textContent || '').replace(/\s+/g, ' ').trim();
                                    if (t.length < 2 || teksten.includes(t)) continue;
                                    if (/^\d{1,2}:\d{2}$/.test(t) || /^[a-z]{2}\s+\d{1,2}[-/]\d{1,2}/i.test(t) ||
                                        /^(ongelezen|niet gelezen|unread|vlag|flag|bijlage|attachment|extern|external|externe?)$/i.test(t)) continue;
                                    teksten.push(t.slice(0, 400));
                                    if (teksten.length >= 12) break;
                                }
                                const sleutel = teksten.slice(0, 2).join('|') || label.slice(0, 80);
                                if (!verzameld.has(sleutel)) {
                                    verzameld.set(sleutel, { label: label.slice(0, 600), teksten, ongelezen,
                                        ruw: (r.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 600) });
                                }
                            }
                        };
                        // De scrollbare voorouder van de mailrijen zoeken.
                        let scroller = document.querySelector('[data-convid], [role="option"]');
                        while (scroller && scroller !== document.body) {
                            const s = getComputedStyle(scroller);
                            if (/(auto|scroll)/.test(s.overflowY) &&
                                scroller.scrollHeight > scroller.clientHeight + 10) break;
                            scroller = scroller.parentElement;
                        }
                        lees();
                        if (scroller && scroller !== document.body) {
                            for (let i = 0; i < 20 && verzameld.size < 80; i++) {
                                scroller.scrollTop += scroller.clientHeight * 0.8;
                                await new Promise(r => setTimeout(r, 220));
                                const voor = verzameld.size;
                                lees();
                                if (verzameld.size === voor &&
                                    scroller.scrollTop + scroller.clientHeight >= scroller.scrollHeight - 5) break;
                            }
                            scroller.scrollTop = 0; // netjes terug naar boven
                        }
                        window.__wmInbox = [...verzameld.values()];
                    })();
                    return true;
                })()
                """, geduld));
            var json = "[]";
            for (var i = 0; i < 190; i++) // wachten op renderen + scrollen (samen max ~38 s)
            {
                await Task.Delay(200, ct);
                var klaar = await JsAsync("JSON.stringify(window.__wmInbox)");
                if (klaar is not ("null" or "\"null\""))
                {
                    json = JsonSerializer.Deserialize<string>(klaar) ?? "[]";
                    break;
                }
            }
            try
            {
                // Diagnose: het ruwe DOM-resultaat bewaren zodat de parsing op het echte
                // CED-formaat afgestemd kan worden als de lijst er raar uitziet.
                File.WriteAllText(Path.Combine(DataDir, "outlook-debug.json"), json);
            }
            catch
            {
                // Alleen diagnose; nooit blokkerend.
            }
            using var doc = JsonDocument.Parse(json);
            var berichten = new List<OutlookBericht>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var label = e.GetProperty("label").GetString() ?? "";
                var teksten = e.GetProperty("teksten").EnumerateArray()
                    .Select(t => t.GetString() ?? "").Where(t => t.Length > 0).ToList();

                // Het CED-DOM levert per rij nette spans: [afzender, onderwerp, (preview…)] —
                // het aria-label is alleen reserve (spatie-gescheiden, dus niet betrouwbaar
                // te splitsen: "Steven van Dam" bevat bv. zelf het woord "van").
                var van = teksten.Count > 0 ? teksten[0] : "";
                var onderwerp = teksten.Count > 1 ? teksten[1] : "";
                var preview = string.Join(" · ", teksten.Skip(2)).Trim();
                if (van.Length == 0)
                {
                    van = System.Text.RegularExpressions.Regex
                        .Replace(label, @"^(ongelezen|niet gelezen|unread|non lu)\s*", "",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        .Trim();
                }
                if (van.Length > 0)
                {
                    berichten.Add(new OutlookBericht(
                        van[..Math.Min(60, van.Length)],
                        onderwerp[..Math.Min(150, onderwerp.Length)],
                        preview[..Math.Min(400, preview.Length)],
                        e.TryGetProperty("ongelezen", out var o) && o.GetBoolean(),
                        ParseMailMoment(label)));
                }
            }
            // Onderscheid "nog niet geladen" van "map is echt leeg": bij nul rijen kijken
            // of de mailmodule wél volledig gerenderd is (dan is leeg ook echt leeg, en
            // mag de cockpit zijn cache-terugval overslaan).
            //
            // Deze toets keek vroeger of [role="main"] tekst bevatte. Op de nieuwe OWA
            // (outlook.cloud.microsoft) is dat element een lege "Ga naar bericht"-regio, dus
            // de toets was altijd onwaar: een leeggewerkte Postvak IN gold als "mislukte
            // scrape" en de cockpit zette de gearchiveerde mails elke ronde weer terug.
            // Nu op structuur: mappenboom gerenderd + lijstcontainer aanwezig + geen rijen.
            LaatsteScrapeEchtLeeg = berichten.Count == 0 && await JsAsync(
                """
                (function () {
                    const mappen = document.querySelectorAll('[role="treeitem"]').length;
                    const lijst = document.getElementById('MailList') ||
                        document.querySelector('[id^="MailList"], [aria-label*="Berichtenlijst"], ' +
                            '[aria-label*="Message list"]');
                    return mappen > 0 && !!lijst &&
                        document.querySelectorAll('[data-convid], [role="option"]').length === 0;
                })()
                """) == "true";
            return berichten;
        }
    }

    /// <summary>Vult het geduld-getal in het verzamelscript in (raw string, dus via Replace).</summary>
    private static string GeduldIn(string script, int geduld) =>
        script.Replace("__GEDULD__", geduld.ToString());

    /// <summary>True als de laatste lijstscrape een volledig geladen maar lege map zag.</summary>
    public bool LaatsteScrapeEchtLeeg { get; private set; }

    /// <summary>Uitkomst van <see cref="DiagnoseAsync"/>: pad naar de schermafdruk, of leeg.</summary>
    public sealed record OutlookDiagnose(string Schermafdruk);

    /// <summary>
    /// Kijkt de hele keten na en schrijft onderweg mee: is de sessie ingelogd, staat de
    /// mailmodule er, hoeveel rijen vinden de selectors, en wat levert de scrape op. Bedoeld
    /// voor als het herladen "niets doet" — dan zie je meteen of het aan de aanmelding, aan
    /// een gewijzigde OWA-DOM of gewoon aan een lege inbox ligt.
    /// </summary>
    public async Task<OutlookDiagnose> DiagnoseAsync(Action<string> log, CancellationToken ct)
    {
        log($"Ooit gekoppeld: {(OoitGekoppeld ? "ja" : "nee")}");
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 25))
            {
                log("Niet aangemeld — gebruik 'Outlook aanmelden…' (het CED-tenant vraagt " +
                    "dagelijks MFA).");
                return new OutlookDiagnose("");
            }
            log("Aangemeld, sessie staat.");
            log($"Laatste herlaadbeurt: {(_laatstHerladen == DateTimeOffset.MinValue
                ? "nog niet deze sessie" : _laatstHerladen.ToLocalTime().ToString("HH:mm:ss"))}");

            var waar = await JsAsync(
                "JSON.stringify({url: location.href, titel: document.title})");
            log("Pagina: " + Ontdubbel(waar));

            async Task TelAsync(string omschrijving, string selector)
            {
                var n = await JsAsync($"document.querySelectorAll('{selector}').length");
                log($"  {omschrijving,-28} {n}");
            }
            log("Selectors:");
            await TelAsync("mappen (treeitem)", "[role=\\\"treeitem\\\"]");
            await TelAsync("rijen (data-convid)", "[data-convid]");
            await TelAsync("rijen (role=option)", "[role=\\\"option\\\"]");
            await TelAsync("zoekveld", "#topSearchInput, [role=\\\"searchbox\\\"]");

            var labels = await JsAsync(
                """
                JSON.stringify([...document.querySelectorAll('[data-convid], [role="option"]')]
                    .slice(0, 5).map(r => (r.getAttribute('aria-label') || '(geen label)').slice(0, 120)))
                """);
            log("Eerste aria-labels: " + Ontdubbel(labels));

            var start = DateTimeOffset.Now;
            var rijen = await LijstKernAsync(ct);
            log($"Scrape: {rijen.Count} rij(en) in {(DateTimeOffset.Now - start).TotalSeconds:0.0} s" +
                (LaatsteScrapeEchtLeeg ? " (map is écht leeg)" : ""));
            foreach (var r in rijen.Take(8))
            {
                log($"  • {r.Van} — {r.Onderwerp}" +
                    (r.Datum is { } d ? $" ({d.ToLocalTime():dd-MM HH:mm})" : "") +
                    (r.Ongelezen ? " [ongelezen]" : ""));
            }
            if (rijen.Count > 8)
            {
                log($"  … en nog {rijen.Count - 8}");
            }

            var store = LaadMails();
            var bestand = File.Exists(MailStoreFile) ? new FileInfo(MailStoreFile).Length : 0;
            log($"Tekstcache: {store.Count} mail(s), {bestand / 1024 / 1024.0:0.0} MB op schijf");

            var pad = Path.Combine(DataDir, "outlook-diagnose.png");
            try
            {
                using var beeld = new MemoryStream();
                await _web!.CoreWebView2!.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png, beeld);
                File.WriteAllBytes(pad, beeld.ToArray());
                log($"Schermafdruk: {pad}");
                return new OutlookDiagnose(pad);
            }
            catch (Exception ex)
            {
                log($"Schermafdruk mislukt: {ex.Message}");
                return new OutlookDiagnose("");
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Eén stuk JavaScript in de Outlook-sessie draaien en het resultaat teruggeven. Alleen
    /// voor diagnose (zie de CLI-schakelaar --owajs): OWA wijzigt zijn DOM geregeld, en dan
    /// moet je ter plekke kunnen kijken welke selector het nu wél doet.
    /// </summary>
    public async Task<string> DiagnoseJsAsync(string script, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct, wachtSeconden: 25))
            {
                return "(niet aangemeld)";
            }
            return await JsAsync(script);
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>JsAsync geeft JSON terug; een string-resultaat zit dan nog eens in quotes.</summary>
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

    public sealed record CcMail(
        string Van, string Onderwerp, string Tekst, string Html, DateTimeOffset Datum);

    /// <summary>
    /// Nieuwe mails uit de map "CC" (waar een Outlook-regel alle mails heen verplaatst
    /// waarin Maarten in de cc staat): de map openen, de lijst lezen, en van elke nog
    /// onbekende mail de volledige tekst ophalen. Eindigt altijd weer in Postvak IN.
    /// </summary>
    public async Task<List<CcMail>> CcMailsAsync(
        IReadOnlyCollection<string> bekendeSleutels, int max, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            void Log(string melding)
            {
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "cc-debug.txt"),
                        $"{DateTime.Now:HH:mm:ss} {melding}\r\n");
                }
                catch
                {
                    // Alleen diagnose.
                }
            }
            if (!await KlikMapAsync(@"^\s*cc\s*(\d+)?\s*$", ct))
            {
                // Diagnose: welke treeitems ziet de sessie dan wél? (Mappenbalk dicht?)
                var mappen = await JsAsync(
                    """
                    JSON.stringify([...document.querySelectorAll('[role="treeitem"]')]
                        .slice(0, 40).map(x => (x.getAttribute('title') || '') + '::' +
                            (x.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 30)))
                    """);
                // Extra diagnose: alle knoppen met hun labels, zodat we de juiste
                // paneel-openknop kunnen herkennen als de hamburger-match faalt.
                var knoppen = await JsAsync(
                    """
                    JSON.stringify([...document.querySelectorAll('button, [role="button"]')]
                        .map(x => ((x.getAttribute('aria-label') || '') + '|' +
                            (x.getAttribute('title') || '') + '|' +
                            (x.querySelector('i,[data-icon-name]')?.getAttribute('data-icon-name') || ''))
                            .trim())
                        .filter(s => s.replace(/\|/g, '').length > 0)
                        .slice(0, 60))
                    """);
                Log($"CC-map niet gevonden in de mappenbalk; treeitems: {mappen}");
                Log($"knoppen: {knoppen}");
                try
                {
                    using var beeld = new MemoryStream();
                    await _web!.CoreWebView2!.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, beeld);
                    File.WriteAllBytes(Path.Combine(DataDir, "outlook-screen.png"), beeld.ToArray());
                }
                catch
                {
                    // Alleen diagnose.
                }
                // Belangrijk: dit is een échte navigatiefout (niet "geen nieuwe CC-mails").
                // Gooien zodat de dag NIET als verwerkt wordt gemarkeerd en de volgende
                // poll het opnieuw probeert.
                throw new InvalidOperationException("CC-map niet gevonden in de mappenbalk.");
            }
            try
            {
                var rijen = await LijstKernAsync(ct);
                Log($"CC-map open, {rijen.Count} rijen ({rijen.Count(r => r.Ongelezen)} ongelezen); " +
                    $"{bekendeSleutels.Count} al bekend");
                var resultaat = new List<CcMail>();
                foreach (var b in rijen)
                {
                    // Alleen ongelezen mails: wat Maarten al in Outlook las, hoort niet
                    // meer in het CC-overzicht.
                    if (!b.Ongelezen)
                    {
                        continue;
                    }
                    var sleutel = CcSleutel(b.Van, b.Onderwerp, b.Datum);
                    if (bekendeSleutels.Contains(sleutel) || resultaat.Count >= max)
                    {
                        continue;
                    }
                    // De rij openen (we staan in de CC-map, dus gewoon in de lijst klikken)
                    // en de volledige inhoud lezen.
                    var vanJs = JsonSerializer.Serialize(b.Van);
                    var ondJs = JsonSerializer.Serialize(b.Onderwerp);
                    var geklikt = await JsAsync(
                        $$"""
                        (function () {
                            {{KlikHelpers}}
                            const norm = s => (s + '').replace(/\s+/g, ' ').toLowerCase();
                            const van = norm({{vanJs}}), ond = norm({{ondJs}});
                            const ondKort = ond.slice(0, 25);
                            const rij = [...document.querySelectorAll(
                                '[data-convid], [role="option"]')].find(x => {
                                const t = norm((x.getAttribute('aria-label') || '') + ' ' +
                                    x.textContent);
                                return t.includes(van) &&
                                    (!ond || t.includes(ond) || t.includes(ondKort));
                            });
                            if (!rij) return false;
                            klik(rij);
                            return true;
                        })()
                        """);
                    if (geklikt != "true")
                    {
                        Log($"rij niet aanklikbaar: {b.Van} | {b.Onderwerp}");
                        continue;
                    }
                    await Task.Delay(2500, ct); // leesvenster laten laden
                    var (tekst, html, exact, _, _, _) = await LeesGeopendeMailKernAsync(ct);
                    Log($"gelezen: {b.Van} | {b.Onderwerp} (tekst {tekst.Length}, html {html.Length})");
                    if (tekst.Length > 0 || html.Length > 0)
                    {
                        resultaat.Add(new CcMail(b.Van, b.Onderwerp, tekst, html,
                            exact ?? b.Datum ?? DateTimeOffset.Now));
                    }
                }
                return resultaat;
            }
            finally
            {
                await KlikMapAsync(@"postvak\s*in|inbox|boîte de réception", ct);
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>Stabiele sleutel voor een CC-mail (om dubbele analyses te voorkomen).</summary>
    public static string CcSleutel(string van, string onderwerp, DateTimeOffset? datum) =>
        $"cc:{van}|{onderwerp}|{datum:yyyyMMddHHmm}";

    /// <summary>
    /// Zet de volledige CC-map in Outlook-web op gelezen via het mapcontextmenu ("Alles als
    /// gelezen markeren"). Gebruikt bij het archiveren van de CC-overzichtsrij: alle mails
    /// waarin Maarten in de cc stond gaan zo in één keer op gelezen. Eindigt weer in Postvak
    /// IN. Geeft true als de menu-actie is aangeklikt.
    /// </summary>
    public async Task<bool> MarkeerCcGelezenAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            void Log(string melding)
            {
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "cc-debug.txt"),
                        $"{DateTime.Now:HH:mm:ss} [gelezen] {melding}\r\n");
                }
                catch
                {
                    // Alleen diagnose.
                }
            }
            try
            {
                // Rechtsklik op de CC-map in de mappenboom om het contextmenu te openen.
                var geopend = await JsAsync(
                    $$"""
                    (function () {
                        const schoon = s => (s + '')
                            .replace(/\s*[-–]\s*[\d., ]+\s*items?.*$/i, '')
                            .replace(/\s+/g, ' ').trim();
                        const pat = /^\s*cc\s*(\d+)?\s*$/i;
                        const map = [...document.querySelectorAll('[role="treeitem"]')]
                            .find(x => pat.test(schoon(x.getAttribute('title') ||
                                x.querySelector('span[title]')?.getAttribute('title') || '')));
                        if (!map) return 'geen-map';
                        map.scrollIntoView({ block: 'center' });
                        const r = map.getBoundingClientRect();
                        const opts = { bubbles: true, cancelable: true, view: window,
                            clientX: r.x + r.width / 2, clientY: r.y + r.height / 2,
                            button: 2, buttons: 2 };
                        map.dispatchEvent(new PointerEvent('pointerdown', opts));
                        map.dispatchEvent(new MouseEvent('mousedown', opts));
                        map.dispatchEvent(new PointerEvent('pointerup', opts));
                        map.dispatchEvent(new MouseEvent('mouseup', opts));
                        map.dispatchEvent(new MouseEvent('contextmenu', opts));
                        return 'menu';
                    })()
                    """);
                if (geopend.Contains("geen-map"))
                {
                    Log("CC-map niet gevonden in de mappenboom");
                    return false;
                }
                await Task.Delay(800, ct); // contextmenu laten verschijnen

                // "Alles als gelezen markeren" (meerdere talen) in het contextmenu aanklikken.
                var geklikt = await JsAsync(
                    $$"""
                    (function () {
                        {{KlikHelpers}}
                        const pat = /alles als gelezen markeren|alle als gelezen|markeer alles als gelezen|mark all as read|tout marquer comme lu|tout lu/i;
                        const b = [...document.querySelectorAll(
                            '[role="menuitem"], button, [role="button"]')]
                            .find(x => pat.test(((x.getAttribute('aria-label') || '') + ' ' +
                                (x.getAttribute('title') || '') + ' ' + (x.textContent || '')).trim()));
                        if (!b) return 'geen-knop';
                        klik(b);
                        return 'ok';
                    })()
                    """);
                Log($"contextmenu-actie: {geklikt}");
                if (geklikt.Contains("ok"))
                {
                    await Task.Delay(1200, ct); // de actie laten uitvoeren
                    return true;
                }
                return false;
            }
            finally
            {
                await KlikMapAsync(@"postvak\s*in|inbox|boîte de réception", ct);
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    // OWA reageert niet altijd op een kaal click(): volledige muisevent-reeks sturen.
    private const string KlikHelpers =
        """
        function klik(el) {
            el.scrollIntoView({ block: 'center' });
            const r = el.getBoundingClientRect();
            const opts = { bubbles: true, cancelable: true, view: window,
                clientX: r.x + r.width / 2, clientY: r.y + r.height / 2, buttons: 1 };
            for (const t of ['pointerover', 'mouseover', 'pointerdown', 'mousedown',
                             'pointerup', 'mouseup', 'click']) {
                el.dispatchEvent(t.startsWith('pointer')
                    ? new PointerEvent(t, opts) : new MouseEvent(t, opts));
            }
        }
        function zoekKnop(wortel, patroon) {
            return [...wortel.querySelectorAll('button, [role="button"], [role="menuitem"]')]
                .find(x => patroon.test(((x.getAttribute('aria-label') || '') + ' ' +
                    (x.getAttribute('title') || '') + ' ' + (x.textContent || '')).trim()));
        }
        """;

    /// <summary>
    /// Archiveert één mail in Outlook-web en markeert hem als gelezen: de rij in de lijst
    /// selecteren (openen in het leesvenster zet hem al op gelezen), daarna de werkbalkknoppen
    /// "Als gelezen markeren" (als die er nog staat) en "Archiveren" aanklikken.
    /// Resultaat: "ok", "rij-niet-gevonden" of "knop-niet-gevonden".
    /// </summary>
    public async Task<string> ArchiveerAsync(string van, string onderwerp, CancellationToken ct,
        string url = "")
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            // De koninklijke weg: is de directe link naar de mail bekend (bewaard bij het
            // ophalen), dan de mail rechtstreeks openen en daar op "verwerkt"/archiveren
            // klikken — geen gevirtualiseerde lijst of zoekbalk nodig.
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var viaUrl = await ArchiveerViaUrlAsync(url, van, onderwerp, ct);
                if (viaUrl == "ok")
                {
                    return "ok";
                }
                // Anders gewoon doorvallen naar de lijst/zoek-route hieronder.
            }
            var vanJs = JsonSerializer.Serialize(van);
            var onderwerpJs = JsonSerializer.Serialize(
                onderwerp.Equals("ongelezen bericht", StringComparison.OrdinalIgnoreCase) ? "" : onderwerp);

            // De rij vinden en aanklikken: direct in de lijst of via het zoekveld (met een
            // échte Enter — synthetische toetsen negeert OWA net als synthetische kliks).
            if (await VindEnOpenRijAsync(van, onderwerp, ct) != "ok")
            {
                try
                {
                    File.WriteAllText(Path.Combine(DataDir, "outlook-archief-debug.json"),
                        $"{{\"stap\":\"rij-niet-gevonden\",\"van\":{vanJs},\"onderwerp\":{onderwerpJs}}}");
                }
                catch
                {
                    // Alleen diagnose (details staan al in outlook-zoek-debug.json).
                }
                await SluitZoekweergaveAsync();
                return "rij-niet-gevonden";
            }
            await Task.Delay(1500, ct); // leesvenster laten openen (markeert doorgaans al als gelezen)

            // Expliciet als gelezen markeren als die knop ergens staat (dan was hij nog ongelezen).
            await JsAsync(
                $$"""
                (function () {
                    {{KlikHelpers}}
                    const b = zoekKnop(document, /als gelezen markeren|mark as read|marquer comme lu/i);
                    if (b) klik(b);
                })()
                """);
            await Task.Delay(400, ct);

            // Archiveren met échte muiskliks (de ribbon negeert synthetische JS-kliks) en
            // verificatie dat de rij daarna echt uit het postvak is. Voorkeur: de Quick
            // Step "Verwerkt"; anders de archiveerknop op de rij of in de werkbalk.
            var vindRijExpr =
                $$"""
                [...document.querySelectorAll('[data-convid], [role="option"]')].find(x => {
                    // Witruimte normaliseren: de rijtekst bevat regeleinden tussen de
                    // tekstdelen, waardoor een letterlijke includes-match stil faalde.
                    const norm = s => (s + '').replace(/\s+/g, ' ').toLowerCase();
                    const van = norm({{vanJs}}), ond = norm({{onderwerpJs}});
                    const ondKort = ond.slice(0, 25);
                    const t = norm((x.getAttribute('aria-label') || '') + ' ' + x.textContent);
                    return t.includes(van) && (!ond || t.includes(ond) || t.includes(ondKort));
                })
                """;
            const string KnopExpr =
                """
                (function () {
                    const b = zoekKnop(document, /\bverwerkt\b/i) ||
                        zoekKnop(document, /\barchiv(eren|e|er)\b/i);
                    if (!b || b.getAttribute('aria-disabled') === 'true' || b.disabled) return null;
                    return b.matches('button') ? b : (b.querySelector('button') || b);
                })()
                """;
            // "Rij niet in de DOM" telt alleen als "verdwenen" wanneer de lijst zélf nog
            // rijen toont: opent OWA de mail schermvullend (of hertekent de lijst even),
            // dan is de hele lijst weg en zegt een ontbrekende rij niets — dat gaf eerder
            // een vals "ok" waarna de mail gewoon in de inbox bleef staan.
            var klaarExpr =
                $$"""
                (function () {
                    const rij = {{vindRijExpr}};
                    if (rij) return /\b(archief|archive|verwerkt)\b/i.test(
                        (rij.getAttribute('aria-label') || '') + ' ' + rij.textContent);
                    return document.querySelectorAll('[data-convid], [role="option"]').length > 0;
                })()
                """;
            var stand = "niet-verdwenen";
            for (var poging = 0; poging < 3; poging++)
            {
                // Klaar? (rij weg, of in zoekresultaten verhuisd naar Verwerkt/Archief)
                if (await JsAsync(klaarExpr) == "true")
                {
                    stand = "ok";
                    break;
                }
                // Rij selecteren (JS-klik werkt prima op rijen) zodat de werkbalk actief is.
                await JsAsync(
                    $$"""
                    (function () {
                        {{KlikHelpers}}
                        const rij = {{vindRijExpr}};
                        if (rij) klik(rij);
                        return true;
                    })()
                    """);
                await Task.Delay(900, ct);
                if (!await KlikFysiekAsync(KnopExpr))
                {
                    stand = "knop-niet-gevonden";
                    continue; // knop kan bij de volgende poging alsnog verschijnen
                }
                stand = "geklikt";
                await Task.Delay(2200, ct); // de verplaatsing laten uitvoeren
            }
            stand = stand == "geklikt" ? "niet-verdwenen" : stand;
            if (stand != "ok" && await JsAsync(klaarExpr) == "true")
            {
                stand = "ok"; // laatste klik had alsnog effect
            }
            // Definitieve dubbelcheck: vers naar Postvak IN en daar controleren dat de
            // mail écht weg is. Alleen dat telt — de checks hierboven kijken naar een
            // DOM die door de archiveeractie zelf in beweging is.
            if (stand == "ok")
            {
                _web!.CoreWebView2!.Navigate("https://outlook.office.com/mail/");
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(500, ct);
                    if (await IsIngelogdAsync())
                    {
                        break;
                    }
                }
                await Task.Delay(2500, ct); // lijst laten renderen
                if (await JsAsync($$"""(function () { return !!({{vindRijExpr}}); })()""") == "true")
                {
                    stand = "niet-verdwenen";
                }
                _laatstHerladen = DateTimeOffset.Now;
            }
            stand = $"{{\"stap\":\"{stand}\",\"van\":{vanJs}}}";
            try
            {
                // Altijd wegschrijven (ook bij succes): welke knop is geklikt en wat het
                // resultaat was — onmisbaar als OWA weer eens van DOM verandert.
                File.WriteAllText(Path.Combine(DataDir, "outlook-archief-debug.json"), stand);
            }
            catch
            {
                // Alleen diagnose.
            }
            // Was de mail via het zoekveld gevonden, dan de zoekweergave weer sluiten zodat
            // de sessie terugkeert naar het gewone Postvak IN (voor de volgende poll).
            await SluitZoekweergaveAsync();
            var genormaliseerd = stand.Replace("\\\"", "\"");
            if (genormaliseerd.Contains("\"stap\":\"ok\""))
            {
                return "ok";
            }
            return genormaliseerd.Contains("knop-niet-gevonden")
                ? "knop-niet-gevonden" : "niet-verdwenen";
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Zoekt een element via de opgegeven JS-expressie (KlikHelpers beschikbaar), scrolt het
    /// in beeld en klikt er fysiek op (Win32-muisberichten): de Outlook-ribbon negeert
    /// synthetische JS-kliks, een echt muisbericht niet. Retourneert false als de expressie
    /// niets oplevert.
    /// </summary>
    private async Task<bool> KlikFysiekAsync(string elementExpr, bool rechts = false)
    {
        var rectJson = await JsAsync(
            $$"""
            JSON.stringify((function () {
                {{KlikHelpers}}
                const el = {{elementExpr}};
                if (!el) return null;
                el.scrollIntoView({ block: 'center' });
                const r = el.getBoundingClientRect();
                if (r.width === 0 || r.height === 0) return null;
                return { x: r.x + r.width / 2, y: r.y + r.height / 2,
                         vw: window.innerWidth };
            })())
            """);
        var vlak = JsonSerializer.Deserialize<string>(rectJson) ?? rectJson;
        if (vlak is "null" or "" || !vlak.StartsWith('{'))
        {
            return false;
        }
        using var doc = JsonDocument.Parse(vlak);
        return _web is not null && FysiekeKlik.Klik(_web,
            doc.RootElement.GetProperty("x").GetDouble(),
            doc.RootElement.GetProperty("y").GetDouble(),
            doc.RootElement.GetProperty("vw").GetDouble(), rechts);
    }

    /// <summary>
    /// Vindt de rij van een mail en klikt hem aan: eerst direct in de (gerenderde) lijst,
    /// anders via het zoekveld — waarbij de tekst via JS gezet wordt maar Enter als échte
    /// toetsaanslag vertrekt (OWA negeert synthetische toetsen net als synthetische kliks).
    /// Retourneert "ok" of "niet-gevonden"; bij falen gaan rijen-dump en screenshot naar
    /// outlook-zoek-debug.json / outlook-screen.png.
    /// </summary>
    private async Task<string> VindEnOpenRijAsync(string van, string onderwerp, CancellationToken ct)
    {
        var vanJs = JsonSerializer.Serialize(van);
        var onderwerpJs = JsonSerializer.Serialize(
            onderwerp.Equals("ongelezen bericht", StringComparison.OrdinalIgnoreCase) ? "" : onderwerp);
        var vindExpr =
            $$"""
            [...document.querySelectorAll('[data-convid], [role="option"]')].find(x => {
                const norm = s => (s + '').replace(/\s+/g, ' ').toLowerCase();
                const van = norm({{vanJs}}), ond = norm({{onderwerpJs}});
                const ondKort = ond.slice(0, 25);
                const t = norm((x.getAttribute('aria-label') || '') + ' ' + x.textContent);
                return t.includes(van) && (!ond || t.includes(ond) || t.includes(ondKort));
            })
            """;
        var klikRijJs =
            $$"""
            (function () {
                {{KlikHelpers}}
                const rij = {{vindExpr}};
                if (!rij) return false;
                klik(rij);
                return true;
            })()
            """;
        // Fase 1: de lijst laten renderen en direct proberen (dekt verreweg de meeste mails).
        for (var i = 0; i < 8; i++)
        {
            if (await JsAsync(klikRijJs) == "true")
            {
                return "ok";
            }
            await Task.Delay(600, ct);
        }
        // Fase 2: zoekveld openen (of het vergrootglas aanklikken) en de term zetten.
        var zoekJs = JsonSerializer.Serialize(ZoekTerm(van, onderwerp));
        var boxKlaar = "false";
        for (var i = 0; i < 6 && boxKlaar != "true"; i++)
        {
            boxKlaar = await JsAsync(
                $$"""
                (function () {
                    {{KlikHelpers}}
                    const vindBox = () => [...document.querySelectorAll(
                        '#topSearchInput, [role="searchbox"], input[type="search"],' +
                        'input[aria-label*="oeken" i], input[aria-label*="earch" i],' +
                        'input[placeholder*="oeken" i], input[placeholder*="earch" i]')]
                        .find(el => el.offsetParent !== null) || null;
                    let box = vindBox();
                    if (!box) {
                        const zk = [...document.querySelectorAll('button, [role="button"]')]
                            .find(x => x.offsetParent !== null &&
                                /zoeken|search|rechercher/i.test(
                                    (x.getAttribute('aria-label') || '') + ' ' +
                                    (x.getAttribute('title') || '') + ' ' + (x.textContent || '')) &&
                                !/afsluiten|afbreken|exit|filters|quitter/i.test(
                                    (x.getAttribute('aria-label') || '') + ' ' + (x.textContent || '')));
                        if (zk) klik(zk);
                        return false; // volgende ronde pakt het (nu gefocuste) veld
                    }
                    klik(box);
                    box.focus();
                    if (box.tagName === 'INPUT') {
                        Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')
                            .set.call(box, {{zoekJs}});
                        box.dispatchEvent(new Event('input', { bubbles: true }));
                    } else {
                        document.execCommand('selectAll', false, null);
                        document.execCommand('insertText', false, {{zoekJs}});
                    }
                    return true;
                })()
                """);
            if (boxKlaar != "true")
            {
                await Task.Delay(900, ct);
            }
        }
        if (boxKlaar == "true" && _web is not null)
        {
            await Task.Delay(600, ct);
            FysiekeKlik.Toets(_web, FysiekeKlik.VkReturn); // échte Enter: start de zoekopdracht
            // Fase 3: op de resultaten wachten en de rij aanklikken.
            for (var i = 0; i < 24; i++)
            {
                await Task.Delay(500, ct);
                if (await JsAsync(klikRijJs) == "true")
                {
                    return "ok";
                }
            }
        }
        try
        {
            var rijen = await JsAsync(
                """
                JSON.stringify([...document.querySelectorAll('[data-convid], [role="option"]')]
                    .slice(0, 20).map(x => ((x.getAttribute('aria-label') || '') ||
                        x.textContent || '').replace(/\s+/g, ' ').slice(0, 90)))
                """);
            File.WriteAllText(Path.Combine(DataDir, "outlook-zoek-debug.json"),
                $"{{\"van\":{vanJs},\"onderwerp\":{onderwerpJs},\"boxKlaar\":{boxKlaar}," +
                $"\"rijen\":{rijen}}}");
            using var beeld = new MemoryStream();
            await _web!.CoreWebView2!.CapturePreviewAsync(
                CoreWebView2CapturePreviewImageFormat.Png, beeld);
            File.WriteAllBytes(Path.Combine(DataDir, "outlook-screen.png"), beeld.ToArray());
        }
        catch
        {
            // Alleen diagnose.
        }
        return "niet-gevonden";
    }

    /// <summary>
    /// Zoekterm voor het OWA-zoekveld: het onderwerp zonder Re:/FW:-voorvoegsels en zonder
    /// leestekens die de zoekopdracht verstoren (dubbele punten, haakjes), ingekort. Een
    /// letterlijk lang onderwerp met interpunctie geeft in Outlook vaak nul resultaten.
    /// </summary>
    private static string ZoekTerm(string van, string onderwerp)
    {
        var term = System.Text.RegularExpressions.Regex.Replace(
            onderwerp, @"^\s*((re|fw|fwd|tr|aw)\s*:\s*)+", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        term = System.Text.RegularExpressions.Regex
            .Replace(term, @"[^\p{L}\p{N}\s]", " ");
        term = System.Text.RegularExpressions.Regex.Replace(term, @"\s+", " ").Trim();
        if (term.Length > 60)
        {
            var knip = term.LastIndexOf(' ', 60);
            term = term[..(knip > 20 ? knip : 60)];
        }
        return term.Length > 2 ? term : van;
    }

    /// <summary>
    /// Archiveert een mail via zijn directe OWA-link: mail openen, in het (volledige) lint
    /// de Quick Step "verwerkt" — of anders Archiveren — aanklikken, en terug naar Postvak
    /// IN navigeren. Belangrijk: een geslaagde klik is géén garantie dat OWA de mail echt
    /// verplaatst heeft (dat ging eerder stil mis, waarna de mail — intussen als gelezen
    /// gemarkeerd — voorgoed in de inbox bleef staan zonder dat de cockpit het zag).
    /// Daarom wordt na afloop in het Postvak IN gecontroleerd of de mail werkelijk weg is;
    /// zo niet, dan valt de aanroeper terug op de lijst/zoek-route voor een tweede poging.
    /// </summary>
    private async Task<string> ArchiveerViaUrlAsync(
        string url, string van, string onderwerp, CancellationToken ct)
    {
        _web!.CoreWebView2!.Navigate(url);
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(500, ct);
            if (await JsAsync(
                """
                !!document.querySelector('[aria-label*="Berichttekst"],' +
                    '[aria-label*="Message body"], [id^="UniqueMessageBody"], [role="document"]')
                """) == "true")
            {
                break;
            }
        }
        await Task.Delay(1500, ct); // lint laten renderen
        // Fysiek klikken (Win32-muisbericht): de ribbon negeert synthetische JS-kliks —
        // "verwerkt" leek eerder geklikt maar er gebeurde niets.
        const string KnopExpr =
            """
            (function () {
                const b = zoekKnop(document, /\bverwerkt\b/i) ||
                    zoekKnop(document, /\barchiv(eren|e|er)\b/i);
                if (!b || b.getAttribute('aria-disabled') === 'true' || b.disabled) return null;
                return b.matches('button') ? b : (b.querySelector('button') || b);
            })()
            """;
        var geklikt = false;
        for (var p = 0; p < 4 && !geklikt; p++)
        {
            geklikt = await KlikFysiekAsync(KnopExpr);
            if (!geklikt)
            {
                await Task.Delay(1200, ct);
            }
        }
        if (geklikt)
        {
            await Task.Delay(2500, ct); // de verplaatsing laten uitvoeren
        }
        // Altijd terug naar het gewone postvak voor de volgende poll.
        _web.CoreWebView2.Navigate("https://outlook.office.com/mail/");
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500, ct);
            if (await IsIngelogdAsync())
            {
                break;
            }
        }
        await Task.Delay(2000, ct);
        _laatstHerladen = DateTimeOffset.Now;

        // Dubbelcheck: staat de mail nog in het (nu geladen) Postvak IN, dan heeft de klik
        // niets gedaan — dan géén "ok" teruggeven maar de lijst-route laten proberen.
        var nogAanwezig = false;
        if (geklikt)
        {
            var vanJs = JsonSerializer.Serialize(van);
            var ondJs = JsonSerializer.Serialize(
                onderwerp.Equals("ongelezen bericht", StringComparison.OrdinalIgnoreCase)
                    ? "" : onderwerp);
            nogAanwezig = await JsAsync(
                $$"""
                (function () {
                    const norm = s => (s + '').replace(/\s+/g, ' ').toLowerCase();
                    const van = norm({{vanJs}}), ond = norm({{ondJs}});
                    const ondKort = ond.slice(0, 25);
                    return !![...document.querySelectorAll('[data-convid], [role="option"]')]
                        .find(x => {
                            const t = norm((x.getAttribute('aria-label') || '') + ' ' +
                                x.textContent);
                            return t.includes(van) && (!ond || t.includes(ond) ||
                                t.includes(ondKort));
                        });
                })()
                """) == "true";
        }
        var res = !geklikt ? "knop-niet-gevonden" : nogAanwezig ? "niet-verdwenen" : "ok";
        try
        {
            File.WriteAllText(Path.Combine(DataDir, "outlook-archief-debug.json"),
                $"{{\"stap\":\"via-url\",\"geklikt\":{(geklikt ? "true" : "false")}," +
                $"\"nogInInbox\":{(nogAanwezig ? "true" : "false")},\"resultaat\":\"{res}\"}}");
        }
        catch
        {
            // Alleen diagnose.
        }
        return res;
    }

    /// <summary>Sluit de OWA-zoekweergave (best effort) zodat het Postvak IN weer zichtbaar is.</summary>
    private async Task SluitZoekweergaveAsync()
    {
        try
        {
            await JsAsync(
                $$"""
                (function () {
                    {{KlikHelpers}}
                    const exit = zoekKnop(document,
                        /zoeken afsluiten|zoekopdracht (sluiten|afsluiten)|exit search|quitter la recherche/i);
                    if (exit) { klik(exit); return 'knop'; }
                    const box = document.querySelector('#topSearchInput, [role="searchbox"]');
                    if (box) {
                        box.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape',
                            code: 'Escape', keyCode: 27, which: 27, bubbles: true }));
                        return 'escape';
                    }
                    return 'geen';
                })()
                """);
            await Task.Delay(800);
        }
        catch
        {
            // Best effort: de volgende poll herstelt de weergave anders zelf.
        }
    }

    /// <summary>
    /// Zet een eerder gearchiveerde mail terug in Postvak IN: de Archief-map openen, de rij
    /// zoeken en via "Verplaatsen naar" → "Postvak IN" terugverplaatsen; eindigt altijd weer
    /// in Postvak IN. Resultaat: "ok", "rij-niet-gevonden" of "knop-niet-gevonden".
    /// </summary>
    public async Task<string> HerstelUitArchiefAsync(string van, string onderwerp, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            if (!await KlikMapAsync(ArchiefPatroon, ct))
            {
                return "rij-niet-gevonden";
            }
            try
            {
                var vanJs = JsonSerializer.Serialize(van);
                var onderwerpJs = JsonSerializer.Serialize(onderwerp);
                var gevonden = await JsAsync(
                    $$"""
                    (function () {
                        {{KlikHelpers}}
                        const norm = s => (s + '').replace(/\s+/g, ' ').toLowerCase();
                        const van = norm({{vanJs}}), ond = norm({{onderwerpJs}});
                        const ondKort = ond.slice(0, 25);
                        const rows = [...document.querySelectorAll('[data-convid], [role="option"]')];
                        const r = rows.find(x => {
                            const t = norm((x.getAttribute('aria-label') || '') + ' ' + x.textContent);
                            return t.includes(van) && (!ond || t.includes(ond) || t.includes(ondKort));
                        });
                        if (!r) return false;
                        klik(r);
                        return true;
                    })()
                    """);
                if (gevonden != "true")
                {
                    return "rij-niet-gevonden";
                }
                await Task.Delay(1500, ct); // leesvenster laten openen
                var menu = await JsAsync(
                    $$"""
                    (function () {
                        {{KlikHelpers}}
                        const b = zoekKnop(document, /verplaatsen naar|move to|d[eé]placer/i);
                        if (!b) return false;
                        klik(b);
                        return true;
                    })()
                    """);
                if (menu != "true")
                {
                    return "knop-niet-gevonden";
                }
                await Task.Delay(900, ct); // mappenmenu laten openen
                var item = await JsAsync(
                    $$"""
                    (function () {
                        {{KlikHelpers}}
                        const pat = new RegExp({{JsonSerializer.Serialize(InboxPatroon)}}, 'i');
                        const i = zoekKnop(document, pat);
                        if (!i) {
                            // Diagnose: welke items staan er wél in het menu?
                            return JSON.stringify([...document.querySelectorAll('[role="menuitem"]')]
                                .map(x => (x.textContent || '').replace(/\s+/g, ' ').trim())
                                .filter(t => t).slice(0, 30));
                        }
                        klik(i);
                        return 'true';
                    })()
                    """);
                if (!item.Contains("true"))
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(DataDir, "outlook-herstel-debug.json"), item);
                    }
                    catch
                    {
                        // Alleen diagnose.
                    }
                    return "knop-niet-gevonden";
                }
                await Task.Delay(1200, ct); // verplaatsing laten verwerken
                return "ok";
            }
            finally
            {
                await KlikMapAsync(InboxPatroon, CancellationToken.None);
            }
        }
        finally
        {
            _slot.Release();
        }
    }

    // Maandnamen (nl/en/fr) voor het kalendertje in de OWA-sluimerdialoog.
    private static readonly string[][] MaandNamen =
    {
        new[] { "januari", "january", "janvier" },
        new[] { "februari", "february", "février" },
        new[] { "maart", "march", "mars" },
        new[] { "april", "april", "avril" },
        new[] { "mei", "may", "mai" },
        new[] { "juni", "june", "juin" },
        new[] { "juli", "july", "juillet" },
        new[] { "augustus", "august", "août" },
        new[] { "september", "september", "septembre" },
        new[] { "oktober", "october", "octobre" },
        new[] { "november", "november", "novembre" },
        new[] { "december", "december", "décembre" },
    };

    /// <summary>
    /// Snoozet een mail met Outlooks éigen sluimerfunctie: de rij selecteren, werkbalk
    /// "Sluimeren" → "Een datum kiezen", in het kalendertje naar de juiste maand bladeren,
    /// de dag aanklikken en opslaan. De mail verdwijnt dan echt uit Postvak IN en komt op
    /// die datum vanzelf terug (ook op de telefoon). Het tijdstip wordt best effort gezet;
    /// lukt dat niet, dan geldt het standaardtijdstip van Outlook.
    /// Resultaat: "ok" of de stap die misliep ("rij-niet-gevonden", "knop-niet-gevonden",
    /// "menu-niet-gevonden", "datum-niet-gevonden", "opslaan-niet-gevonden").
    /// </summary>
    public async Task<string> SnoozeAsync(
        string van, string onderwerp, DateTimeOffset tot, CancellationToken ct, string url = "")
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            // De mail eerst betrouwbaar openen. Voorheen deed snooze dat via een eigen (broze)
            // rij-klik of een directe link; kwam de mail dan niet echt open ("Geen items
            // geselecteerd"), dan faalde de rest. Nu dezelfde bewezen opener als archiveren:
            // een directe link als fast-path, anders lijst → zoeken → echte Enter.
            var mailOpen = false;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                _web!.CoreWebView2!.Navigate(url);
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(500, ct);
                    if (await JsAsync(
                        """
                        !!document.querySelector('[aria-label*="Berichttekst"],' +
                            '[aria-label*="Message body"], [id^="UniqueMessageBody"], [role="document"]')
                        """) == "true")
                    {
                        mailOpen = true;
                        break;
                    }
                }
            }
            if (!mailOpen)
            {
                // Geen (werkende) link: de mail via lijst/zoeken openen zoals archiveren doet.
                if (await VindEnOpenRijAsync(van, onderwerp, ct) != "ok")
                {
                    await SluitZoekweergaveAsync();
                    return "rij-niet-gevonden";
                }
            }
            await Task.Delay(1500, ct); // leesvenster + lint laten renderen
            // De ribbon negeert JS-kliks: "Uitstellen/Sluimeren" fysiek aanklikken zodat het
            // sluimermenu echt opent; de job hieronder gaat dan verder met de kalender.
            await KlikFysiekAsync(
                """
                (function () {
                    const b = zoekKnop(document, /sluimeren|snooze|uitstellen|répéter|reporter/i);
                    if (!b || b.getAttribute('aria-disabled') === 'true' || b.disabled) return null;
                    return b.matches('button') ? b : (b.querySelector('button') || b);
                })()
                """);
            await Task.Delay(900, ct);
            var maandenJs = JsonSerializer.Serialize(MaandNamen[tot.Month - 1]);
            var tijdJs = JsonSerializer.Serialize(tot.ToString("HH:mm"));

            // Vanaf hier klikken we FYSIEK. OWA negeert synthetische kliks niet alleen op de
            // ribbon maar ook op de sluimer-flyout en de datumkiezer (precies waardoor snooze
            // strandde: de flyout bleef open staan). Elk element lokaliseren we met JS en
            // klikken we via KlikFysiekAsync met een echte muisklik.
            const string CustomExpr =
                """
                (function () {
                    const pat = /datum kiezen|kies een datum|aangepast|andere datum|andere tijd|choose a date|custom time|choose a custom|pick a date|choisir une date|personnalis|autre date/i;
                    const rollen = 'button, [role="button"], [role="menuitem"], [role="menuitemradio"], [role="menuitemcheckbox"], [role="option"]';
                    return [...document.querySelectorAll(rollen)].find(x =>
                        pat.test(((x.getAttribute('aria-label') || '') + ' ' +
                            (x.getAttribute('title') || '') + ' ' + (x.textContent || '')).trim())) || null;
                })()
                """;
            const string SluimerExpr =
                """
                (function () {
                    const b = zoekKnop(document, /sluimeren|snooze|uitstellen|répéter|reporter/i);
                    if (!b || b.getAttribute('aria-disabled') === 'true' || b.disabled) return null;
                    return b.matches('button') ? b : (b.querySelector('button') || b);
                })()
                """;
            // Flyout open (bevat de custom-optie)? Zo niet: de sluimerknop nog eens fysiek klikken.
            if (await JsAsync($$"""(function () { {{KlikHelpers}} return !!({{CustomExpr}}); })()""") != "true")
            {
                await KlikFysiekAsync(SluimerExpr);
                await Task.Delay(900, ct);
            }
            var stand = "{\"stap\":\"ok\"}";
            if (!await KlikFysiekAsync(CustomExpr))
            {
                stand = await SnoozeDiagnoseAsync("menu-niet-gevonden");
            }
            else
            {
                await Task.Delay(1200, ct); // de datumkiezer laten openen
                // Kalender: naar de juiste maand bladeren en de dag fysiek aanklikken.
                var dagExpr =
                    $$"""
                    (function () {
                        const dag = {{tot.Day}}, jaar = {{tot.Year}};
                        const maanden = {{maandenJs}};
                        const dagPat = new RegExp('\\b' + dag + '\\b');
                        return [...document.querySelectorAll(
                            '[role="gridcell"][aria-label], [role="gridcell"] button, td[aria-label], button[aria-label]')]
                            .find(x => {
                                const l = ((x.getAttribute('aria-label') ||
                                    x.closest('[aria-label]')?.getAttribute('aria-label')) || '').toLowerCase();
                                const jaarOk = !/\b(19|20)\d\d\b/.test(l) || l.includes(String(jaar));
                                return jaarOk && maanden.some(m => l.includes(m)) && dagPat.test(l);
                            }) || null;
                    })()
                    """;
                const string VolgendeMaandExpr =
                    "(function () { return zoekKnop(document, /volgende maand|next month|mois suivant/i) || null; })()";
                var dagGeklikt = false;
                for (var m = 0; m < 13; m++)
                {
                    if (await KlikFysiekAsync(dagExpr))
                    {
                        dagGeklikt = true;
                        break;
                    }
                    if (!await KlikFysiekAsync(VolgendeMaandExpr))
                    {
                        break;
                    }
                    await Task.Delay(500, ct);
                }
                if (!dagGeklikt)
                {
                    stand = await SnoozeDiagnoseAsync("datum-niet-gevonden");
                }
                else
                {
                    await Task.Delay(600, ct);
                    // Tijdstip (best effort): het uurveld via JS invullen als het er staat.
                    await JsAsync(
                        $$"""
                        (function () {
                            try {
                                const tijd = [...document.querySelectorAll('input')]
                                    .find(x => /\d{1,2}:\d{2}/.test(x.value || ''));
                                if (tijd) {
                                    Object.getOwnPropertyDescriptor(
                                        window.HTMLInputElement.prototype, 'value').set.call(tijd, {{tijdJs}});
                                    tijd.dispatchEvent(new Event('input', { bubbles: true }));
                                }
                            } catch { }
                            return true;
                        })()
                        """);
                    await Task.Delay(300, ct);
                    const string OpslaanExpr =
                        """
                        (function () {
                            return [...document.querySelectorAll('button, [role="button"]')]
                                .find(x => /^(opslaan|save|enregistrer|bewaren)$/i
                                    .test((x.textContent || '').trim())) || null;
                        })()
                        """;
                    if (!await KlikFysiekAsync(OpslaanExpr))
                    {
                        stand = await SnoozeDiagnoseAsync("opslaan-niet-gevonden");
                    }
                    else
                    {
                        await Task.Delay(1000, ct);
                    }
                }
            }
            // Mislukt? Dan een screenshot van de (nog open) picker maken vóór we terugnavigeren.
            if (!stand.Replace("\\\"", "\"").Contains("\"stap\":\"ok\""))
            {
                try
                {
                    using var beeld = new MemoryStream();
                    await _web!.CoreWebView2!.CapturePreviewAsync(
                        CoreWebView2CapturePreviewImageFormat.Png, beeld);
                    File.WriteAllBytes(
                        Path.Combine(DataDir, "outlook-snooze-screen.png"), beeld.ToArray());
                }
                catch
                {
                    // Alleen diagnose.
                }
            }
            await Task.Delay(800, ct); // de verplaatsing nog laten verwerken
            {
                // Altijd terug naar het gewone postvak voor de volgende poll (we kunnen in de
                // zoekweergave of op een deeplink zijn beland).
                _web!.CoreWebView2!.Navigate("https://outlook.office.com/mail/");
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(500, ct);
                    if (await IsIngelogdAsync())
                    {
                        break;
                    }
                }
                await Task.Delay(2000, ct);
                _laatstHerladen = DateTimeOffset.Now;
            }
            var genormaliseerd = stand.Replace("\\\"", "\"");
            if (!genormaliseerd.Contains("\"stap\":\"ok\""))
            {
                try
                {
                    File.WriteAllText(Path.Combine(DataDir, "outlook-snooze-debug.json"), stand);
                }
                catch
                {
                    // Alleen diagnose.
                }
                foreach (var code in new[] { "rij-niet-gevonden", "knop-niet-gevonden",
                    "menu-niet-gevonden", "datum-niet-gevonden", "opslaan-niet-gevonden" })
                {
                    if (genormaliseerd.Contains(code))
                    {
                        return code;
                    }
                }
                return "knop-niet-gevonden";
            }
            return "ok";
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Bouwt een JSON-string met de faalstap en een rijke momentopname van het scherm
    /// (dialogen, invoervelden/comboboxes, kalendercellen, menu-opties, knoppen) voor
    /// outlook-snooze-debug.json — zodat een mislukte sluimerstap gericht te fixen is.
    /// </summary>
    private async Task<string> SnoozeDiagnoseAsync(string stap)
    {
        var context = "null";
        try
        {
            var ruw = await JsAsync(
                """
                JSON.stringify({
                    dialogen: [...document.querySelectorAll('[role="dialog"]')]
                        .map(x => (x.getAttribute('aria-label') || '').slice(0, 60)).filter(t => t).slice(0, 5),
                    inputs: [...document.querySelectorAll('input, [role="combobox"], [role="spinbutton"]')]
                        .map(x => ((x.getAttribute('aria-label') || x.getAttribute('placeholder') || '') + '=' +
                            (x.value || x.textContent || '')).replace(/\s+/g, ' ').trim().slice(0, 60))
                        .filter(t => t.length > 1).slice(0, 15),
                    gridcellen: [...document.querySelectorAll('[role="gridcell"]')]
                        .map(x => (x.getAttribute('aria-label') || x.textContent || '')
                            .replace(/\s+/g, ' ').trim().slice(0, 40)).filter(t => t).slice(0, 12),
                    opties: [...document.querySelectorAll('[role="menuitem"], [role="menuitemradio"], [role="option"]')]
                        .map(x => (x.getAttribute('aria-label') || x.textContent || '')
                            .replace(/\s+/g, ' ').trim()).filter(t => t).slice(0, 25),
                    knoppen: [...new Set([...document.querySelectorAll('button, [role="button"]')]
                        .map(x => (x.getAttribute('aria-label') || x.textContent || '')
                            .replace(/\s+/g, ' ').trim()).filter(t => t.length > 1 && t.length < 70))].slice(0, 45)
                })
                """);
            // ExecuteScriptAsync levert een JSON-gecodeerde (dubbel-escaped) string; één keer decoderen.
            context = JsonSerializer.Deserialize<string>(ruw) ?? "null";
        }
        catch
        {
            // Alleen diagnose; val terug op enkel de stap.
        }
        return $"{{\"stap\":\"{stap}\",\"context\":{context}}}";
    }

    /// <summary>
    /// Toont het (anders verborgen) Outlook-venster met de Archief-map open, zodat je zelf
    /// door het archief kunt bladeren of iets kunt terugslepen. Het venster sluiten verbergt
    /// het alleen en navigeert automatisch terug naar Postvak IN.
    /// </summary>
    public async Task ToonArchiefAsync(CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            await KlikMapAsync(ArchiefPatroon, ct);
            LogVensterOnScreen("ToonArchiefAsync (🗂 Archief-knop)");
            var scherm = Screen.FromPoint(Cursor.Position).WorkingArea;
            _venster!.Text = "Outlook (CED) — Archief · sluiten = venster weer verbergen";
            _venster.Location = new Point(
                scherm.X + (scherm.Width - _venster.Width) / 2,
                scherm.Y + (scherm.Height - _venster.Height) / 2);
            _venster.TopMost = true;
            _venster.BringToFront();
            _venster.Activate();
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Opent één mail in het leesvenster en leest de volledige inhoud: platte tekst én HTML,
    /// met afbeeldingen ingebed als data-URL's (opgehaald binnen de ingelogde sessie — buiten
    /// Outlook laden die URL's niet). Let op: hierdoor markeert Outlook de mail als gelezen.
    /// </summary>
    public async Task<(string Tekst, string Html, DateTimeOffset? Datum, string Url,
            string Aan, string Cc)> LeesMailAsync(
        string van, string onderwerp, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException(
                    "Outlook is niet aangemeld — klik op 'Outlook aanmelden…' (dagelijkse MFA).");
            }
            // De rij vinden en aanklikken: direct in de lijst of via het zoekveld (met een
            // échte Enter — synthetische toetsen negeert OWA net als synthetische kliks).
            if (await VindEnOpenRijAsync(van, onderwerp, ct) != "ok")
            {
                try
                {
                    File.AppendAllText(Path.Combine(DataDir, "outlook-lees-debug.txt"),
                        $"{DateTime.Now:HH:mm:ss} {van} | {onderwerp}: rij niet gevonden " +
                        "(details in outlook-zoek-debug.json)\r\n");
                }
                catch
                {
                    // Alleen diagnose.
                }
                await SluitZoekweergaveAsync();
                return ("", "", null, "", "", "");
            }
            await Task.Delay(2500, ct); // leesvenster laten laden
            var gelezen = await LeesGeopendeMailKernAsync(ct);
            await SluitZoekweergaveAsync(); // was de mail via zoeken gevonden
            return gelezen;
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Leest de inhoud van de nu geopende mail (leesvenster): platte tekst, HTML met
    /// ingebedde afbeeldingen, het exacte tijdstip uit de kop, de directe link en de
    /// Aan/Cc-regels (als "Naam; Naam"-strings, zonder het label).
    /// Aanroeper houdt zelf het slot vast en heeft de mail al geopend.
    /// </summary>
    private async Task<(string Tekst, string Html, DateTimeOffset? Datum, string Url,
            string Aan, string Cc)>
        LeesGeopendeMailKernAsync(CancellationToken ct)
    {
        {
            // Asynchrone verzamel-job in de pagina (afbeeldingen fetchen kan even duren);
            // het resultaat komt in window.__wmMail en wordt hieronder gepolld.
            await JsAsync(
                """
                (function () {
                    window.__wmMail = null;
                    (async () => {
                        const body = document.querySelector(
                            '[aria-label*="Berichttekst"], [aria-label*="Message body"],' +
                            '[id^="UniqueMessageBody"], [role="document"]');
                        if (!body) { window.__wmMail = { tekst: '', html: '' }; return; }
                        const kloon = body.cloneNode(true);
                        // De gele "Externe Mail"-banner van het CED-tenant verbergen: puur
                        // ruis in elke externe mail.
                        for (const el of [...kloon.querySelectorAll('div, table, tr, td, p')]) {
                            const t = el.textContent || '';
                            if (t.length < 700 && /externe mail/i.test(t) &&
                                /ext[ée]rieur|support/i.test(t)) {
                                el.remove();
                            }
                        }
                        const origineel = [...body.querySelectorAll('img')];
                        const kopie = [...kloon.querySelectorAll('img')];
                        for (let i = 0; i < kopie.length; i++) {
                            try {
                                const src = origineel[i]?.src || kopie[i].src || '';
                                if (!src || src.startsWith('data:')) continue;
                                const resp = await fetch(src, { credentials: 'include' });
                                const blob = await resp.blob();
                                if (blob.size > 1_500_000) { kopie[i].remove(); continue; }
                                kopie[i].src = await new Promise(res => {
                                    const fr = new FileReader();
                                    fr.onload = () => res(fr.result);
                                    fr.readAsDataURL(blob);
                                });
                            } catch { /* afbeelding niet op te halen: origineel laten staan */ }
                        }
                        // Directe link naar deze mail: OWA zet bij het openen de conversatie-id
                        // in het adres; anders zelf bouwen uit de geselecteerde rij.
                        const geselecteerd = document.querySelector(
                            '[data-convid][aria-selected="true"], [data-convid].is-selected');
                        const url = location.href.includes('/id/')
                            ? location.href
                            : (geselecteerd
                                ? 'https://outlook.office.com/mail/inbox/id/' +
                                    encodeURIComponent(geselecteerd.getAttribute('data-convid'))
                                : '');
                        // Bestemmelingen (Aan/Cc) uit de kop boven de berichttekst: het
                        // kortste element dat met het label begint is de echte adresregel
                        // (grotere containers bevatten de hele kop en vallen zo af).
                        const main = body.closest('[role="main"]');
                        const vindAdresregel = (labels) => {
                            let beste = '';
                            if (!main) return beste;
                            for (const el of main.querySelectorAll('div, span')) {
                                if (body.contains(el)) continue; // niet in de berichttekst zoeken
                                const t = (el.innerText || '').replace(/\s+/g, ' ').trim();
                                if (t.length > 3 && t.length < 600 &&
                                    labels.some(l => t.toLowerCase().startsWith(l)) &&
                                    (!beste || t.length < beste.length)) {
                                    beste = t;
                                }
                            }
                            return beste;
                        };
                        window.__wmMail = {
                            tekst: (body.innerText || '').trim().slice(0, 12000),
                            html: kloon.innerHTML.slice(0, 150000),
                            kop: (main?.innerText || '').slice(0, 2500),
                            aan: vindAdresregel(['aan:', 'to:', 'à :', 'à:']),
                            cc: vindAdresregel(['cc:', 'kopie:', 'copie :', 'copie:']),
                            url,
                        };
                    })();
                    return true;
                })()
                """);
            for (var i = 0; i < 30; i++) // max. ~9 s op de afbeeldingen wachten
            {
                await Task.Delay(300, ct);
                var klaar = await JsAsync("JSON.stringify(window.__wmMail)");
                if (klaar is not ("null" or "\"null\""))
                {
                    using var doc = JsonDocument.Parse(
                        JsonSerializer.Deserialize<string>(klaar) is { } s ? s : klaar);
                    var kop = doc.RootElement.TryGetProperty("kop", out var k)
                        ? k.GetString() ?? "" : "";
                    var tekst = doc.RootElement.GetProperty("tekst").GetString() ?? "";
                    // De "Externe Mail"-waarschuwing ook uit de platte tekst halen.
                    tekst = System.Text.RegularExpressions.Regex.Replace(tekst,
                        @"Externe Mail / Mail de l[’']ext[ée]rieur:[\s\S]{0,500}?support informatique\.?",
                        "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    // Labels ("Aan:", "To:", "Cc:", …) van de adresregels strippen.
                    static string StripLabel(string regel) =>
                        System.Text.RegularExpressions.Regex.Replace(regel,
                            @"^\s*(aan|to|à|cc|kopie|copie)\s*:\s*", "",
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
                    return (
                        tekst,
                        doc.RootElement.GetProperty("html").GetString() ?? "",
                        ParseVolledigMoment(kop),
                        doc.RootElement.TryGetProperty("url", out var u)
                            ? u.GetString() ?? "" : "",
                        doc.RootElement.TryGetProperty("aan", out var a)
                            ? StripLabel(a.GetString() ?? "") : "",
                        doc.RootElement.TryGetProperty("cc", out var c)
                            ? StripLabel(c.GetString() ?? "") : "");
                }
            }
            return ("", "", null, "", "", "");
        }
    }

    public sealed record OutlookMailVol(
        string Sleutel, string Van, string Onderwerp, string Tekst, DateTimeOffset Datum,
        string Html = "", int Pogingen = 0, string Url = "",
        string Aan = "", string Cc = ""); // "Naam; Naam"-regels uit de mailkop

    private static readonly string MailStoreFile = Path.Combine(DataDir, "outlook-mails.json");

    /// <summary>
    /// Alle zichtbare inboxmails, met de volledige tekst uit een lokale cache. Elke mail die
    /// nog niet in de cache zit wordt meteen geopend en volledig opgehaald (Outlook markeert
    /// hem daardoor als gelezen); daarna komt de inhoud altijd uit de cache. Verdwijnt een
    /// mail uit de inbox (elders gearchiveerd), dan verdwijnt hij ook hier; de tekstcache
    /// verloopt na 7 dagen.
    /// </summary>
    public async Task<List<OutlookMailVol>> VolledigeMailsAsync(CancellationToken ct)
    {
        var store = LaadMails();
        var inbox = await InboxAsync(ct);
        var gewijzigd = false;
        var resultaat = new List<OutlookMailVol>();
        // Elke onbekende mail moet in OWA geopend worden om zijn tekst te krijgen, en dat
        // duurt seconden. Na een weekend staan er zo twintig klaar en blijft de hele
        // ophaalbeurt minutenlang hangen. Vandaar een budget per beurt: de rest komt met
        // zijn preview in de lijst en wordt de volgende ronde alsnog uitgelezen (de
        // retry-tak hieronder pikt entries zonder HTML vanzelf op).
        var leesBudget = MaxNieuweMailsPerBeurt;
        // Eén keer wegschrijven aan het eind, ook als het ophalen halverwege afbreekt: de
        // store per mail opslaan kostte bij een volle inbox tientallen megabytes schrijfwerk.
        try
        {
            foreach (var b in inbox)
            {
                // Tijdstip hoort bij de sleutel: een nieuwe mail in dezelfde thread (zelfde
                // afzender + onderwerp) moet als nieuw gelden en opnieuw opgehaald worden —
                // anders blijft de inhoud van de vorige mail uit de cache komen.
                var sleutel = $"owa:{b.Van}|{b.Onderwerp}" +
                    (b.Datum is { } lijstDatum ? $"|{lijstDatum:yyyyMMddHHmm}" : "");
                var moment = b.Datum ?? DateTimeOffset.Now;
                var bekend = store.FirstOrDefault(m => m.Sleutel == sleutel);
                if (bekend is null)
                {
                    // Zelfde mail waarvan alleen de dág in de sleutel verschoof (de lijstlabels
                    // wisselen van vorm): de eerder opgehaalde inhoud hergebruiken. De tijd
                    // (HH:mm) identificeert de mail binnen een thread betrouwbaar genoeg.
                    var tijdDeel = b.Datum is { } td ? td.ToString("HHmm") : "";
                    if (tijdDeel.Length > 0 && store.FirstOrDefault(m =>
                            m.Van == b.Van && m.Onderwerp == b.Onderwerp && m.Html.Length > 0 &&
                            m.Sleutel.EndsWith(tijdDeel, StringComparison.Ordinal)) is { } eerder)
                    {
                        bekend = eerder with { Sleutel = sleutel, Datum = moment };
                        store.Add(bekend);
                        gewijzigd = true;
                    }
                }
                if (bekend is null)
                {
                    var tekst = "";
                    var html = "";
                    var url = "";
                    var aan = "";
                    var cc = "";
                    DateTimeOffset? exact = null;
                    if (leesBudget > 0)
                    {
                        leesBudget--;
                        try
                        {
                            (tekst, html, exact, url, aan, cc) =
                                await LeesMailAsync(b.Van, b.Onderwerp, ct);
                        }
                        catch
                        {
                            // Best effort: dan alleen de (eventuele) preview cachen.
                        }
                    }
                    bekend = new OutlookMailVol(sleutel, b.Van, b.Onderwerp,
                        tekst.Length > 0 ? tekst : b.Preview, exact ?? moment, html,
                        Pogingen: tekst.Length > 0 || html.Length > 0 ? 0 : 1, Url: url,
                        Aan: aan, Cc: cc);
                    store.Add(bekend);
                    gewijzigd = true;
                }
                else if (bekend.Html.Length == 0 && bekend.Pogingen < 3)
                {
                    // Eerder (deels) mislukt — bv. omdat de rij toen buiten de gevirtualiseerde
                    // lijst viel: nog eens proberen, met een teller zodat het na 3 keer stopt.
                    try
                    {
                        var (tekst2, html2, exact2, url2, aan2, cc2) =
                            await LeesMailAsync(b.Van, b.Onderwerp, ct);
                        bekend = bekend with
                        {
                            Tekst = tekst2.Length > 0 ? tekst2 : bekend.Tekst,
                            Html = html2,
                            Datum = exact2 ?? bekend.Datum,
                            Pogingen = tekst2.Length > 0 || html2.Length > 0 ? 0 : bekend.Pogingen + 1,
                            Url = url2.Length > 0 ? url2 : bekend.Url,
                            Aan = aan2.Length > 0 ? aan2 : bekend.Aan,
                            Cc = cc2.Length > 0 ? cc2 : bekend.Cc,
                        };
                    }
                    catch
                    {
                        bekend = bekend with { Pogingen = bekend.Pogingen + 1 };
                    }
                    store[store.FindIndex(m => m.Sleutel == sleutel)] = bekend;
                    gewijzigd = true;
                }
                if (b.Datum is { } lijstmoment &&
                    DateOnly.FromDateTime(bekend.Datum.LocalDateTime) !=
                    DateOnly.FromDateTime(lijstmoment.LocalDateTime))
                {
                    // Alleen corrigeren als de dág afwijkt: het exacte tijdstip uit de mailkop
                    // is preciezer dan de lijst (die toont voor oudere mails alleen een datum).
                    store[store.IndexOf(bekend)] = bekend = bekend with { Datum = lijstmoment };
                    gewijzigd = true;
                }
                resultaat.Add(bekend ?? new OutlookMailVol(sleutel, b.Van, b.Onderwerp, b.Preview, moment));
            }
            // Tekstcache opschonen: entries die niet meer in de inbox staan én oud zijn.
            var inboxSleutels = resultaat.Select(r => r.Sleutel).ToHashSet();
            var grens = DateTimeOffset.Now.AddDays(-7);
            if (store.RemoveAll(m => !inboxSleutels.Contains(m.Sleutel) && m.Datum < grens) > 0)
            {
                gewijzigd = true;
            }
        }
        finally
        {
            if (gewijzigd)
            {
                BewaarMails(store);
            }
        }
        return resultaat;
    }

    /// <summary>
    /// Grens op de bewaarde HTML per mail. Een mailtje met een paar ingesloten afbeeldingen
    /// haalde ongestraft 900 kB; met honderd van die dingen werd de cache 18 MB, en die werd
    /// bij élke nieuwe mail opnieuw ingelezen én weggeschreven. Voor de leesweergave is dit
    /// ruim voldoende — is de mail langer, dan open je hem toch in Outlook zelf.
    /// </summary>
    private const int MaxHtmlPerMail = 150_000;

    /// <summary>Zoveel mails houden we bij; ouder dan dat is de inbox toch al opgeschoond.</summary>
    private const int MaxMailsInStore = 150;

    /// <summary>Zoveel onbekende mails worden er per ophaalbeurt in OWA geopend en uitgelezen.</summary>
    private const int MaxNieuweMailsPerBeurt = 6;

    /// <summary>
    /// De store blijft in het geheugen staan: hij wordt binnen één ophaalbeurt tientallen
    /// keren geraadpleegd, en van schijf lezen is met dit formaat niet gratis.
    /// </summary>
    private static List<OutlookMailVol>? _mailCache;

    private static List<OutlookMailVol> LaadMails()
    {
        if (_mailCache is not null)
        {
            return _mailCache;
        }
        try
        {
            if (File.Exists(MailStoreFile) &&
                JsonSerializer.Deserialize<List<OutlookMailVol>>(
                    File.ReadAllText(MailStoreFile)) is { } mails)
            {
                // Vóór het snoeien meten: Snoei kapt de HTML in dezelfde lijst af, dus
                // achteraf vergelijken zou altijd "niets veranderd" opleveren.
                var voorAantal = mails.Count;
                var voorTekens = mails.Sum(m => (long)m.Html.Length);
                var gesnoeid = Snoei(mails);
                _mailCache = gesnoeid;
                // Een bestaand te groot bestand meteen terugbrengen: anders blijft die 18 MB
                // staan tot er toevallig een mail bijkomt.
                if (gesnoeid.Count != voorAantal ||
                    gesnoeid.Sum(m => (long)m.Html.Length) != voorTekens)
                {
                    BewaarMails(gesnoeid);
                }
                return gesnoeid;
            }
        }
        catch
        {
            // Onleesbaar: opnieuw beginnen (nieuwe mails worden gewoon opnieuw opgehaald).
        }
        return _mailCache = new List<OutlookMailVol>();
    }

    /// <summary>Te lange HTML afkappen en oude entries weggooien, zodat het bestand klein blijft.</summary>
    private static List<OutlookMailVol> Snoei(List<OutlookMailVol> mails)
    {
        for (var i = 0; i < mails.Count; i++)
        {
            if (mails[i].Html.Length > MaxHtmlPerMail)
            {
                mails[i] = mails[i] with { Html = mails[i].Html[..MaxHtmlPerMail] };
            }
        }
        if (mails.Count > MaxMailsInStore)
        {
            mails = mails.OrderByDescending(m => m.Datum).Take(MaxMailsInStore).ToList();
        }
        return mails;
    }

    private static void BewaarMails(List<OutlookMailVol> mails)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            _mailCache = Snoei(mails);
            File.WriteAllText(MailStoreFile, JsonSerializer.Serialize(_mailCache));
        }
        catch
        {
            // Cache is best effort.
        }
    }

    /// <summary>
    /// Afspraken van één dag uit de CED-webagenda. Zie <see cref="AgendaDagenAsync"/>: die haalt
    /// in dezelfde beweging de omliggende dagen op, want de webagenda toont doorgaans een hele
    /// week tegelijk.
    /// </summary>
    public async Task<List<AgendaClient.AgendaItem>> AgendaAsync(DateOnly dag, CancellationToken ct) =>
        (await AgendaDagenAsync(dag, ct)).GetValueOrDefault(dag) ?? new List<AgendaClient.AgendaItem>();

    /// <summary>
    /// Leest de CED-webagenda rond een dag uit: navigeert in het agenda-tabblad naar de week
    /// van die dag en leest de aria-labels van de afspraken. De hoofdpagina blijft intussen
    /// gewoon op Postvak IN staan.
    ///
    /// Elke afspraak wordt op de datum uit haar eigen label gezet, niet op de gevraagde dag.
    /// Zo levert één navigatie de hele week op en kan de beller die dagen cachen; bladeren naar
    /// een andere dag van dezelfde week hoeft dan niets meer op te halen.
    /// </summary>
    public async Task<Dictionary<DateOnly, List<AgendaClient.AgendaItem>>> AgendaDagenAsync(
        DateOnly dag, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException("Outlook is niet aangemeld.");
            }
            _jsDoel = await AgendaWebAsync(ct);
            // Bewust de weekweergave en niet de dagweergave: één navigatie levert dan meteen de
            // hele week aan afspraken, elk met haar eigen datum in het aria-label. Dat scheelt
            // zes trage navigaties bij het bladeren.
            var json = await NaarAgendaAsync(dag, ct);
            var waar = await WaarStaanWeAsync();

            try
            {
                // Diagnose: de ruwe labels bewaren om spookafspraken te kunnen herleiden.
                File.WriteAllText(Path.Combine(DataDir, "outlook-agenda-debug.json"),
                    $"{{\"gevraagd\":\"{dag:yyyy-MM-dd}\",\"pagina\":{JsonSerializer.Serialize(waar)}," +
                    $"\"stappen\":{JsonSerializer.Serialize(_agendaStappen)},\"labels\":{json}}}");
            }
            catch
            {
                // Alleen diagnose.
            }

            var perDag = new Dictionary<DateOnly, List<AgendaClient.AgendaItem>>();
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var label = el.GetString() ?? "";
                // UI-elementen die géén afspraak zijn overslaan: de "klik om een afspraak te
                // maken"-cel op het eerstvolgende vrije halfuur, vrij/bezet-indicatoren enz.
                if (System.Text.RegularExpressions.Regex.IsMatch(label,
                    @"nieuwe afspraak|new event|toevoegen|te maken|create|klik|click|dubbelklik|" +
                    @"double.?click|selecteer|select |beschikbaar|available|geen gebeurtenis|no event|" +
                    @"werktijden|working hours|heures de travail|" +
                    @"werklocatie|work location|werkplek|lieu de travail",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    continue;
                }
                // Zonder datum in het label: aannemen dat het om de gevraagde dag gaat.
                var datum = DatumUitLabel(label, dag) ?? dag;
                var tijden = System.Text.RegularExpressions.Regex.Matches(label, @"\b(\d{1,2}):(\d{2})\b");
                if (tijden.Count < 2)
                {
                    continue;
                }
                var start = new DateTimeOffset(datum.Year, datum.Month, datum.Day,
                    int.Parse(tijden[0].Groups[1].Value), int.Parse(tijden[0].Groups[2].Value), 0,
                    DateTimeOffset.Now.Offset);
                var einde = new DateTimeOffset(datum.Year, datum.Month, datum.Day,
                    int.Parse(tijden[1].Groups[1].Value), int.Parse(tijden[1].Groups[2].Value), 0,
                    DateTimeOffset.Now.Offset);
                var titel = System.Text.RegularExpressions.Regex
                    .Replace(label, @"\b(van|tot|from|to)?\s*\d{1,2}:\d{2}\b", "")
                    .Trim(' ', ',', '.', '-');
                titel = SchoonAgendaTitel(titel);
                if (titel.Length > 70)
                {
                    titel = titel[..70] + "…";
                }
                // Blijft er na het weghalen van dag-, maand- en tijdwoorden vrijwel niets
                // over, dan was het label alleen een tijdslot (geen echte afspraak).
                var kaal = System.Text.RegularExpressions.Regex.Replace(titel.ToLowerInvariant(),
                    @"\b(maandag|dinsdag|woensdag|donderdag|vrijdag|zaterdag|zondag|" +
                    @"monday|tuesday|wednesday|thursday|friday|saturday|sunday|" +
                    $@"uur|hour|heures?|{string.Join('|', Maanden.Distinct())})\b|[^\p{{L}}]", "");
                if (kaal.Length < 3)
                {
                    continue;
                }
                if (titel.Length > 0 && einde > start)
                {
                    if (!perDag.TryGetValue(datum, out var lijst))
                    {
                        perDag[datum] = lijst = new List<AgendaClient.AgendaItem>();
                    }
                    lijst.Add(new AgendaClient.AgendaItem(start, einde, false, titel));
                }
            }

            // Dezelfde afspraak komt in de DOM vaak meermaals voor met licht afwijkende
            // labels: per dag ontdubbelen op tijdslot (kortste titel is doorgaans de schoonste).
            foreach (var datum in perDag.Keys.ToList())
            {
                perDag[datum] = perDag[datum]
                    .GroupBy(i => (i.Start, i.Einde))
                    .Select(g => g.OrderBy(i => i.Titel.Length).First())
                    .OrderBy(i => i.Start)
                    .ToList();
            }
            // De gevraagde dag altijd terugmelden, ook als er niets op staat: zo weet de beller
            // dat die dag effectief gelezen is en hoeft hij er niet opnieuw voor te navigeren.
            perDag.TryAdd(dag, new List<AgendaClient.AgendaItem>());
            return perDag;
        }
        finally
        {
            _jsDoel = null; // terug naar de hoofdpagina voor de mailfuncties
            _slot.Release();
        }
    }

    /// <summary>
    /// Houdt van een agenda-label alleen de eigenlijke titel over: OWA plakt achter de naam
    /// nog dag, datum, jaar en locatie ("IT-meeting, , Dinsdag, 4 Augustus, 2026, Microsoft
    /// Teams-vergadering"). Alles vanaf het eerste ruis-deel (leeg, weekdag, datum, jaartal
    /// of Teams-locatie) valt weg.
    /// </summary>
    internal static string SchoonAgendaTitel(string titel)
    {
        var schoon = new List<string>();
        foreach (var deelRuw in titel.Split(','))
        {
            var deel = deelRuw.Trim();
            if (deel.Length == 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(deel,
                    @"^(maandag|dinsdag|woensdag|donderdag|vrijdag|zaterdag|zondag|" +
                    @"monday|tuesday|wednesday|thursday|friday|saturday|sunday|" +
                    @"lundi|mardi|mercredi|jeudi|vendredi|samedi|dimanche)\b|" +
                    @"^\d{1,2}\s+\p{L}+$|^(19|20)\d{2}$|" +
                    @"teams-?\s?(vergadering|meeting)|réunion teams",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                break; // vanaf hier alleen nog datum/locatie-ruis
            }
            schoon.Add(deel);
        }
        return schoon.Count > 0 ? string.Join(", ", schoon) : titel;
    }

    /// <summary>
    /// Navigeert naar de agendaweek van een dag en leest de aria-labels van de afspraken. De
    /// agenda rendert traag en in stappen, dus in plaats van blind een paar seconden te wachten
    /// blijven we lezen tot de oogst twee keer na elkaar dezelfde is.
    ///
    /// Outlook negeert de datum in het pad: het stuurt door naar outlook.cloud.microsoft en zet je
    /// op de bewaarde weergave (workweek) met de laatst bekeken datum. Navigeren alleen brengt je
    /// dus nooit bij de gevraagde week. Daarom lezen we uit de labels welke week er staat en
    /// bladeren we met de vorige/volgende-knoppen van Outlook zelf naar de juiste week.
    /// </summary>
    private async Task<string> NaarAgendaAsync(DateOnly dag, CancellationToken ct)
    {
        var pad = $"/calendar/view/week/{dag.Year}/{dag.Month}/{dag.Day}";
        await NaarUrlAsync("https://outlook.office.com" + pad, ct);
        if (!await StaatDatumInUrlAsync(dag))
        {
            var href = await HuidigeUrlAsync();
            if (Uri.TryCreate(href, UriKind.Absolute, out var uri))
            {
                await NaarUrlAsync(uri.GetLeftPart(UriPartial.Authority) + pad, ct);
            }
        }

        // Outlook onthoudt de weergave "Werkweek"; die toont maandag t/m vrijdag en verzwijgt dus
        // zaterdag en zondag. Zet ze eerst op de zevendaagse weergave, anders missen we afspraken.
        var weergave = await JsAsync(WeekWeergaveScript);
        if (weergave.Contains("geklikt", StringComparison.Ordinal))
        {
            await Task.Delay(1500, ct);
        }

        var json = await ScrapeAsync(ct);
        _agendaStappen = $"weergave: {weergave} | " + BladerKnoppen(await JsAsync(KnoppenScript));

        // Bladeren tot de getoonde week de gevraagde dag bevat. Maximaal acht stappen: verder dan
        // dat willen we sowieso niet klikken, en zonder rem zou een niet-werkende knop een
        // eindeloze lus opleveren.
        for (var stap = 0; stap < 8; stap++)
        {
            var verschil = WekenVerschil(json, dag);
            if (verschil == 0)
            {
                _agendaStappen += $" | goed na {stap} stap(pen)";
                break;
            }
            var res = await JsAsync(verschil > 0 ? VolgendeScript : VorigeScript);
            _agendaStappen += $" | stap {stap + 1}: {(verschil > 0 ? "vooruit" : "terug")} → {res}";
            if (!res.Contains("geklikt", StringComparison.Ordinal))
            {
                break; // geen bruikbare knop gevonden: laten staan wat er staat
            }
            await Task.Delay(1200, ct);
            json = await ScrapeAsync(ct);
        }
        return json;
    }

    /// <summary>Diagnose over de laatste agenda-ophaling; komt mee in outlook-agenda-debug.json.</summary>
    private string _agendaStappen = "";

    private const string Scrape =
        """
        (function () {
            const labels = [...document.querySelectorAll(
                '[role="main"] [role="button"][aria-label], [data-calitem] [aria-label],' +
                '[class*="calendar"] [role="group"] [aria-label]')]
                .map(e => e.getAttribute('aria-label'))
                .filter(l => l && /\d{1,2}:\d{2}/.test(l) && l.length < 220);
            return [...new Set(labels)].slice(0, 150);
        })()
        """;

    /// <summary>Leest de labels tot de oogst twee keer na elkaar dezelfde is (= uitgerenderd).</summary>
    private async Task<string> ScrapeAsync(CancellationToken ct)
    {
        var json = "[]";
        var vorige = "";
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(750, ct);
            json = await JsAsync(Scrape);
            if (json.Length > 2 && json == vorige)
            {
                break;
            }
            vorige = json;
        }
        return json;
    }

    // De knop voor de volgende/vorige periode. Outlook labelt die per taal anders en soms met
    // title in plaats van aria-label, vandaar de brede test op beide plus de zichtbare tekst.
    private const string KnopHulp =
        """
        const zichtbaar = el => el && el.offsetParent !== null && !el.disabled;
        const tekst = el => ((el.getAttribute('aria-label') || el.getAttribute('title') ||
                              el.innerText || '') + '').trim();
        const knoppen = [...document.querySelectorAll('button, [role="button"]')].filter(zichtbaar);
        """;

    private const string VolgendeScript =
        $$"""
        (function () {
            {{KnopHulp}}
            const knop = knoppen.find(b => /^(volgende|next|suivant|nächste)\b/i.test(tekst(b)) ||
                /(volgende|next|suivant).{0,12}(week|periode|period|semaine|woche)/i.test(tekst(b)));
            if (!knop) { return 'geen-knop'; }
            knop.click();
            return 'geklikt:' + tekst(knop).slice(0, 40);
        })()
        """;

    private const string VorigeScript =
        $$"""
        (function () {
            {{KnopHulp}}
            const knop = knoppen.find(b => /^(vorige|previous|prev|précédent|vorherige)\b/i.test(tekst(b)) ||
                /(vorige|previous|précédent).{0,12}(week|periode|period|semaine|woche)/i.test(tekst(b)));
            if (!knop) { return 'geen-knop'; }
            knop.click();
            return 'geklikt:' + tekst(knop).slice(0, 40);
        })()
        """;

    // Zet de agenda op de zevendaagse weergave. De knop heet exact "Week" — bewust een strakke
    // match, want "Werkweek" (en "Work week") bevat datzelfde woord en is net wat we níét willen.
    private const string WeekWeergaveScript =
        $$"""
        (function () {
            {{KnopHulp}}
            const isWeek = t => /^(week|semaine|woche|7 dagen|7 days)$/i.test(t);
            const knop = knoppen.find(b => isWeek(tekst(b)));
            if (!knop) { return location.href.includes('/view/week') ? 'al-week' : 'geen-knop'; }
            const aan = ['aria-checked', 'aria-pressed', 'aria-selected']
                .some(a => knop.getAttribute(a) === 'true');
            if (aan) { return 'al-week'; }
            knop.click();
            return 'geklikt:' + tekst(knop).slice(0, 40);
        })()
        """;

    // Diagnose: welke knoppen staan er in de agendabalk?
    private const string KnoppenScript =
        $$"""
        (function () {
            {{KnopHulp}}
            return knoppen.map(tekst).filter(t => t.length > 0 && t.length < 40).slice(0, 25);
        })()
        """;

    private static string BladerKnoppen(string json)
    {
        try
        {
            var lijst = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return "knoppen: " + string.Join(" / ", lijst);
        }
        catch (JsonException)
        {
            return "knoppen: ?";
        }
    }

    /// <summary>
    /// Hoeveel weken zit de getoonde agenda naast de gevraagde dag? 0 = de gevraagde dag valt in
    /// de week die op het scherm staat. Zonder leesbare datums in de labels geven we ook 0 terug:
    /// dan weten we niets en is blind klikken erger dan niets doen.
    /// </summary>
    private static int WekenVerschil(string json, DateOnly dag)
    {
        static DateOnly Maandag(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

        List<DateOnly> datums;
        try
        {
            datums = (JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>())
                .Select(l => DatumUitLabel(l, dag))
                .Where(d => d.HasValue)
                .Select(d => d!.Value)
                .ToList();
        }
        catch (JsonException)
        {
            return 0;
        }
        if (datums.Count == 0)
        {
            return 0;
        }
        // De maandag van de getoonde week tegen de maandag van de gevraagde week.
        var getoond = Maandag(datums.Min());
        return (Maandag(dag).DayNumber - getoond.DayNumber) / 7;
    }

    /// <summary>Navigeert en wacht tot de agenda geladen is (of de tijd op is).</summary>
    private async Task NaarUrlAsync(string url, CancellationToken ct)
    {
        (_jsDoel ?? _web)!.CoreWebView2!.Navigate(url);
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(500, ct);
            if (await JsAsync("location.pathname.includes('/calendar')") == "true")
            {
                return;
            }
        }
    }

    /// <summary>De URL waar de sessie werkelijk op uitgekomen is, zonder aanhalingstekens.</summary>
    private async Task<string> HuidigeUrlAsync()
    {
        var ruw = await JsAsync("location.href");
        try
        {
            return JsonSerializer.Deserialize<string>(ruw) ?? ruw;
        }
        catch (JsonException)
        {
            return ruw;
        }
    }

    /// <summary>
    /// Toont de pagina de gevraagde datum nog? Outlook schrijft de maand en dag soms met en soms
    /// zonder voorloopnul, dus vergelijken we op de getallen zelf.
    /// </summary>
    private async Task<bool> StaatDatumInUrlAsync(DateOnly dag)
    {
        var href = await HuidigeUrlAsync();
        var m = System.Text.RegularExpressions.Regex.Match(href, @"/(\d{4})/(\d{1,2})/(\d{1,2})\b");
        return m.Success &&
               int.Parse(m.Groups[1].Value) == dag.Year &&
               int.Parse(m.Groups[2].Value) == dag.Month &&
               int.Parse(m.Groups[3].Value) == dag.Day;
    }

    /// <summary>
    /// Diagnose: welke pagina en welke dagen staan er werkelijk op het scherm? Zonder dit is
    /// niet te zien of Outlook de gevraagde week echt toont dan wel de vorige weergave laat
    /// staan — en dus ook niet of een lege dag echt leeg is.
    /// </summary>
    private async Task<string> WaarStaanWeAsync()
    {
        var ruw = await JsAsync(
            """
            (function () {
                const kop = document.querySelector('[role="main"] h1, [role="main"] [role="heading"]');
                const kolommen = [...document.querySelectorAll(
                    '[role="main"] [role="columnheader"], [role="main"] [role="grid"] [role="row"]:first-child *')]
                    .map(e => (e.getAttribute('aria-label') || e.textContent || '').trim())
                    .filter(t => t.length > 0 && t.length < 40);
                return location.href +
                    ' | kop: ' + (kop ? kop.textContent.trim().slice(0, 120) : '?') +
                    ' | kolommen: ' + [...new Set(kolommen)].slice(0, 12).join(' / ');
            })()
            """);
        try
        {
            return JsonSerializer.Deserialize<string>(ruw) ?? ruw;
        }
        catch (JsonException)
        {
            return ruw;
        }
    }

    /// <summary>Haalt één stuk uit een aria-label met een regex; lege string als het er niet in staat.</summary>
    private static string LabelDeel(string label, string patroon)
    {
        var m = System.Text.RegularExpressions.Regex.Match(label, patroon,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    // NL- en EN-maandnamen op dezelfde index (mod 12), zodat één lookup beide talen dekt.
    private static readonly string[] Maanden =
    {
        "januari", "februari", "maart", "april", "mei", "juni",
        "juli", "augustus", "september", "oktober", "november", "december",
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december",
    };

    /// <summary>
    /// De datum die in het aria-label van een afspraak staat ("28 Juli, 2026" of "July 28"),
    /// of null als er geen datum in staat. Ontbreekt het jaartal, dan wordt dat van
    /// <paramref name="rond"/> genomen, met een correctie over de jaarwissel heen.
    /// </summary>
    private static DateOnly? DatumUitLabel(string label, DateOnly rond)
    {
        var alternatie = string.Join('|', Maanden.Distinct());
        var m = System.Text.RegularExpressions.Regex.Match(label,
            $@"\b(\d{{1,2}})\s+({alternatie})\b,?\s*(\d{{4}})?|" +
            $@"\b({alternatie})\s+(\d{{1,2}})\b,?\s*(\d{{4}})?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }
        var dagNr = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[5].Value);
        var maandNaam = (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[4].Value).ToLowerInvariant();
        var maandNr = (Array.IndexOf(Maanden, maandNaam) % 12) + 1;
        var jaarTekst = m.Groups[3].Success ? m.Groups[3].Value
            : m.Groups[6].Success ? m.Groups[6].Value : "";
        try
        {
            if (jaarTekst.Length > 0)
            {
                return new DateOnly(int.Parse(jaarTekst), maandNr, dagNr);
            }
            // Zonder jaartal: het jaar kiezen dat het dichtst bij de gevraagde dag ligt, zodat
            // "31 december" naast "1 januari" niet elf maanden verderop belandt.
            return new[] { rond.Year - 1, rond.Year, rond.Year + 1 }
                .Select(j => new DateOnly(j, maandNr, Math.Min(dagNr, DateTime.DaysInMonth(j, maandNr))))
                .OrderBy(d => Math.Abs(d.DayNumber - rond.DayNumber))
                .First();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // "31 februari" en ander onzin uit een half gerenderd label
        }
    }

    /// <summary>
    /// Haalt de details van één CED-afspraak op (genodigden, omschrijving, organisator) door
    /// hem in de webagenda aan te klikken en het detailpaneel uit te lezen. Traag (± 10 s) en
    /// best effort — de beller cachet het resultaat per afspraak. Leeg = niet gelukt.
    /// </summary>
    public async Task<string> MeetingDetailsAsync(
        DateOnly dag, string tijd, string titelDeel, CancellationToken ct)
    {
        await _slot.WaitAsync(ct);
        try
        {
            if (!await StartAsync(ct))
            {
                throw new InvalidOperationException("Outlook is niet aangemeld.");
            }
            _jsDoel = await AgendaWebAsync(ct);
            var debug = new List<string> { $"dag={dag:yyyy-MM-dd} tijd={tijd} titel={titelDeel}" };
            try
            {
                await NaarUrlAsync(
                    $"https://outlook.office.com/calendar/view/day/{dag.Year}/{dag.Month}/{dag.Day}", ct);
                // De dagweergave rendert de blokjes pas na een paar tellen; vijf klikpogingen,
                // de laatste twee alleen op tijd (het label kort de titel soms in).
                var klik = "";
                for (var poging = 0; poging < 5; poging++)
                {
                    await Task.Delay(1500, ct);
                    klik = JsonTekst(await JsAsync(KlikAfspraakScript(tijd, poging < 3 ? titelDeel : "")));
                    debug.Add($"klik {poging + 1}: {klik}");
                    if (klik.Contains("geklikt", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
                if (!klik.Contains("geklikt", StringComparison.Ordinal))
                {
                    return "";
                }
                // Het detailpaneel (peek) uitlezen tot de tekst twee rondes stabiel is.
                var vorige = "";
                var tekst = "";
                var leegInfo = "";
                for (var i = 0; i < 12; i++)
                {
                    await Task.Delay(800, ct);
                    tekst = JsonTekst(await JsAsync(PeekScript));
                    if (tekst.StartsWith("LEEG:", StringComparison.Ordinal))
                    {
                        leegInfo = tekst; // diagnose, geen inhoud
                        tekst = "";
                        vorige = "";
                        continue;
                    }
                    if (tekst.Length > 40 && tekst == vorige)
                    {
                        break;
                    }
                    vorige = tekst;
                }
                if (tekst.Length == 0 && leegInfo.Length > 0)
                {
                    debug.Add(leegInfo);
                }
                debug.Add($"peek: {tekst.Length} tekens");
                return tekst;
            }
            finally
            {
                try
                {
                    File.WriteAllText(Path.Combine(DataDir, "outlook-peek-debug.json"),
                        JsonSerializer.Serialize(debug));
                }
                catch
                {
                    // Alleen diagnose.
                }
            }
        }
        finally
        {
            _jsDoel = null; // terug naar de hoofdpagina voor de mailfuncties
            _slot.Release();
        }
    }

    /// <summary>Klikt het afspraakblokje aan waarvan het aria-label de tijd en (een stuk van) de titel bevat.</summary>
    private static string KlikAfspraakScript(string tijd, string titelDeel) =>
        $$"""
        (function () {
            const tijd = {{JsonSerializer.Serialize(tijd)}};
            const titel = {{JsonSerializer.Serialize(titelDeel.ToLowerInvariant())}};
            // Breed zoeken: OWA wisselt nogal eens van structuur. Alles met een aria-label
            // waar een tijdsaanduiding in zit is kandidaat-afspraakblokje.
            const items = [...document.querySelectorAll('[aria-label]')]
                .filter(e => {
                    const l = (e.getAttribute('aria-label') || '').toLowerCase();
                    if (!l.includes(tijd) || !/\d{1,2}:\d{2}/.test(l)) { return false; }
                    if (titel.length >= 3 && !l.includes(titel)) { return false; }
                    const r = e.getBoundingClientRect();
                    return r.width > 20 && r.height > 8; // zichtbaar blokje, geen verborgen node
                });
            if (!items.length) { return 'niet-gevonden (' +
                document.querySelectorAll('[aria-label]').length + ' labels)'; }
            // Het kleinste passende element is het blokje zelf (ouders matchen soms ook).
            items.sort((a, b) => (a.getBoundingClientRect().width * a.getBoundingClientRect().height) -
                                 (b.getBoundingClientRect().width * b.getBoundingClientRect().height));
            // Dubbelklik opent de vólledige weergave (met deelnemerslijst); het peek-paneel
            // van een enkele klik toont die namen niet.
            items[0].click();
            items[0].dispatchEvent(new MouseEvent('dblclick',
                { bubbles: true, cancelable: true, view: window }));
            return 'geklikt:' + (items[0].getAttribute('aria-label') || '').slice(0, 60);
        })()
        """;

    // De detailweergave na de (dubbel)klik: liefst de volledige afspraakpagina (daar staat de
    // deelnemerslijst), anders de grootste zichtbare dialog/callout. Zichtbaarheid via de
    // bounding box — offsetParent is null voor position:fixed-panelen en die gebruikt OWA net.
    private const string PeekScript =
        """
        (function () {
            const zichtbaar = e => {
                const r = e.getBoundingClientRect();
                return r.width > 60 && r.height > 40;
            };
            // De Teams-deelnamelink zit in een href (de knop/link "Deelnemen"), niet in de
            // zichtbare tekst: apart meegeven zodat de cockpit een join-icoontje kan tonen.
            // Bewust in de hele pagina zoeken — OWA rendert de joinknop soms buiten het
            // peek-paneel zelf.
            const joinLink = _ => {
                const a = [...document.querySelectorAll('a[href]')]
                    .find(x => /meetup-join|teams\.live\.com\/meet/i.test(x.href || ''));
                return a ? '\nDeelnemen: ' + a.href : '';
            };
            if (location.pathname.includes('/calendar/item/')) {
                // De losse afspraakpagina heeft geen [role=main]; de body ís de afspraak
                // (de C#-kant filtert de resterende chrome er toch uit).
                const vol = document.querySelector('[role="main"]') || document.body;
                const t = (vol.innerText || '');
                if (t.length > 80) {
                    return t.slice(0, 6000) + joinLink(vol);
                }
            }
            const kandidaten = [...document.querySelectorAll(
                '[role="dialog"], .ms-Callout, [data-app-section*="Peek" i], [role="complementary"]')]
                .filter(e => zichtbaar(e) && (e.innerText || '').length > 20);
            if (!kandidaten.length) {
                // Diagnoseprotocol: "LEEG:"-prefix wordt door de beller als mislukking gelogd.
                return 'LEEG: url=' + location.pathname.slice(0, 60) +
                       ' dialogs=' + document.querySelectorAll('[role="dialog"]').length +
                       ' callouts=' + document.querySelectorAll('.ms-Callout').length +
                       ' peeks=' + document.querySelectorAll('[data-app-section*="Peek" i]').length;
            }
            const el = kandidaten.reduce((a, b) =>
                (a.innerText || '').length >= (b.innerText || '').length ? a : b);
            return (el.innerText || '').slice(0, 6000) + joinLink(el);
        })()
        """;

    /// <summary>ExecuteScriptAsync geeft een JSON-gecodeerde string terug; één keer decoderen.</summary>
    private static string JsonTekst(string ruw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(ruw) ?? "";
        }
        catch (JsonException)
        {
            return ruw.Trim('"');
        }
    }

    /// <summary>
    /// Live DOM-zelftest: kloppen de structurele ankers van de OWA-pagina nog (mappenbalk,
    /// zoekveld, maillijst)? Leeg = in orde; anders een omschrijving van wat er niet meer
    /// gevonden wordt — hét vroege signaal dat Microsoft de UI omgegooid heeft.
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
            var json = await JsAsync(
                """
                JSON.stringify({
                    mappen: document.querySelectorAll('[role="treeitem"]').length,
                    rijen: document.querySelectorAll('[data-convid], [role="option"]').length,
                    zoek: !!document.querySelector('#topSearchInput, [role="searchbox"]'),
                })
                """);
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(json) ?? json);
            var problemen = new List<string>();
            if (doc.RootElement.GetProperty("mappen").GetInt32() == 0)
            {
                problemen.Add("mappenbalk-selector vindt niets");
            }
            if (!doc.RootElement.GetProperty("zoek").GetBoolean())
            {
                problemen.Add("zoekveld-selector vindt niets");
            }
            return problemen.Count > 0 ? "Outlook: " + string.Join(", ", problemen) : "";
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Het agenda-tabblad: een tweede WebView2 in hetzelfde profiel (zelfde cookies, dus
    /// geen extra MFA) voor agenda en afspraakdetails. De hoofdpagina blijft daardoor
    /// permanent op Postvak IN staan — de weg-en-terug-navigaties die de mailpoll en
    /// archiveeracties konden breken, zijn hiermee weg.
    /// </summary>
    private async Task<WebView2> AgendaWebAsync(CancellationToken ct)
    {
        if (_webAgenda?.CoreWebView2 is not null)
        {
            return _webAgenda;
        }
        if (_venster is null || _env is null)
        {
            throw new InvalidOperationException("Outlook-sessie is niet gestart.");
        }
        var web = new WebView2
        {
            // Vast groot vlak (het venster staat toch buiten beeld): genoeg ruimte om de
            // weekagenda volledig te renderen.
            Location = new Point(0, 0),
            Size = new Size(1400, 1500),
        };
        _venster.Controls.Add(web);
        var init = web.EnsureCoreWebView2Async(_env);
        if (await Task.WhenAny(init, Task.Delay(TimeSpan.FromSeconds(20), ct)) != init)
        {
            throw new InvalidOperationException("Het agenda-tabblad start niet op.");
        }
        await init;
        // Ook dit tabblad stil houden: OWA kan herinneringsgeluiden afspelen.
        web.CoreWebView2!.IsMuted = true;
        web.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            web.CoreWebView2.Navigate(e.Uri);
        };
        web.CoreWebView2.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
            {
                _gecrasht = true; // browserproces weg: hele sessie vers opbouwen
            }
        };
        _webAgenda = web;
        return web;
    }

    private async Task<string> JsAsync(string script)
    {
        // Binnen agenda-operaties wijst _jsDoel naar het agenda-tabblad; daarbuiten
        // werkt alles op de hoofdpagina (Postvak IN).
        if ((_jsDoel ?? _web)?.CoreWebView2 is not { } core)
        {
            throw new InvalidOperationException("Outlook-sessie is niet gestart.");
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
                "De Outlook-browser is gecrasht en wordt bij de volgende synchronisatie " +
                "automatisch opnieuw gestart.", ex);
        }
    }

    public void Dispose()
    {
        _web?.Dispose();
        _webAgenda?.Dispose();
        _venster?.Dispose();
    }
}
